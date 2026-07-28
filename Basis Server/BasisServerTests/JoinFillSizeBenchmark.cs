using Basis.Network.Core;
using Xunit;
using Xunit.Abstractions;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Sizes the join fill — what a client receives about everyone already present — at a 2000-player
/// scale. Run it with <c>--logger "console;verbosity=detailed"</c> to see the numbers.
///
/// It exists because the intuitive answer here is wrong. Batching and compressing the fill looks like
/// a ~20x win if you measure with empty pose buffers, but a real pose is quantized bone rotations,
/// which is close to incompressible, and the pose is roughly 70% of every record. The honest figure
/// for a realistic crowd is nearer 3x, and the remaining bulk can only be removed by sending less
/// pose (distance-tiering the join snapshot the way the steady-state reduction system already does),
/// not by compressing it harder. The three pose modes below bracket that.
/// </summary>
public class JoinFillSizeBenchmark
{
    private readonly ITestOutputHelper _out;
    public JoinFillSizeBenchmark(ITestOutputHelper o) => _out = o;

    const int Players = 2000;
    const int DistinctAvatars = 54;

    static readonly Dictionary<int, byte[]> IdlePoses = new();

    /// <summary>
    /// zeros  — floor: unrealistic, every byte identical.
    /// idle   — realistic: a crowd sharing a handful of resting poses with small per-player jitter.
    /// random — ceiling: every player mid-motion, quantized rotations with no shared structure.
    /// </summary>
    static byte[] Pose(string kind, Random rng, int player)
    {
        int n = Wire.PayloadSize(BitQuality.High);
        if (kind == "zeros") return new byte[n];
        if (kind == "random")
        {
            byte[] b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        int slot = player % 20;
        if (!IdlePoses.TryGetValue(slot, out byte[]? basePose))
        {
            basePose = new byte[n];
            new Random(1000 + slot).NextBytes(basePose);
            IdlePoses[slot] = basePose;
        }
        byte[] copy = (byte[])basePose.Clone();
        for (int k = 0; k < 8; k++) copy[rng.Next(n)] = (byte)rng.Next(256);
        return copy;
    }

    /// <summary>
    /// Mirrors BasisAvatarNetworkLoad.EncodeToBytes: two strings, then raw Deflate on that one record.
    /// Per-record compression is why the blob barely shrinks — the redundancy is across players.
    /// </summary>
    static byte[] AvatarBlob(int avatarIndex)
    {
        string url = $"https://BasisFramework.b-cdn.net/Avatars/BEE/BEE/avatar{avatarIndex}/{avatarIndex:x8}20251003.BEE";
        string pw = new string((char)('a' + (avatarIndex % 6)), 64);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            byte[] u = System.Text.Encoding.UTF8.GetBytes(url);
            byte[] p = System.Text.Encoding.UTF8.GetBytes(pw);
            w.Write((ushort)u.Length); w.Write(u);
            w.Write((ushort)p.Length); w.Write(p);
        }
        byte[] raw = ms.ToArray();

        using var deflated = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(deflated, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            d.Write(raw, 0, raw.Length);
        }
        return deflated.ToArray();
    }

    [Theory]
    [InlineData("zeros")]
    [InlineData("idle")]
    [InlineData("random")]
    public void JoinFill_BatchingBeatsPerPacket(string poseKind)
    {
        var rng = new Random(7);
        var all = new NetDataWriter();
        long perPacketBytes = 0;

        for (int i = 0; i < Players; i++)
        {
            // Skewed avatar popularity, like a real public instance: a few avatars dominate.
            int avatar = Math.Min((int)Math.Floor(Math.Pow(rng.NextDouble(), 3) * DistinctAvatars), DistinctAvatars - 1);

            var srm = new ServerReadyMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = (ushort)i },
                localReadyMessage = new ReadyMessage
                {
                    playerMetaDataMessage = new ClientMetaDataMessage
                    {
                        playerUUID = (76561198000000000L + i).ToString(),
                        playerDisplayName = $"Player{i}",
                        playerPlatform = i % 3 == 0 ? "Android" : "WindowsPlayer",
                    },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        loadMode = 1,
                        byteArray = AvatarBlob(avatar),
                        LocalAvatarIndex = (byte)i,
                    },
                    localAvatarSyncMessage = new LocalAvatarSyncMessage
                    {
                        DataQualityLevel = (byte)BitQuality.High,
                        array = Pose(poseKind, rng, i),
                    },
                },
            };

            var one = new NetDataWriter();
            srm.Serialize(one);
            perPacketBytes += one.Length;
            srm.Serialize(all);
        }

        byte[] payload = all.CopyData();
        long batchedBytes = 0;
        int offset = 0, packets = 0;
        while (offset < payload.Length)
        {
            int chunk = Math.Min(ServerReadyBatchMessage.MaxPayloadBytes, payload.Length - offset);
            byte[] slice = new byte[chunk];
            Array.Copy(payload, offset, slice, 0, chunk);

            var batch = new ServerReadyBatchMessage { Count = 1, Payload = slice };
            var bw = new NetDataWriter();
            batch.Serialize(bw);
            batchedBytes += bw.Length;
            offset += chunk;
            packets++;
        }

        _out.WriteLine($"pose={poseKind,-7} per-packet {perPacketBytes / 1024.0,8:F1} KB in {Players} packets");
        _out.WriteLine($"pose={poseKind,-7} batched    {batchedBytes / 1024.0,8:F1} KB in {packets} packets"
                       + $"   ({(double)perPacketBytes / batchedBytes:F2}x bytes, {(double)Players / packets:F0}x fewer packets)");

        // Bytes: guaranteed only in the loose sense, since an all-random crowd barely compresses.
        Assert.True(batchedBytes < perPacketBytes,
            $"batching must never be larger: {batchedBytes} vs {perPacketBytes}");

        // Packet count is the robust win and holds regardless of how compressible the poses are —
        // it is what stops a joiner being buried under ~2000 reliable sends.
        Assert.True(packets * 10 < Players,
            $"expected at least a 10x packet reduction, got {Players} -> {packets}");
    }
}
