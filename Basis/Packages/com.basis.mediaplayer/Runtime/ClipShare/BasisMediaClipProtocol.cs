using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public enum BasisMediaClipContainer : byte
{
    None = 0,
    Mp4 = 1,
    Mov = 2,
    WebM = 3,
    Mkv = 4,
    Mp3 = 5,
    M4a = 6,
    Flac = 7,
    Wav = 8,
    Opus = 9,
    Aac = 10,
}

public struct BasisMediaClipOffer
{
    public Guid Id;
    public int TotalBytes;
    public BasisMediaClipContainer Container;
    public bool SharerProtected;
    public bool SharerBypassesLock;
    public Vector3 Position;
    public Quaternion Rotation;
    public float Scale;
    public string Name;
}

public static class BasisMediaClipProtocol
{
    public const byte OpOffer = 1;
    public const byte OpChunk = 2;
    public const byte OpRemove = 3;
    public const byte OpPose = 4;
    public const byte OpNeed = 5;
    public const byte FlagProtected = 1;
    public const byte FlagLockBypass = 2;
    public const int GuidBytes = 16;
    public const int ChunkBytes = 16 * 1024;
    public const int ChunkHeaderBytes = 1 + GuidBytes + sizeof(int);
    public const int MaxClipBytes = 96 * 1024 * 1024;
    public const int MaxNameBytes = 160;
    public const int MaxNeedIndices = 512;
    public const int PoseBytes = 1 + GuidBytes + sizeof(float) * 8;
    public const string UrlScheme = "basisclip://";

    public static int ChunkCount(int totalBytes) => totalBytes <= 0 ? 0 : (totalBytes + ChunkBytes - 1) / ChunkBytes;

    public static int ChunkLength(int totalBytes, int index)
    {
        int count = ChunkCount(totalBytes);
        if (index < 0 || index >= count) return 0;
        return index == count - 1 ? totalBytes - index * ChunkBytes : ChunkBytes;
    }

    public static string ToUrl(Guid id) => UrlScheme + id.ToString("N");

