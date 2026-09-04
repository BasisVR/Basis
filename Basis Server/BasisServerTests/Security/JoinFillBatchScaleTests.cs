using Basis.Network.Core;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// The inflation cap on the join fill has to admit what the join fill actually produces.
///
/// BasisServerHandleEvents builds a batch by appending one serialized ServerReadyMessage at a
/// time and flushing once the buffer has REACHED the cap, so an emitted payload is always
/// MaxPayloadBytes plus however much the record that crossed the line added. The oldest-first
/// prefix in JoinBroadcast.Flush has the same shape, and additionally always takes at least one
/// record so a large single record can never wedge the queue.
///
/// A receiver that refuses anything over MaxPayloadBytes therefore rejects every full batch a
/// 2000-player join fill sends, and the joining client spawns nobody. These tests pin the
/// producer's real worst case against the ceiling the receiver enforces.
/// </summary>
public class JoinFillBatchScaleTests
{
    private static NetDataReader Reader(NetDataWriter w) => new NetDataReader(w.CopyData());

    /// <summary>One spawn record of a plausible size: a wearer on a CDN url with a normal name.</summary>
    private static ServerReadyMessage Record(ushort playerId)
    {
        var avatar = new BasisAvatarNetworkLoadStub
        {
            Url = $"https://content.example.invalid/bees/{playerId:D5}/avatar.bee",
            Password = "0123456789abcdef0123456789abcdef",
        };
        return new ServerReadyMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = playerId },
            localReadyMessage = new ReadyMessage
            {
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerUUID = $"did:key:z6Mk{playerId:D5}AbCdEfGhIjKlMnOpQrStUvWxYz012345",
                    playerDisplayName = $"Player {playerId:D5}",
                },
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    loadMode = 0,
                    byteArray = avatar.Encode(),
                    LocalAvatarIndex = 0,
                    ArmScale = 1f,
                    LegScale = 1f,
                    TorsoScale = 1f,
                },
                localAvatarSyncMessage = new LocalAvatarSyncMessage
                {
                    DataQualityLevel = 0,
                    array = new byte[Basis.Network.Core.Compression.BasisAvatarBitPacking.ConvertToSize(
                        Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality.VeryLow)],
                },
            },
        };
    }

    /// <summary>Stands in for the client-side BasisAvatarNetworkLoad blob, which the server stores opaquely.</summary>
    private struct BasisAvatarNetworkLoadStub
    {
        public string Url;
        public string Password;

        public byte[] Encode()
        {
            using var raw = new MemoryStream();
            using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
            {
                WriteString(writer, Url);
                WriteString(writer, Password);
                WriteString(writer, string.Empty);
            }
            byte[] flat = raw.ToArray();
            using var compressed = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                deflate.Write(flat, 0, flat.Length);
            }
            return compressed.ToArray();
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
    }

    /// <summary>
    /// Mirrors the producer loop in BasisServerHandleEvents: serialize, then flush once the
    /// buffer has reached the cap. Returns the payloads exactly as they go on the wire.
    /// </summary>
    private static List<(ushort count, byte[] payload)> BuildJoinFill(int playerCount)
    {
        var batches = new List<(ushort, byte[])>();
        var buffer = new NetDataWriter();
        ushort batched = 0;
        for (ushort i = 1; i <= playerCount; i++)
        {
            Record(i).Serialize(buffer);
            batched++;
            if (buffer.Length >= ServerReadyBatchMessage.MaxPayloadBytes)
            {
                batches.Add((batched, buffer.CopyData()));
                buffer.Reset();
                batched = 0;
            }
        }
        if (batched != 0)
        {
            batches.Add((batched, buffer.CopyData()));
        }
        return batches;
    }

    [Fact]
    public void JoinFill_ProducerEmitsBatchesOverTheNominalCap()
    {
        // Not an aspiration — this is what the append-then-check loop does today, and it is why
        // the receiver ceiling cannot be MaxPayloadBytes exactly.
        var batches = BuildJoinFill(2000);
        Assert.True(batches.Count > 1, "2000 players must span several batches");

        int full = 0;
        foreach (var (_, payload) in batches)
        {
            if (payload.Length < ServerReadyBatchMessage.MaxPayloadBytes) continue;
            full++;
            Assert.True(payload.Length > ServerReadyBatchMessage.MaxPayloadBytes,
                "a flushed batch always overshoots the cap by the record that crossed it");
        }
        Assert.True(full > 0, "a 2000-player fill must produce at least one full batch");
    }

    [Fact]
    public void JoinFill_TwoThousandPlayers_EveryBatchRoundTrips()
    {
        var batches = BuildJoinFill(2000);

        int decodedRecords = 0;
        foreach (var (count, payload) in batches)
        {
            var w = new NetDataWriter();
            var sent = new ServerReadyBatchMessage { Count = count, Payload = payload };
            sent.Serialize(w);

            var received = default(ServerReadyBatchMessage);
            received.Deserialize(Reader(w));

            Assert.Equal(count, received.Count);
            Assert.Equal(payload, received.Payload);

            var reader = new NetDataReader(received.Payload);
            for (int i = 0; i < received.Count; i++)
            {
                var srm = default(ServerReadyMessage);
                srm.Deserialize(reader);
                decodedRecords++;
            }
        }

        Assert.Equal(2000, decodedRecords);
    }

    [Fact]
    public void JoinFill_WorstCaseBatch_FitsUnderTheInflationCeiling()
    {
        // The ceiling has to cover the largest batch the producers can emit: a full
        // MaxPayloadBytes prefix plus the single biggest record the protocol can express —
        // a maxed avatar blob and a maxed additional-data section. If this ever stops
        // fitting, MaxInflatedBytes is too small and joins break for whoever wears it.
        var worst = Record(1);
        worst.localReadyMessage.clientAvatarChangeMessage.byteArray = new byte[ushort.MaxValue];

        var additional = new AdditionalAvatarData[byte.MaxValue];
        for (int i = 0; i < additional.Length; i++)
        {
            additional[i] = new AdditionalAvatarData { messageIndex = (byte)i, array = new byte[byte.MaxValue] };
        }
        worst.localReadyMessage.localAvatarSyncMessage.AdditionalAvatarDatas = additional;

        var recordWriter = new NetDataWriter();
        worst.Serialize(recordWriter);
        int worstRecordBytes = recordWriter.Length;

        // Site 1 (join fill): flushes after appending, so cap + one record.
        // Site 2 (JoinBroadcast.Flush): prefix is bounded by max(one record, cap).
        long worstBatch = ServerReadyBatchMessage.MaxPayloadBytes + (long)worstRecordBytes;
        Assert.True(worstBatch <= ServerReadyBatchMessage.MaxInflatedBytes,
            $"worst batch {worstBatch} must fit MaxInflatedBytes {ServerReadyBatchMessage.MaxInflatedBytes}");
    }

    [Fact]
    public void JoinFill_SingleRecordLargerThanTheNominalCap_StillRoundTrips()
    {
        // JoinBroadcast.Flush always takes at least one record so the queue cannot wedge, so a
        // wearer whose avatar blob is unusually large produces a batch of one oversized record.
        // Refusing it would mean that player never spawns for anyone.
        var big = Record(1);
        big.localReadyMessage.clientAvatarChangeMessage.byteArray = new byte[48 * 1024];

        var payloadWriter = new NetDataWriter();
        big.Serialize(payloadWriter);
        byte[] payload = payloadWriter.CopyData();
        Assert.True(payload.Length > ServerReadyBatchMessage.MaxPayloadBytes);

        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 1, Payload = payload }.Serialize(w);

        var received = default(ServerReadyBatchMessage);
        received.Deserialize(Reader(w));
        Assert.Equal((ushort)1, received.Count);
        Assert.Equal(payload, received.Payload);
    }
}
