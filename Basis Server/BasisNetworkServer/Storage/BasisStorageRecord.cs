using Basis.Network.Core;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisStorageRecord
    {
        public const int HeaderBytes = 18, HashBytes = 32, MaxOwnerBytes = 256;
        public const int MaxBlobBytes = HeaderBytes + MaxOwnerBytes + 2 + BasisStorageProtocol.MaxBytesPerPlayer + HashBytes;
        private const byte Version = 1;
        private const string Hex = "0123456789abcdef";
        private static readonly byte[] Magic = { (byte)'B', (byte)'S', (byte)'T', (byte)'R' };
        private readonly Dictionary<string, Dictionary<string, byte[]>> scopes = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        private readonly byte[] ownerBytes;
        public readonly string Owner;
        public long Revision;
        public int Used { get; private set; }
        public int Entries { get; private set; }
        public int Scopes => scopes.Count;
        public bool IsEmpty => scopes.Count == 0;
        public int BlobLength => HeaderBytes + ownerBytes.Length + 2 + Used + HashBytes;
        public BasisStorageRecord(string owner)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            ownerBytes = BasisStorageProtocol.StrictUtf8.GetBytes(owner);
            if (ownerBytes.Length == 0 || ownerBytes.Length > MaxOwnerBytes) throw new ArgumentException("Storage owner must be 1 to 256 UTF-8 bytes.", nameof(owner));
        }
        public static string OwnerKeyOf(string owner)
        {
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(BasisStorageProtocol.StrictUtf8.GetBytes(owner));
            char[] text = new char[digest.Length * 2];
            for (int i = 0; i < digest.Length; i++)
            {
                text[i * 2] = Hex[digest[i] >> 4];
                text[i * 2 + 1] = Hex[digest[i] & 15];
            }
            return new string(text);
        }
        public static bool IsOwnerKey(string key)
        {
            if (key == null || key.Length != 64) return false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
            }
            return true;
        }
        public bool TryGet(string scope, string key, out byte[] value)
        {
            value = null;
            return scopes.TryGetValue(scope, out Dictionary<string, byte[]> entries) && entries.TryGetValue(key, out value);
        }
        public string[] KeysOf(string scope)
        {
            if (!scopes.TryGetValue(scope, out Dictionary<string, byte[]> entries)) return Array.Empty<string>();
            string[] keys = new string[entries.Count];
            entries.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }
        public string[] ScopeNames()
        {
            string[] names = new string[scopes.Count];
            scopes.Keys.CopyTo(names, 0);
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }
        public int SizeOf(string scope, string key) => TryGet(scope, key, out byte[] value) ? value.Length : -1;
        public BasisStorageStatus Set(string scope, string key, byte[] value, int limit)
        {
            if (value == null || value.Length > BasisStorageProtocol.MaxValueBytes) return BasisStorageStatus.Invalid;
            int scopeBytes = BasisStorageProtocol.StrictUtf8.GetByteCount(scope), keyBytes = BasisStorageProtocol.StrictUtf8.GetByteCount(key);
            bool hasScope = scopes.TryGetValue(scope, out Dictionary<string, byte[]> entries);
            int used = Used, count = Entries;
            if (hasScope && entries.TryGetValue(key, out byte[] existing)) used += value.Length - existing.Length;
            else
            {
                if (!hasScope && scopes.Count >= BasisStorageProtocol.MaxScopesPerPlayer) return BasisStorageStatus.Full;
                if (count >= BasisStorageProtocol.MaxEntriesPerPlayer) return BasisStorageStatus.Full;
                used += BasisStorageProtocol.EntryCost(keyBytes, value.Length) + (hasScope ? 0 : BasisStorageProtocol.ScopeCost(scopeBytes));
                count++;
            }
            if (used > Used && used > Math.Min(limit, BasisStorageProtocol.MaxBytesPerPlayer)) return BasisStorageStatus.Full;
            if (!hasScope)
            {
                entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                scopes.Add(scope, entries);
            }
            entries[key] = value;
            Used = used;
            Entries = count;
            return BasisStorageStatus.Ok;
        }
        public bool Remove(string scope, string key)
        {
            if (!scopes.TryGetValue(scope, out Dictionary<string, byte[]> entries) || !entries.TryGetValue(key, out byte[] value)) return false;
            entries.Remove(key);
            Used -= BasisStorageProtocol.EntryCost(BasisStorageProtocol.StrictUtf8.GetByteCount(key), value.Length);
            Entries--;
            if (entries.Count == 0)
            {
                scopes.Remove(scope);
                Used -= BasisStorageProtocol.ScopeCost(BasisStorageProtocol.StrictUtf8.GetByteCount(scope));
            }
            return true;
        }
        public int Clear(string scope)
        {
            if (!scopes.TryGetValue(scope, out Dictionary<string, byte[]> entries)) return 0;
            int removed = entries.Count, freed = BasisStorageProtocol.ScopeCost(BasisStorageProtocol.StrictUtf8.GetByteCount(scope));
            foreach (KeyValuePair<string, byte[]> entry in entries) freed += BasisStorageProtocol.EntryCost(BasisStorageProtocol.StrictUtf8.GetByteCount(entry.Key), entry.Value.Length);
            scopes.Remove(scope);
            Used -= freed;
            Entries -= removed;
            return removed;
        }
        public BasisStorageRecord Clone()
        {
            BasisStorageRecord copy = new BasisStorageRecord(Owner) { Revision = Revision, Used = Used, Entries = Entries };
            foreach (KeyValuePair<string, Dictionary<string, byte[]>> scope in scopes) copy.scopes.Add(scope.Key, new Dictionary<string, byte[]>(scope.Value, StringComparer.Ordinal));
            return copy;
        }
        public byte[] Serialize()
        {
            byte[] blob = new byte[BlobLength];
            Span<byte> span = blob;
            Magic.CopyTo(span);
            span[4] = Version;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), Revision);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(16), (ushort)ownerBytes.Length);
            int at = HeaderBytes;
            ownerBytes.CopyTo(span.Slice(at));
            at += ownerBytes.Length;
            string[] names = ScopeNames();
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(at), (ushort)names.Length);
            at += 2;
            foreach (string name in names)
            {
                at = PutName(span, at, name);
                string[] keys = KeysOf(name);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(at), (ushort)keys.Length);
                at += 2;
                Dictionary<string, byte[]> entries = scopes[name];
                foreach (string key in keys)
                {
                    at = PutName(span, at, key);
                    byte[] value = entries[key];
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(at), (ushort)value.Length);
                    at += 2;
                    value.CopyTo(span.Slice(at));
                    at += value.Length;
                }
            }
            using (SHA256 sha = SHA256.Create()) sha.ComputeHash(blob, 0, at).CopyTo(span.Slice(at));
            return blob;
        }
        public static bool TryParse(byte[] blob, string expectedOwner, out BasisStorageRecord record)
        {
            record = null;
            if (blob == null || blob.Length < HeaderBytes + 2 + HashBytes || blob.Length > MaxBlobBytes || expectedOwner == null) return false;
            ReadOnlySpan<byte> span = blob;
            int end = blob.Length - HashBytes;
            if (!span.Slice(0, 4).SequenceEqual(Magic) || span[4] != Version || span[5] != 0 || span[6] != 0 || span[7] != 0) return false;
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(blob, 0, end);
            if (!span.Slice(end).SequenceEqual(digest)) return false;
            long revision = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8));
            int ownerLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16)), at = HeaderBytes;
            if (revision < 0 || ownerLength == 0 || ownerLength > MaxOwnerBytes || at + ownerLength + 2 > end) return false;
            byte[] expected = BasisStorageProtocol.StrictUtf8.GetBytes(expectedOwner);
            if (!span.Slice(at, ownerLength).SequenceEqual(expected)) return false;
            at += ownerLength;
            BasisStorageRecord parsed = new BasisStorageRecord(expectedOwner) { Revision = revision };
            int scopeCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(at));
            at += 2;
            if (scopeCount > BasisStorageProtocol.MaxScopesPerPlayer) return false;
            for (int s = 0; s < scopeCount; s++)
            {
                if (!TryGetName(span, ref at, end, out string scope) || parsed.scopes.ContainsKey(scope) || at + 2 > end) return false;
                int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(at));
                at += 2;
                if (entryCount == 0 || parsed.Entries + entryCount > BasisStorageProtocol.MaxEntriesPerPlayer) return false;
                Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(entryCount, StringComparer.Ordinal);
                parsed.scopes.Add(scope, entries);
                parsed.Used += BasisStorageProtocol.ScopeCost(BasisStorageProtocol.StrictUtf8.GetByteCount(scope));
                for (int e = 0; e < entryCount; e++)
                {
                    if (!TryGetName(span, ref at, end, out string key) || entries.ContainsKey(key) || at + 2 > end) return false;
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(at));
                    at += 2;
                    if (at + length > end) return false;
                    entries.Add(key, span.Slice(at, length).ToArray());
                    at += length;
                    parsed.Used += BasisStorageProtocol.EntryCost(BasisStorageProtocol.StrictUtf8.GetByteCount(key), length);
                    parsed.Entries++;
                }
            }
            if (at != end || parsed.Used > BasisStorageProtocol.MaxBytesPerPlayer) return false;
            record = parsed;
            return true;
        }
        public static bool TryReadOwner(byte[] blob, out string owner)
        {
            owner = null;
            if (blob == null || blob.Length < HeaderBytes + 2 + HashBytes || blob.Length > MaxBlobBytes) return false;
            ReadOnlySpan<byte> span = blob;
            if (!span.Slice(0, 4).SequenceEqual(Magic) || span[4] != Version) return false;
            int ownerLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16));
            if (ownerLength == 0 || ownerLength > MaxOwnerBytes || HeaderBytes + ownerLength + 2 + HashBytes > blob.Length) return false;
            try { owner = BasisStorageProtocol.StrictUtf8.GetString(blob, HeaderBytes, ownerLength); }
            catch (ArgumentException) { return false; }
            return true;
        }
        private static int PutName(Span<byte> span, int at, string name)
        {
            int length = BasisStorageProtocol.StrictUtf8.GetBytes(name, span.Slice(at + 1));
            span[at] = (byte)length;
            return at + 1 + length;
        }
        private static bool TryGetName(ReadOnlySpan<byte> span, ref int at, int end, out string name)
        {
            name = null;
            if (at >= end) return false;
            int length = span[at];
            if (length == 0 || at + 1 + length > end) return false;
            if (!BasisStorageProtocol.TryDecodeName(span.Slice(at + 1, length).ToArray(), out name)) return false;
            at += 1 + length;
            return true;
        }
    }
}
