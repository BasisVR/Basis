using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class BasisMediaClipProtocolTests
{
    static BasisMediaClipOffer SampleOffer() => new BasisMediaClipOffer
    {
        Id = Guid.NewGuid(),
        TotalBytes = 3 * BasisMediaClipProtocol.ChunkBytes + 17,
        Container = BasisMediaClipContainer.WebM,
        SharerProtected = true,
        Position = new Vector3(1.5f, -2f, 30f),
        Rotation = new Quaternion(0.1f, 0.6f, -0.05f, 0.79f).normalized,
        Scale = 1.75f,
        Name = "holiday clip",
    };

    [Test]
    public void Offer_round_trips()
    {
        BasisMediaClipOffer offer = SampleOffer();
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out BasisMediaClipOffer decoded, out string error), error);
        Assert.AreEqual(offer.Id, decoded.Id);
        Assert.AreEqual(offer.TotalBytes, decoded.TotalBytes);
        Assert.AreEqual(offer.Container, decoded.Container);
        Assert.IsTrue(decoded.SharerProtected);
        Assert.AreEqual(offer.Position, decoded.Position);
        Assert.Less(Quaternion.Angle(offer.Rotation, decoded.Rotation), 0.01f);
        Assert.AreEqual(offer.Scale, decoded.Scale, 1e-6f);
        Assert.AreEqual(offer.Name, decoded.Name);
    }

    [Test]
    public void Offer_flags_round_trip_and_unknown_bits_are_refused()
    {
        BasisMediaClipOffer offer = SampleOffer();
        offer.SharerProtected = false;
        offer.SharerBypassesLock = true;
        byte[] encoded = BasisMediaClipProtocol.EncodeOffer(offer);
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodeOffer(encoded, out BasisMediaClipOffer decoded, out string error), error);
        Assert.IsFalse(decoded.SharerProtected);
        Assert.IsTrue(decoded.SharerBypassesLock);
        int flagsOffset = 1 + BasisMediaClipProtocol.GuidBytes + sizeof(int) + 1;
        Assert.AreEqual(BasisMediaClipProtocol.FlagLockBypass, encoded[flagsOffset]);
        encoded[flagsOffset] |= 0x80;
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(encoded, out _, out _));
    }

    [Test]
    public void Clip_file_takes_chunks_out_of_order_and_reads_them_back()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "basis-clip-test-" + Guid.NewGuid().ToString("N") + ".part");
        int total = 2 * BasisMediaClipProtocol.ChunkBytes + 100;
        var expected = new byte[total];
        new System.Random(7).NextBytes(expected);
        try
        {
            using (BasisMediaClipFile file = BasisMediaClipFile.CreateForWrite(path, total))
            {
                for (int index = BasisMediaClipProtocol.ChunkCount(total) - 1; index >= 0; index--)
                {
                    int length = BasisMediaClipProtocol.ChunkLength(total, index);
                    var packet = new byte[length + 3];
                    Buffer.BlockCopy(expected, index * BasisMediaClipProtocol.ChunkBytes, packet, 3, length);
                    file.Write((long)index * BasisMediaClipProtocol.ChunkBytes, packet, 3, length);
                }
            }
            using (BasisMediaClipFile file = BasisMediaClipFile.OpenForRead(path))
            {
                Assert.AreEqual(total, file.Length);
                var actual = new byte[total];
                file.Read(0, actual, 0, total);
                CollectionAssert.AreEqual(expected, actual);
                var tail = new byte[50];
                file.Read(total - 50, tail, 0, 50);
                CollectionAssert.AreEqual(expected.AsSpan(total - 50).ToArray(), tail);
            }
            CollectionAssert.AreEqual(expected, System.IO.File.ReadAllBytes(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public void Times_format_as_minutes_or_hours()
    {
        Assert.AreEqual("0:00", BasisMediaClipUI.FormatTime(0d));
        Assert.AreEqual("0:00", BasisMediaClipUI.FormatTime(double.NaN));
        Assert.AreEqual("1:05", BasisMediaClipUI.FormatTime(65.9d));
        Assert.AreEqual("1:01:01", BasisMediaClipUI.FormatTime(3661d));
    }

    [Test]
    public void Offer_rejects_sizes_over_the_cap_and_unknown_containers()
    {
        BasisMediaClipOffer offer = SampleOffer();
        offer.TotalBytes = BasisMediaClipProtocol.MaxClipBytes + 1;
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out _, out _));
        offer = SampleOffer();
        offer.TotalBytes = 0;
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out _, out _));
        offer = SampleOffer();
        offer.Container = (BasisMediaClipContainer)42;
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out _, out _));
    }

    [Test]
    public void Offer_rejects_truncation_and_trailing_bytes()
    {
        byte[] encoded = BasisMediaClipProtocol.EncodeOffer(SampleOffer());
        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(encoded.AsSpan(0, length).ToArray(), out _, out _), $"length {length}");
        }
        byte[] longer = new byte[encoded.Length + 1];
        Buffer.BlockCopy(encoded, 0, longer, 0, encoded.Length);
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(longer, out _, out _));
    }

    [Test]
    public void Offer_rejects_non_finite_and_degenerate_poses()
    {
        BasisMediaClipOffer offer = SampleOffer();
        offer.Position = new Vector3(float.NaN, 0f, 0f);
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out _, out _));
        offer = SampleOffer();
        offer.Rotation = new Quaternion(0f, 0f, 0f, 0f);
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out _, out _));
        offer = SampleOffer();
        offer.Scale = 1000f;
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodeOffer(BasisMediaClipProtocol.EncodeOffer(offer), out BasisMediaClipOffer clamped, out _));
        Assert.AreEqual(BasisMediaClipProtocol.MaxScale, clamped.Scale);
    }

    [Test]
    public void Names_lose_markup_and_control_characters_and_fit_the_byte_cap()
    {
        Assert.AreEqual("\u2039b\u203Ahi", BasisMediaClipProtocol.SanitizeName("<b>\u202Ehi\u0007"));
        string sanitized = BasisMediaClipProtocol.SanitizeName(new string('\u00E9', 500));
        Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(sanitized), BasisMediaClipProtocol.MaxNameBytes);
        string emoji = BasisMediaClipProtocol.SanitizeName(string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 100)));
        Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(emoji), BasisMediaClipProtocol.MaxNameBytes);
        Assert.IsFalse(char.IsHighSurrogate(emoji[emoji.Length - 1]));
    }

    [Test]
    public void Chunks_cover_the_file_exactly()
    {
        int total = 5 * BasisMediaClipProtocol.ChunkBytes + 1;
        Assert.AreEqual(6, BasisMediaClipProtocol.ChunkCount(total));
        Assert.AreEqual(BasisMediaClipProtocol.ChunkBytes, BasisMediaClipProtocol.ChunkLength(total, 0));
        Assert.AreEqual(1, BasisMediaClipProtocol.ChunkLength(total, 5));
        Assert.AreEqual(0, BasisMediaClipProtocol.ChunkLength(total, 6));
        Assert.AreEqual(0, BasisMediaClipProtocol.ChunkLength(total, -1));
        Assert.AreEqual(1, BasisMediaClipProtocol.ChunkCount(BasisMediaClipProtocol.ChunkBytes));
        Assert.AreEqual(0, BasisMediaClipProtocol.ChunkCount(0));
    }

    [Test]
    public void Chunk_header_round_trips()
    {
        Guid id = Guid.NewGuid();
        byte[] data = new byte[100];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        byte[] packet = BasisMediaClipProtocol.EncodeChunk(id, 7, data, 10, 50);
        Assert.IsTrue(BasisMediaClipProtocol.TryReadChunkHeader(packet, out Guid readId, out int index, out int length));
        Assert.AreEqual(id, readId);
        Assert.AreEqual(7, index);
        Assert.AreEqual(50, length);
        Assert.AreEqual(10, packet[BasisMediaClipProtocol.ChunkHeaderBytes]);
        Assert.IsFalse(BasisMediaClipProtocol.TryReadChunkHeader(packet.AsSpan(0, BasisMediaClipProtocol.ChunkHeaderBytes).ToArray(), out _, out _, out _));
    }

    [Test]
    public void Need_round_trips_and_caps_its_count()
    {
        Guid id = Guid.NewGuid();
        var wanted = new List<int>();
        for (int i = 0; i < BasisMediaClipProtocol.MaxNeedIndices + 50; i++) wanted.Add(i * 3);
        var decoded = new List<int>();
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodeNeed(BasisMediaClipProtocol.EncodeNeed(id, wanted), out Guid readId, decoded));
        Assert.AreEqual(id, readId);
        Assert.AreEqual(BasisMediaClipProtocol.MaxNeedIndices, decoded.Count);
        Assert.AreEqual(wanted[10], decoded[10]);
        byte[] forged = BasisMediaClipProtocol.EncodeNeed(id, new[] { 1, 2 });
        forged[1 + BasisMediaClipProtocol.GuidBytes] = 0xFF;
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeNeed(forged, out _, decoded));
    }

    [Test]
    public void Pose_and_remove_round_trip()
    {
        Guid id = Guid.NewGuid();
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodePose(BasisMediaClipProtocol.EncodePose(id, new Vector3(1f, 2f, 3f), Quaternion.identity, 2f), out Guid poseId, out Vector3 position, out Quaternion rotation, out float scale));
        Assert.AreEqual(id, poseId);
        Assert.AreEqual(new Vector3(1f, 2f, 3f), position);
        Assert.AreEqual(2f, scale);
        Assert.IsTrue(BasisMediaClipProtocol.TryDecodeId(BasisMediaClipProtocol.EncodeRemove(id), BasisMediaClipProtocol.OpRemove, out Guid removeId));
        Assert.AreEqual(id, removeId);
        Assert.IsFalse(BasisMediaClipProtocol.TryDecodeId(BasisMediaClipProtocol.EncodeRemove(id), BasisMediaClipProtocol.OpPose, out _));
    }

    [Test]
    public void Urls_round_trip_and_reject_other_text()
    {
        Guid id = Guid.NewGuid();
        string url = BasisMediaClipProtocol.ToUrl(id);
        Assert.IsTrue(BasisMediaClipProtocol.TryParseUrl(url, out Guid parsed));
        Assert.AreEqual(id, parsed);
        Assert.IsFalse(BasisMediaClipProtocol.TryParseUrl("basisclip://not-a-guid", out _));
        Assert.IsFalse(BasisMediaClipProtocol.TryParseUrl("https://example.com/a.mp4", out _));
        Assert.IsFalse(BasisMediaClipProtocol.TryParseUrl(BasisMediaClipProtocol.ToUrl(Guid.Empty), out _));
        Assert.IsFalse(BasisMediaUrlRouter.IsDirectlyPlayable(url));
    }

    [Test]
    public void Containers_follow_extensions_and_signatures()
    {
        Assert.IsTrue(BasisMediaClipProtocol.TryGetContainer(@"C:\clips\a.MP4", out BasisMediaClipContainer mp4));
        Assert.AreEqual(BasisMediaClipContainer.Mp4, mp4);
        Assert.IsTrue(BasisMediaClipProtocol.TryGetContainer("b.m4v", out mp4));
        Assert.AreEqual(BasisMediaClipContainer.Mp4, mp4);
        Assert.IsTrue(BasisMediaClipProtocol.TryGetContainer("c.webm", out BasisMediaClipContainer webm));
        Assert.AreEqual(BasisMediaClipContainer.WebM, webm);
        Assert.IsFalse(BasisMediaClipProtocol.TryGetContainer("d.gif", out _));
        Assert.IsFalse(BasisMediaClipProtocol.TryGetContainer("noextension", out _));

        byte[] ftyp = { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
        byte[] ebml = { 0x1A, 0x45, 0xDF, 0xA3, 0x9F };
        Assert.IsTrue(BasisMediaClipProtocol.SignatureMatches(ftyp, BasisMediaClipContainer.Mp4));
        Assert.IsTrue(BasisMediaClipProtocol.SignatureMatches(ftyp, BasisMediaClipContainer.Mov));
        Assert.IsFalse(BasisMediaClipProtocol.SignatureMatches(ftyp, BasisMediaClipContainer.WebM));
        Assert.IsTrue(BasisMediaClipProtocol.SignatureMatches(ebml, BasisMediaClipContainer.Mkv));
        Assert.IsFalse(BasisMediaClipProtocol.SignatureMatches(ebml, BasisMediaClipContainer.Mp4));
        Assert.IsFalse(BasisMediaClipProtocol.SignatureMatches(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0 }, BasisMediaClipContainer.Mp4));
    }
}