    public static bool IsClipUrl(string url) => !string.IsNullOrEmpty(url) && url.StartsWith(UrlScheme, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseUrl(string url, out Guid id)
    {
        id = Guid.Empty;
        if (!IsClipUrl(url)) return false;
        string rest = url.Substring(UrlScheme.Length).Trim().TrimEnd('/');
        return rest.Length == 32 && Guid.TryParseExact(rest, "N", out id) && id != Guid.Empty;
    }

    public static bool TryGetContainer(string path, out BasisMediaClipContainer container)
    {
        container = BasisMediaClipContainer.None;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        switch (extension?.ToLowerInvariant())
        {
            case ".mp4":
            case ".m4v":
                container = BasisMediaClipContainer.Mp4;
                return true;
            case ".mov":
                container = BasisMediaClipContainer.Mov;
                return true;
            case ".webm":
                container = BasisMediaClipContainer.WebM;
                return true;
            case ".mkv":
                container = BasisMediaClipContainer.Mkv;
                return true;
            case ".mp3":
                container = BasisMediaClipContainer.Mp3;
                return true;
            case ".m4a":
                container = BasisMediaClipContainer.M4a;
                return true;
            case ".flac":
                container = BasisMediaClipContainer.Flac;
                return true;
            case ".wav":
                container = BasisMediaClipContainer.Wav;
                return true;
            case ".opus":
                container = BasisMediaClipContainer.Opus;
                return true;
            case ".aac":
                container = BasisMediaClipContainer.Aac;
                return true;
        }
        return false;
    }

    public static bool IsAudio(BasisMediaClipContainer container) => container >= BasisMediaClipContainer.Mp3 && container <= BasisMediaClipContainer.Aac;

    public static string Extension(BasisMediaClipContainer container)
    {
        switch (container)
        {
            case BasisMediaClipContainer.Mp4: return ".mp4";
            case BasisMediaClipContainer.Mov: return ".mov";
            case BasisMediaClipContainer.WebM: return ".webm";
            case BasisMediaClipContainer.Mkv: return ".mkv";
            case BasisMediaClipContainer.Mp3: return ".mp3";
            case BasisMediaClipContainer.M4a: return ".m4a";
            case BasisMediaClipContainer.Flac: return ".flac";
            case BasisMediaClipContainer.Wav: return ".wav";
            case BasisMediaClipContainer.Opus: return ".opus";
            case BasisMediaClipContainer.Aac: return ".aac";
        }
        return null;
    }

    public static bool SignatureMatches(ReadOnlySpan<byte> head, BasisMediaClipContainer container)
    {
        switch (container)
        {
            case BasisMediaClipContainer.Mp3:
                return HasId3(head) || head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
            case BasisMediaClipContainer.Aac:
                return HasId3(head) || head.Length >= 4 && IsAscii(head.Slice(0, 4), "ADIF") || head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0;
            case BasisMediaClipContainer.Flac:
                return HasId3(head) || head.Length >= 4 && IsAscii(head.Slice(0, 4), "fLaC");
            case BasisMediaClipContainer.Wav:
                return head.Length >= 12 && (IsAscii(head.Slice(0, 4), "RIFF") || IsAscii(head.Slice(0, 4), "RF64")) && IsAscii(head.Slice(8, 4), "WAVE");
            case BasisMediaClipContainer.Opus:
                return head.Length >= 4 && IsAscii(head.Slice(0, 4), "OggS");
            case BasisMediaClipContainer.Mp4:
            case BasisMediaClipContainer.Mov:
            case BasisMediaClipContainer.M4a:
                if (head.Length < 8) return false;
                ReadOnlySpan<byte> atom = head.Slice(4, 4);
                return IsAscii(atom, "ftyp") || IsAscii(atom, "moov") || IsAscii(atom, "mdat") || IsAscii(atom, "wide") || IsAscii(atom, "free") || IsAscii(atom, "skip") || IsAscii(atom, "pnot");
            case BasisMediaClipContainer.WebM:
            case BasisMediaClipContainer.Mkv:
                return head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        }
        return false;
    }

    private static bool HasId3(ReadOnlySpan<byte> head) => head.Length >= 3 && IsAscii(head.Slice(0, 3), "ID3");

    private static bool IsAscii(ReadOnlySpan<byte> bytes, string text)
    {
        if (bytes.Length != text.Length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (bytes[i] != (byte)text[i]) return false;
        }
        return true;
    }

    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var builder = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsControl(c)) continue;
            System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category == System.Globalization.UnicodeCategory.Format) continue;
            if (c == '<') c = '‹';
            else if (c == '>') c = '›';
            builder.Append(c);
        }
        string result = builder.ToString().Trim();
        while (result.Length > 0 && Encoding.UTF8.GetByteCount(result) > MaxNameBytes)
        {
            int cut = result.Length - 1;
            if (cut > 0 && char.IsLowSurrogate(result[cut])) cut--;
            result = result.Substring(0, cut);
        }
        return result;
    }

    public static byte[] EncodeOffer(in BasisMediaClipOffer offer)
    {
        string name = SanitizeName(offer.Name);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buffer = new byte[1 + GuidBytes + sizeof(int) + 2 + sizeof(float) * 8 + sizeof(ushort) + nameBytes.Length];
        int offset = 0;
        buffer[offset++] = OpOffer;
        WriteGuid(buffer, ref offset, offer.Id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), offer.TotalBytes);
        offset += sizeof(int);
        buffer[offset++] = (byte)offer.Container;
        buffer[offset++] = (byte)((offer.SharerProtected ? FlagProtected : 0) | (offer.SharerBypassesLock ? FlagLockBypass : 0));
        WritePose(buffer, ref offset, offer.Position, offer.Rotation, offer.Scale);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)nameBytes.Length);
        offset += sizeof(ushort);
        Buffer.BlockCopy(nameBytes, 0, buffer, offset, nameBytes.Length);
        return buffer;
    }

    public static bool TryDecodeOffer(byte[] buffer, out BasisMediaClipOffer offer, out string error)
    {
        offer = default;
        error = null;
        int fixedBytes = 1 + GuidBytes + sizeof(int) + 2 + sizeof(float) * 8 + sizeof(ushort);
        if (buffer == null || buffer.Length < fixedBytes || buffer[0] != OpOffer)
        {
            error = "truncated offer";
            return false;
        }
        int offset = 1;
        offer.Id = ReadGuid(buffer, ref offset);
        offer.TotalBytes = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(int);
        offer.Container = (BasisMediaClipContainer)buffer[offset++];
        byte flags = buffer[offset++];
        if ((flags & ~(FlagProtected | FlagLockBypass)) != 0)
        {
            error = "unknown flags";
            return false;
        }
        offer.SharerProtected = (flags & FlagProtected) != 0;
        offer.SharerBypassesLock = (flags & FlagLockBypass) != 0;
        if (!ReadPose(buffer, ref offset, out offer.Position, out offer.Rotation, out offer.Scale))
        {
            error = "invalid pose";
            return false;
        }
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(ushort);
        if (nameLength > MaxNameBytes || offset + nameLength != buffer.Length)
        {
            error = "invalid name";
            return false;
        }
        try
        {
            offer.Name = SanitizeName(new UTF8Encoding(false, true).GetString(buffer, offset, nameLength));
        }
        catch (ArgumentException)
        {
            error = "invalid name";
            return false;
        }
        if (offer.Id == Guid.Empty)
        {
            error = "empty id";
            return false;
        }
        if (offer.TotalBytes <= 0 || offer.TotalBytes > MaxClipBytes)
        {
            error = "size out of range";
            return false;
        }
        if (Extension(offer.Container) == null)
        {
            error = "unsupported container";
            return false;
        }
        return true;
    }

    public static byte[] EncodeChunk(Guid id, int index, byte[] data, int dataOffset, int length)
    {
        byte[] buffer = new byte[ChunkHeaderBytes + length];
        int offset = 0;
        buffer[offset++] = OpChunk;
        WriteGuid(buffer, ref offset, id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), index);
        offset += sizeof(int);
        Buffer.BlockCopy(data, dataOffset, buffer, offset, length);
        return buffer;
    }

    public static bool TryReadChunkHeader(byte[] buffer, out Guid id, out int index, out int length)
    {
        id = Guid.Empty;
        index = -1;
        length = 0;
        if (buffer == null || buffer.Length <= ChunkHeaderBytes || buffer[0] != OpChunk) return false;
        int offset = 1;
        id = ReadGuid(buffer, ref offset);
        index = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        length = buffer.Length - ChunkHeaderBytes;
        return true;
    }

    public static byte[] EncodeRemove(Guid id)
    {
        byte[] buffer = new byte[1 + GuidBytes];
        int offset = 0;
        buffer[offset++] = OpRemove;
        WriteGuid(buffer, ref offset, id);
        return buffer;
    }

    public static bool TryDecodeId(byte[] buffer, byte op, out Guid id)
    {
        id = Guid.Empty;
        if (buffer == null || buffer.Length < 1 + GuidBytes || buffer[0] != op) return false;
        int offset = 1;
        id = ReadGuid(buffer, ref offset);
        return id != Guid.Empty;
    }

    public static byte[] EncodePose(Guid id, Vector3 position, Quaternion rotation, float scale)
    {
        byte[] buffer = new byte[PoseBytes];
        int offset = 0;
        buffer[offset++] = OpPose;
        WriteGuid(buffer, ref offset, id);
        WritePose(buffer, ref offset, position, rotation, scale);
        return buffer;
    }

    public static bool TryDecodePose(byte[] buffer, out Guid id, out Vector3 position, out Quaternion rotation, out float scale)
    {
        id = Guid.Empty;
        position = default;
        rotation = Quaternion.identity;
        scale = 1f;
        if (buffer == null || buffer.Length != PoseBytes || buffer[0] != OpPose) return false;
        int offset = 1;
        id = ReadGuid(buffer, ref offset);
        return ReadPose(buffer, ref offset, out position, out rotation, out scale);
    }

    public static byte[] EncodeNeed(Guid id, IReadOnlyList<int> indices)
    {
        int count = Math.Min(indices?.Count ?? 0, MaxNeedIndices);
        byte[] buffer = new byte[1 + GuidBytes + sizeof(ushort) + count * sizeof(int)];
        int offset = 0;
        buffer[offset++] = OpNeed;
        WriteGuid(buffer, ref offset, id);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)count);
        offset += sizeof(ushort);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), indices[i]);
            offset += sizeof(int);
        }
        return buffer;
    }

    public static bool TryDecodeNeed(byte[] buffer, out Guid id, List<int> indices)
    {
        id = Guid.Empty;
        indices?.Clear();
        int fixedBytes = 1 + GuidBytes + sizeof(ushort);
        if (buffer == null || indices == null || buffer.Length < fixedBytes || buffer[0] != OpNeed) return false;
        int offset = 1;
        id = ReadGuid(buffer, ref offset);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
        offset += sizeof(ushort);
        if (count > MaxNeedIndices || buffer.Length != fixedBytes + count * sizeof(int)) return false;
        for (int i = 0; i < count; i++)
        {
            indices.Add(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)));
            offset += sizeof(int);
        }
        return true;
    }

    private static void WriteGuid(byte[] buffer, ref int offset, Guid id)
    {
        id.TryWriteBytes(buffer.AsSpan(offset, GuidBytes));
        offset += GuidBytes;
    }

    private static Guid ReadGuid(byte[] buffer, ref int offset)
    {
        var id = new Guid(new ReadOnlySpan<byte>(buffer, offset, GuidBytes));
        offset += GuidBytes;
        return id;
    }

    private static void WritePose(byte[] buffer, ref int offset, Vector3 position, Quaternion rotation, float scale)
    {
        WriteFloat(buffer, ref offset, position.x);
        WriteFloat(buffer, ref offset, position.y);
        WriteFloat(buffer, ref offset, position.z);
        WriteFloat(buffer, ref offset, rotation.x);
        WriteFloat(buffer, ref offset, rotation.y);
        WriteFloat(buffer, ref offset, rotation.z);
        WriteFloat(buffer, ref offset, rotation.w);
        WriteFloat(buffer, ref offset, scale);
    }

    private static bool ReadPose(byte[] buffer, ref int offset, out Vector3 position, out Quaternion rotation, out float scale)
    {
        position = new Vector3(ReadFloat(buffer, ref offset), ReadFloat(buffer, ref offset), ReadFloat(buffer, ref offset));
        rotation = new Quaternion(ReadFloat(buffer, ref offset), ReadFloat(buffer, ref offset), ReadFloat(buffer, ref offset), ReadFloat(buffer, ref offset));
        scale = ReadFloat(buffer, ref offset);
        if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) || Mathf.Abs(position.x) > 100000f || Mathf.Abs(position.y) > 100000f || Mathf.Abs(position.z) > 100000f) return false;
        if (!IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w)) return false;
        float magnitude = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
        if (magnitude < 0.5f || magnitude > 1.5f) return false;
        rotation = new Quaternion(rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude);
        if (!IsFinite(scale)) return false;
        scale = Mathf.Clamp(scale, MinScale, MaxScale);
        return true;
    }

    public const float MinScale = 0.25f;
    public const float MaxScale = 4f;

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void WriteFloat(byte[] buffer, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
    }

    private static float ReadFloat(byte[] buffer, ref int offset)
    {
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)));
        offset += sizeof(float);
        return value;
    }
}
