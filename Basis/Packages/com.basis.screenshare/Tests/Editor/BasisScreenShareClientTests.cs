using System;
using System.Collections.Generic;
using Basis.Network.Core;
using NUnit.Framework;
using UnityEngine;
using static Basis.Network.Core.BasisScreenShareProtocol;

public class BasisScreenShareClientTests
{
    static BasisScreenShareClient.Remote Watching() => new BasisScreenShareClient.Remote { Sharer = 7, Watching = true, Route = BasisScreenShareClient.RouteServer };

    static void Feed(BasisScreenShareClient.Remote remote, uint sequence, bool key, int length, int skipChunk = -1)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(sequence + i);
        int count = ChunkCount(length);
        for (int i = 0; i < count; i++)
        {
            if (i == skipChunk) continue;
            int offset = i * MaxChunkPayload, size = Math.Min(MaxChunkPayload, length - offset);
            ChunkHeader header = new ChunkHeader { Sequence = sequence, Kind = KindVideo, Flags = key ? FlagKey : (byte)0, Index = (ushort)i, Count = (ushort)count, PtsUs = sequence * 1000L, TotalLength = length };
            BasisScreenShareClient.Accept(remote, header, new ReadOnlySpan<byte>(data, offset, size));
        }
    }

    struct Captured
    {
        public byte Kind, Last;
        public int Length;
        public long PtsUs;
        public bool Key;
    }

    readonly List<Captured> sunk = new List<Captured>();
    BasisScreenShareClient.FrameSink original;

    [SetUp]
    public void UseCapturingSink()
    {
        original = BasisScreenShareClient.Sink;
        sunk.Clear();
        BasisScreenShareClient.Sink = (remote, data, pts, key, kind) =>
        {
            sunk.Add(new Captured { Kind = kind, Length = data.Length, PtsUs = pts, Key = key, Last = data.Length > 0 ? data[data.Length - 1] : (byte)0 });
            return 0;
        };
    }

    [TearDown]
    public void RestoreSink() => BasisScreenShareClient.Sink = original;

    List<Captured> Drain(BasisScreenShareClient.Remote remote)
    {
        List<Captured> frames = new List<Captured>(sunk);
        sunk.Clear();
        return frames;
    }

    [Test]
    public void ReassemblesMultiChunkFramesInOrder()
    {
        BasisScreenShareClient.Remote remote = Watching();
        Feed(remote, 1, true, 3000);
        Feed(remote, 2, false, 50);
        List<Captured> frames = Drain(remote);
        Assert.AreEqual(2, frames.Count);
        Assert.AreEqual(3000, frames[0].Length);
        Assert.IsTrue(frames[0].Key);
        Assert.AreEqual(unchecked((byte)(1 + 2999)), frames[0].Last);
        Assert.AreEqual(2000, frames[1].PtsUs);
        Assert.IsFalse(remote.NeedKeyframe);
    }

    [Test]
    public void DeltasBeforeTheFirstKeyframeAreDropped()
    {
        BasisScreenShareClient.Remote remote = Watching();
        Feed(remote, 5, false, 100);
        Assert.AreEqual(0, Drain(remote).Count);
        Assert.IsTrue(remote.NeedKeyframe);
        Feed(remote, 6, true, 100);
        Assert.AreEqual(1, Drain(remote).Count);
    }

    [Test]
    public void AMissingChunkHoldsVideoUntilTheNextKeyframe()
    {
        BasisScreenShareClient.Remote remote = Watching();
        Feed(remote, 1, true, 100);
        Feed(remote, 2, false, 2500, skipChunk: 1);
        Feed(remote, 3, false, 100);
        Assert.AreEqual(1, Drain(remote).Count);
        Assert.IsTrue(remote.NeedKeyframe);
        Feed(remote, 4, true, 100);
        Assert.AreEqual(1, Drain(remote).Count);
        Assert.IsFalse(remote.NeedKeyframe);
    }

    [Test]
    public void ASequenceGapNeedsAKeyframe()
    {
        BasisScreenShareClient.Remote remote = Watching();
        Feed(remote, 1, true, 100);
        Feed(remote, 3, false, 100);
        Assert.AreEqual(1, Drain(remote).Count);
        Assert.IsTrue(remote.NeedKeyframe);
    }

    [Test]
    public void NothingQueuesWhileNotWatching()
    {
        BasisScreenShareClient.Remote remote = Watching();
        remote.Watching = false;
        Feed(remote, 1, true, 100);
        Assert.AreEqual(0, Drain(remote).Count);
    }

    [Test]
    public void HidingStopsWatchingAndDropsEverythingQueued()
    {
        BasisScreenShareClient.Remote remote = Watching();
        Feed(remote, 1, true, 100);
        BasisScreenShareClient.Hide(remote);
        Assert.IsTrue(remote.Hidden);
        Assert.IsFalse(remote.Watching);
        Assert.AreEqual(0, Drain(remote).Count);
        Feed(remote, 2, true, 100);
        Assert.AreEqual(0, Drain(remote).Count);
    }

    [Test]
    public void AudioPacketsQueueWithTheirTimestamps()
    {
        BasisScreenShareClient.Remote remote = Watching();
        ChunkHeader header = new ChunkHeader { Sequence = 0, Kind = KindAudio, Flags = FlagKey, Index = 0, Count = 1, PtsUs = 20000, TotalLength = 3 };
        BasisScreenShareClient.Accept(remote, header, new byte[] { 1, 2, 3 });
        List<Captured> frames = Drain(remote);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(KindAudio, frames[0].Kind);
        Assert.AreEqual(20000, frames[0].PtsUs);
    }

    [Test]
    public void SizePresetsKeepTheSourceShape()
    {
        Assert.AreEqual(new Vector2Int(1920, 1080), BasisScreenShareSettings.MaxSize(BasisScreenShareSettings.Resolution1080, new Vector2Int(3840, 2160)));
        Assert.AreEqual(new Vector2Int(2580, 1080), BasisScreenShareSettings.MaxSize(BasisScreenShareSettings.Resolution1080, new Vector2Int(3440, 1440)));
        Assert.AreEqual(new Vector2Int(3840, 2160), BasisScreenShareSettings.MaxSize(BasisScreenShareSettings.ResolutionNative, new Vector2Int(3840, 2160)));
        Assert.AreEqual(new Vector2Int(854, 480), BasisScreenShareSettings.MaxSize(BasisScreenShareSettings.Resolution480, new Vector2Int(1920, 1080)));
    }

    [Test]
    public void BitratePresetsScaleAndRespectTheServerCap()
    {
        Vector2Int size = new Vector2Int(1920, 1080);
        int auto = BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityAuto, size, 30, 12000);
        Assert.AreEqual(4977, auto);
        Assert.Less(BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityLow, size, 30, 12000), auto);
        Assert.Greater(BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityHigh, size, 30, 12000), auto);
        Assert.AreEqual(12000, BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityMax, size, 60, 12000));
        Assert.AreEqual(3000, BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityAuto, size, 60, 3000));
        Assert.AreEqual(BasisScreenShareSettings.MinBitrateKbps, BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.QualityLow, new Vector2Int(320, 180), 5, 12000));
    }

    [Test]
    public void CaptureDescriptorIsEscapedJson()
    {
        string json = BasisScreenShareNative.Describe(new BasisScreenShareNative.Config { SourceId = "window:\"1\"\\x\t", MaxWidth = 1920, MaxHeight = 1080, Fps = 30, BitrateKbps = 4000, KeyframeIntervalMs = 10000, AudioBitrateKbps = 128, Cursor = true, Audio = false });
        StringAssert.Contains("\"source_id\":\"window:\\\"1\\\"\\\\x\\u0009\"", json);
        StringAssert.Contains("\"audio\":false", json);
        StringAssert.Contains("\"cursor\":true", json);
        StringAssert.Contains("\"fps\":30", json);
    }
}
