using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class BasisMediaPlaylistProtocolTests
{
    static BasisMediaPlaylistState SampleState(int current) => new BasisMediaPlaylistState
    {
        Id = Guid.NewGuid(),
        Revision = 4000000000u,
        Position = new Vector3(3f, 1.25f, -8f),
        Rotation = new Quaternion(0f, 0.38f, 0f, 0.92f).normalized,
        Scale = 1.5f,
        Repeat = true,
        Current = current,
    };

    static List<Guid> Entries(int count)
    {
        var list = new List<Guid>(count);
        for (int i = 0; i < count; i++) list.Add(Guid.NewGuid());
        return list;
    }

    [Test]
    public void State_round_trips_at_full_size()
    {
        List<Guid> entries = Entries(BasisMediaPlaylistProtocol.MaxEntries);
        BasisMediaPlaylistState state = SampleState(517);
        var decoded = new List<Guid>();
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodeState(BasisMediaPlaylistProtocol.EncodeState(state, entries), out BasisMediaPlaylistState result, decoded));
        Assert.AreEqual(state.Id, result.Id);
        Assert.AreEqual(state.Revision, result.Revision);
        Assert.AreEqual(state.Position, result.Position);
        Assert.Less(Quaternion.Angle(state.Rotation, result.Rotation), 0.01f);
        Assert.AreEqual(state.Scale, result.Scale, 1e-5f);
        Assert.IsTrue(result.Repeat);
        Assert.AreEqual(517, result.Current);
        CollectionAssert.AreEqual(entries, decoded);
    }

    [Test]
    public void State_caps_entries_and_drops_duplicates()
    {
        List<Guid> entries = Entries(BasisMediaPlaylistProtocol.MaxEntries + 50);
        byte[] buffer = BasisMediaPlaylistProtocol.EncodeState(SampleState(-1), entries);
        var decoded = new List<Guid>();
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodeState(buffer, out _, decoded));
        Assert.AreEqual(BasisMediaPlaylistProtocol.MaxEntries, decoded.Count);

        var twice = new List<Guid> { entries[0], entries[1], entries[0] };
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodeState(BasisMediaPlaylistProtocol.EncodeState(SampleState(2), twice), out BasisMediaPlaylistState result, decoded));
        Assert.AreEqual(2, decoded.Count);
        Assert.AreEqual(-1, result.Current);
    }

    [Test]
    public void State_rejects_truncated_and_padded_buffers()
    {
        byte[] buffer = BasisMediaPlaylistProtocol.EncodeState(SampleState(0), Entries(3));
        var decoded = new List<Guid>();
        Assert.IsFalse(BasisMediaPlaylistProtocol.TryDecodeState(buffer.AsSpan(0, buffer.Length - 1).ToArray(), out _, decoded));
        byte[] padded = new byte[buffer.Length + 1];
        buffer.CopyTo(padded, 0);
        Assert.IsFalse(BasisMediaPlaylistProtocol.TryDecodeState(padded, out _, decoded));
        Assert.IsFalse(BasisMediaPlaylistProtocol.TryDecodeState(null, out _, decoded));
    }

    [Test]
    public void Request_round_trips_and_rejects_unknown_actions()
    {
        Guid playlist = Guid.NewGuid(), clip = Guid.NewGuid();
        byte[] buffer = BasisMediaPlaylistProtocol.EncodeRequest(playlist, BasisMediaPlaylistAction.Remove, 312, clip);
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodeRequest(buffer, out Guid p, out BasisMediaPlaylistAction action, out int index, out Guid c));
        Assert.AreEqual(playlist, p);
        Assert.AreEqual(BasisMediaPlaylistAction.Remove, action);
        Assert.AreEqual(312, index);
        Assert.AreEqual(clip, c);
        buffer[1 + 16] = 200;
        Assert.IsFalse(BasisMediaPlaylistProtocol.TryDecodeRequest(buffer, out _, out _, out _, out _));
    }

    [Test]
    public void Pose_and_close_round_trip()
    {
        Guid playlist = Guid.NewGuid();
        byte[] pose = BasisMediaPlaylistProtocol.EncodePose(playlist, new Vector3(1f, 2f, 3f), Quaternion.identity, 2f);
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodePose(pose, out Guid id, out Vector3 position, out _, out float scale));
        Assert.AreEqual(playlist, id);
        Assert.AreEqual(new Vector3(1f, 2f, 3f), position);
        Assert.AreEqual(2f, scale, 1e-5f);
        Assert.IsTrue(BasisMediaPlaylistProtocol.TryDecodeClose(BasisMediaPlaylistProtocol.EncodeClose(playlist), out Guid closed));
        Assert.AreEqual(playlist, closed);
    }
}
