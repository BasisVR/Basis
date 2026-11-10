using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using UnityEngine;

public enum BasisMediaPlaylistAction : byte
{
    Insert = 1,
    Remove = 2,
    Play = 3,
    Next = 4,
    Previous = 5,
    Repeat = 6,
}

public struct BasisMediaPlaylistState
{
    public Guid Id;
    public uint Revision;
    public Vector3 Position;
    public Quaternion Rotation;
    public float Scale;
    public bool Repeat;
    public int Current;
}

public static class BasisMediaPlaylistProtocol
{
    public const byte OpState = 1;
    public const byte OpRequest = 2;
    public const byte OpPose = 3;
    public const byte OpClose = 4;
    public const byte OpHello = 5;
    public const int MaxEntries = 1024;
    private const byte FlagRepeat = 1;
    private const int GuidBytes = BasisMediaClipProtocol.GuidBytes;
    private const int PoseFloats = sizeof(float) * 8;
    private const int StateFixedBytes = 1 + GuidBytes + sizeof(uint) + PoseFloats + 1 + sizeof(int) + sizeof(ushort);
    public const int RequestBytes = 1 + GuidBytes + 1 + sizeof(int) + GuidBytes;
    public const int PoseBytes = 1 + GuidBytes + PoseFloats;
    public static readonly byte[] HelloBytes = { OpHello };

    public static byte[] EncodeState(in BasisMediaPlaylistState state, IReadOnlyList<Guid> entries)
    {
        int count = Math.Min(entries?.Count ?? 0, MaxEntries);
        byte[] buffer = new byte[StateFixedBytes + count * GuidBytes];
        int offset = 0;
        buffer[offset++] = OpState;
        BasisMediaClipProtocol.WriteGuid(buffer, ref offset, state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), state.Revision);
        offset += sizeof(uint);
        BasisMediaClipProtocol.WritePose(buffer, ref offset, state.Position, state.Rotation, state.Scale);
        buffer[offset++] = state.Repeat ? FlagRepeat : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), state.Current);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)count);
        offset += sizeof(ushort);
        for (int i = 0; i < count; i++) BasisMediaClipProtocol.WriteGuid(buffer, ref offset, entries[i]);
        return buffer;
    }

    public static bool TryDecodeState(byte[] buffer, out BasisMediaPlaylistState state, List<Guid> entries)
    {
        state = default;
        entries.Clear();
        if (buffer == null || buffer.Length < StateFixedBytes || buffer[0] != OpState) return false;
        int offset = 1;
        state.Id = BasisMediaClipProtocol.ReadGuid(buffer, ref offset);
        state.Revision = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(uint);
        if (!BasisMediaClipProtocol.ReadPose(buffer, ref offset, out state.Position, out state.Rotation, out state.Scale)) return false;
        state.Repeat = (buffer[offset++] & FlagRepeat) != 0;
        state.Current = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(int);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(ushort);
        if (state.Id == Guid.Empty || count > MaxEntries || buffer.Length != StateFixedBytes + count * GuidBytes) return false;
        for (int i = 0; i < count; i++)
        {
            Guid id = BasisMediaClipProtocol.ReadGuid(buffer, ref offset);
            if (id == Guid.Empty || entries.Contains(id)) continue;
            entries.Add(id);
        }
        if (state.Current < -1 || state.Current >= entries.Count) state.Current = -1;
        return true;
    }

    public static byte[] EncodeRequest(Guid playlist, BasisMediaPlaylistAction action, int index, Guid clip)
    {
        byte[] buffer = new byte[RequestBytes];
        int offset = 0;
        buffer[offset++] = OpRequest;
        BasisMediaClipProtocol.WriteGuid(buffer, ref offset, playlist);
        buffer[offset++] = (byte)action;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), index);
        offset += sizeof(int);
        BasisMediaClipProtocol.WriteGuid(buffer, ref offset, clip);
        return buffer;
    }

    public static bool TryDecodeRequest(byte[] buffer, out Guid playlist, out BasisMediaPlaylistAction action, out int index, out Guid clip)
    {
        playlist = Guid.Empty;
        action = 0;
        index = -1;
        clip = Guid.Empty;
        if (buffer == null || buffer.Length != RequestBytes || buffer[0] != OpRequest) return false;
        int offset = 1;
        playlist = BasisMediaClipProtocol.ReadGuid(buffer, ref offset);
        action = (BasisMediaPlaylistAction)buffer[offset++];
        index = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(int);
        clip = BasisMediaClipProtocol.ReadGuid(buffer, ref offset);
        return playlist != Guid.Empty && action >= BasisMediaPlaylistAction.Insert && action <= BasisMediaPlaylistAction.Repeat;
    }

    public static byte[] EncodePose(Guid playlist, Vector3 position, Quaternion rotation, float scale)
    {
        byte[] buffer = new byte[PoseBytes];
        int offset = 0;
        buffer[offset++] = OpPose;
        BasisMediaClipProtocol.WriteGuid(buffer, ref offset, playlist);
        BasisMediaClipProtocol.WritePose(buffer, ref offset, position, rotation, scale);
        return buffer;
    }

    public static bool TryDecodePose(byte[] buffer, out Guid playlist, out Vector3 position, out Quaternion rotation, out float scale)
    {
        playlist = Guid.Empty;
        position = default;
        rotation = Quaternion.identity;
        scale = 1f;
        if (buffer == null || buffer.Length != PoseBytes || buffer[0] != OpPose) return false;
        int offset = 1;
        playlist = BasisMediaClipProtocol.ReadGuid(buffer, ref offset);
        return playlist != Guid.Empty && BasisMediaClipProtocol.ReadPose(buffer, ref offset, out position, out rotation, out scale);
    }

    public static byte[] EncodeClose(Guid playlist) => BasisMediaClipProtocol.EncodeId(OpClose, playlist);

    public static bool TryDecodeClose(byte[] buffer, out Guid playlist) => BasisMediaClipProtocol.TryDecodeId(buffer, OpClose, out playlist) && buffer.Length == 1 + GuidBytes;
}
