using System;
using System.Globalization;
using System.Text;

namespace Basis.Network.Core
{
    public enum BasisStorageOp : byte
    {
        Get = 0,
        Set = 1,
        Delete = 2,
        Keys = 3,
        Usage = 4,
        Clear = 5,
    }
    public enum BasisStorageStatus : byte
    {
        Ok = 0,
        NotFound = 1,
        Denied = 2,
        Full = 3,
        RateLimited = 4,
        Invalid = 5,
        Unavailable = 6,
        Busy = 7,
        TimedOut = 8,
        NotConnected = 9,
        NotAllowed = 10,
    }
    public struct BasisStorageRequest
    {
        public BasisStorageOp Op;
        public ushort RequestId;
        public string Scope;
        public string Key;
        public byte[] Value;
    }
    public struct BasisStorageReply
    {
        public BasisStorageOp Op;
        public ushort RequestId;
        public BasisStorageStatus Status;
        public int Used;
        public int Limit;
        public byte[] Value;
        public string[] Keys;
    }
    public static class BasisStorageProtocol
    {
        public const int MaxNameBytes = 64, MaxValueBytes = ushort.MaxValue, MaxEntriesPerPlayer = 1024, MaxScopesPerPlayer = 64;
        public const int DefaultBytesPerPlayer = 16384, MaxBytesPerPlayer = 1048576, MaxInFlightPerConnection = 16;
        public static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static bool HasKey(BasisStorageOp op) => op == BasisStorageOp.Get || op == BasisStorageOp.Set || op == BasisStorageOp.Delete;
        public static bool IsKnown(BasisStorageOp op) => op <= BasisStorageOp.Clear;
        public static bool Mutates(BasisStorageOp op) => op == BasisStorageOp.Set || op == BasisStorageOp.Delete || op == BasisStorageOp.Clear;
        public static int ScopeCost(int scopeBytes) => 3 + scopeBytes;
        public static int EntryCost(int keyBytes, int valueBytes) => 3 + keyBytes + valueBytes;
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameBytes || char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[name.Length - 1])) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                    if (!IsAllowed(CharUnicodeInfo.GetUnicodeCategory(name, i))) return false;
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c) || !IsAllowed(CharUnicodeInfo.GetUnicodeCategory(c))) return false;
            }
            return StrictUtf8.GetByteCount(name) <= MaxNameBytes;
        }
        public static bool TryEncodeName(string name, out byte[] bytes)
        {
            bytes = null;
            if (!IsValidName(name)) return false;
            bytes = StrictUtf8.GetBytes(name);
            return true;
        }
        public static bool TryDecodeName(byte[] bytes, out string name)
        {
            name = null;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxNameBytes) return false;
            try { name = StrictUtf8.GetString(bytes); }
            catch (ArgumentException) { return false; }
            if (IsValidName(name)) return true;
            name = null;
            return false;
        }
        public static void WriteRequest(NetDataWriter writer, BasisStorageRequest request)
        {
            writer.Put((byte)request.Op);
            writer.Put(request.RequestId);
            PutBlob(writer, StrictUtf8.GetBytes(request.Scope ?? string.Empty));
            if (HasKey(request.Op)) PutBlob(writer, StrictUtf8.GetBytes(request.Key ?? string.Empty));
            if (request.Op == BasisStorageOp.Set) PutBlob(writer, request.Value ?? Array.Empty<byte>());
        }
        public static bool TryReadRequest(NetDataReader reader, out BasisStorageRequest request)
        {
            request = default;
            if (!reader.TryGetByte(out byte op) || !IsKnown((BasisStorageOp)op) || !reader.TryGetUShort(out ushort id)) return false;
            request.Op = (BasisStorageOp)op;
            request.RequestId = id;
            if (!TryGetBlob(reader, MaxNameBytes, out byte[] scope) || !TryDecodeName(scope, out request.Scope)) return false;
            if (HasKey(request.Op) && (!TryGetBlob(reader, MaxNameBytes, out byte[] key) || !TryDecodeName(key, out request.Key))) return false;
            if (request.Op == BasisStorageOp.Set && !TryGetBlob(reader, MaxValueBytes, out request.Value)) return false;
            return reader.AvailableBytes == 0;
        }
        public static void WriteReply(NetDataWriter writer, BasisStorageReply reply)
        {
            writer.Put((byte)reply.Op);
            writer.Put(reply.RequestId);
            writer.Put((byte)reply.Status);
            writer.Put(reply.Used);
            writer.Put(reply.Limit);
            if (reply.Status != BasisStorageStatus.Ok) return;
            if (reply.Op == BasisStorageOp.Get) PutBlob(writer, reply.Value ?? Array.Empty<byte>());
            else if (reply.Op == BasisStorageOp.Keys)
            {
                string[] keys = reply.Keys ?? Array.Empty<string>();
                writer.Put((ushort)keys.Length);
                for (int i = 0; i < keys.Length; i++) PutBlob(writer, StrictUtf8.GetBytes(keys[i]));
            }
        }
        public static bool TryReadReply(NetDataReader reader, out BasisStorageReply reply)
        {
            reply = default;
            if (!reader.TryGetByte(out byte op) || !IsKnown((BasisStorageOp)op) || !reader.TryGetUShort(out reply.RequestId) || !reader.TryGetByte(out byte status) || !reader.TryGetInt(out reply.Used) || !reader.TryGetInt(out reply.Limit)) return false;
            reply.Op = (BasisStorageOp)op;
            reply.Status = status <= (byte)BasisStorageStatus.NotAllowed ? (BasisStorageStatus)status : BasisStorageStatus.Unavailable;
            if (reply.Status == BasisStorageStatus.Ok)
            {
                if (reply.Op == BasisStorageOp.Get && !TryGetBlob(reader, MaxValueBytes, out reply.Value)) return false;
                if (reply.Op == BasisStorageOp.Keys)
                {
                    if (!reader.TryGetUShort(out ushort count) || count > MaxEntriesPerPlayer) return false;
                    reply.Keys = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        if (!TryGetBlob(reader, MaxNameBytes, out byte[] key) || !TryDecodeName(key, out reply.Keys[i])) return false;
                    }
                }
            }
            return reader.AvailableBytes == 0;
        }
        private static bool IsAllowed(UnicodeCategory category)
        {
            switch (category)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                    return false;
                default:
                    return true;
            }
        }
        private static void PutBlob(NetDataWriter writer, byte[] data)
        {
            writer.Put((ushort)data.Length);
            writer.Put(data, 0, data.Length);
        }
        private static bool TryGetBlob(NetDataReader reader, int max, out byte[] data)
        {
            data = null;
            if (!reader.TryGetUShort(out ushort length) || length > max || length > reader.AvailableBytes) return false;
            data = new byte[length];
            if (length > 0) reader.GetBytes(data, length);
            return true;
        }
    }
}
