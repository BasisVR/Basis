using Basis.Contrib.Crypto;
using System;
using System.Security.Cryptography;
using System.Text;
namespace Basis.Network.Core
{
    public static class BasisContentKeyProtocol
    {
        public const string MessageName = "basis.contentkeys";
        public const string RequestPrefix = "\u0001basis-keyreq-v1:", GrantPrefix = "\u0001basis-keys-v1:";
        public const ushort ServerOrigin = ushort.MaxValue;
        public const int PublicKeyBytes = 32, IdBytes = 16, NonceBytes = BasisChaCha20Poly1305.NonceSize, TagBytes = BasisChaCha20Poly1305.TagSize;
        public const int SaltBytes = 16, KeyBytes = 32, GrantEntryBytes = SaltBytes + KeyBytes, MaxGrantEntries = 64;
        public const int MaxGrantBytes = 2 + MaxGrantEntries * GrantEntryBytes, MaxSealedBytes = MaxGrantBytes + TagBytes;
        public const int MaxInflightPerHolder = 8, MaxPendingPerRequester = 256, MaxQueuedPerContent = 8192, MaxLiveRequests = 262144, MaxOwnedPerPeer = 1024, MaxTries = 4;
        public const long RequestTimeoutMs = 30000;
        public const byte OpOwn = 0, OpHave = 1, OpDrop = 2, OpRequest = 3, OpGrant = 4, OpDeny = 5, OpBad = 6;
        public const byte ReasonNone = 0, ReasonUnknown = 1, ReasonOwnerLeft = 2, ReasonBusy = 3, ReasonUnavailable = 4;
        private static readonly byte[] sessionInfo = Encoding.ASCII.GetBytes("basis-content-keys-v1");
        private static readonly byte[] idLabel = Encoding.ASCII.GetBytes("basis-content-id-v1\n"), commitLabel = Encoding.ASCII.GetBytes("basis-content-commit-v1\n");
        public static bool IsMarker(string password) => password != null && password.StartsWith(RequestPrefix, StringComparison.Ordinal);
        public static bool IsToken(string password) => password != null && password.StartsWith(GrantPrefix, StringComparison.Ordinal);
        public static string Marker(byte[] contentId) => RequestPrefix + "/" + Hex(contentId);
        public static string Marker(ushort origin, byte[] contentId) => RequestPrefix + origin.ToString(System.Globalization.CultureInfo.InvariantCulture) + (IsId(contentId) ? "/" + Hex(contentId) : string.Empty);
        public static bool HasOrigin(string marker) => IsMarker(marker) && marker.Length > RequestPrefix.Length && marker[RequestPrefix.Length] != '/';
        public static string WithOrigin(string marker, ushort origin) => !IsMarker(marker) || HasOrigin(marker) ? marker : RequestPrefix + origin.ToString(System.Globalization.CultureInfo.InvariantCulture) + marker.Substring(RequestPrefix.Length);
        public static bool TryParseMarker(string password, out ushort origin, out byte[] contentId)
        {
            origin = 0;
            contentId = null;
            if (!IsMarker(password)) return false;
            string rest = password.Substring(RequestPrefix.Length);
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                contentId = FromHex(rest.Substring(slash + 1));
                if (contentId == null) return false;
                rest = rest.Substring(0, slash);
            }
            return ushort.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out origin);
        }
        public static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length != IdBytes * 2) return null;
            byte[] bytes = new byte[IdBytes];
            for (int index = 0; index < IdBytes; index++)
            {
                if (!byte.TryParse(hex.Substring(index * 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out bytes[index])) return null;
            }
            return bytes;
        }
        public static bool IsValidGrant(byte[] grant)
        {
            if (grant == null || grant.Length < 2) return false;
            int count = grant[0] | grant[1] << 8;
            return count > 0 && count <= MaxGrantEntries && grant.Length == 2 + count * GrantEntryBytes;
        }
        public static string Token(byte[] grant) => IsValidGrant(grant) ? GrantPrefix + Convert.ToBase64String(grant) : null;
        public static byte[] GrantFromToken(string token)
        {
            if (!IsToken(token)) return null;
            try
            {
                byte[] grant = Convert.FromBase64String(token.Substring(GrantPrefix.Length));
                return IsValidGrant(grant) ? grant : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        public static byte[] ContentId(string url, string versionTag) => Hash16(idLabel, Encoding.UTF8.GetBytes(string.Concat(url ?? string.Empty, "\n", versionTag ?? string.Empty)));
        public static byte[] Commit(byte[] grant) => Hash16(commitLabel, grant);
        public static void NewKeyPair(out byte[] privateKey, out byte[] publicKey) => BasisX25519.GenerateKeyPair(out privateKey, out publicKey);
        public static uint[] SessionKey(byte[] privateKey, byte[] peerPublic, byte[] requesterPublic, byte[] holderPublic)
        {
            if (!IsKey(privateKey) || !IsKey(peerPublic) || !IsKey(requesterPublic) || !IsKey(holderPublic)) return null;
            byte[] shared = null, key = null;
            try
            {
                shared = BasisX25519.Agree(privateKey, peerPublic);
                byte[] salt = new byte[PublicKeyBytes * 2];
                Buffer.BlockCopy(requesterPublic, 0, salt, 0, PublicKeyBytes);
                Buffer.BlockCopy(holderPublic, 0, salt, PublicKeyBytes, PublicKeyBytes);
                key = BasisHkdf.DeriveKey(shared, salt, sessionInfo, BasisChaCha20Poly1305.KeySize);
                return BasisChaCha20Poly1305.ExpandKey(key);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (shared != null) Array.Clear(shared, 0, shared.Length);
                if (key != null) Array.Clear(key, 0, key.Length);
            }
        }
        public static byte[] Seal(uint[] sessionKey, uint requestId, byte[] commit, byte[] requesterPublic, byte[] holderPublic, byte[] grant, out byte[] nonce)
        {
            nonce = null;
            if (sessionKey == null || !IsId(commit) || !IsValidGrant(grant)) return null;
            byte[] freshNonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(freshNonce);
            byte[] sealedData = new byte[grant.Length + TagBytes];
            Buffer.BlockCopy(grant, 0, sealedData, 0, grant.Length);
            BasisChaCha20Poly1305.Seal(sessionKey, freshNonce, Aad(requestId, commit, requesterPublic, holderPublic), new Span<byte>(sealedData, 0, grant.Length), new Span<byte>(sealedData, grant.Length, TagBytes));
            nonce = freshNonce;
            return sealedData;
        }
        public static byte[] Open(uint[] sessionKey, uint requestId, byte[] commit, byte[] requesterPublic, byte[] holderPublic, byte[] nonce, byte[] sealedData)
        {
            if (sessionKey == null || !IsId(commit) || nonce == null || nonce.Length != NonceBytes || sealedData == null || sealedData.Length <= TagBytes || sealedData.Length > MaxSealedBytes) return null;
            int length = sealedData.Length - TagBytes;
            byte[] plain = new byte[length];
            Buffer.BlockCopy(sealedData, 0, plain, 0, length);
            if (!BasisChaCha20Poly1305.Open(sessionKey, nonce, Aad(requestId, commit, requesterPublic, holderPublic), new Span<byte>(plain), new ReadOnlySpan<byte>(sealedData, length, TagBytes))) return null;
            if (IsValidGrant(plain) && new ReadOnlySpan<byte>(Commit(plain)).SequenceEqual(commit)) return plain;
            Array.Clear(plain, 0, plain.Length);
            return null;
        }
        public static bool TryReadBytes(NetDataReader reader, int count, out byte[] bytes)
        {
            bytes = null;
            if (count < 0 || reader.AvailableBytes < count) return false;
            bytes = new byte[count];
            reader.GetBytes(bytes, 0, count);
            return true;
        }
        public static string Hex(byte[] id) => id == null ? string.Empty : BitConverter.ToString(id).Replace("-", string.Empty);
        public static bool IsKey(byte[] key) => key != null && key.Length == PublicKeyBytes;
        public static bool IsId(byte[] id) => id != null && id.Length == IdBytes;
        private static byte[] Hash16(byte[] label, byte[] data)
        {
            using SHA256 sha = SHA256.Create();
            byte[] input = new byte[label.Length + (data?.Length ?? 0)];
            Buffer.BlockCopy(label, 0, input, 0, label.Length);
            if (data != null) Buffer.BlockCopy(data, 0, input, label.Length, data.Length);
            byte[] full = sha.ComputeHash(input);
            byte[] hash = new byte[IdBytes];
            Buffer.BlockCopy(full, 0, hash, 0, IdBytes);
            return hash;
        }
        private static byte[] Aad(uint requestId, byte[] commit, byte[] requesterPublic, byte[] holderPublic)
        {
            byte[] aad = new byte[4 + IdBytes + PublicKeyBytes * 2];
            aad[0] = (byte)requestId;
            aad[1] = (byte)(requestId >> 8);
            aad[2] = (byte)(requestId >> 16);
            aad[3] = (byte)(requestId >> 24);
            Buffer.BlockCopy(commit, 0, aad, 4, IdBytes);
            Buffer.BlockCopy(requesterPublic, 0, aad, 4 + IdBytes, PublicKeyBytes);
            Buffer.BlockCopy(holderPublic, 0, aad, 4 + IdBytes + PublicKeyBytes, PublicKeyBytes);
            return aad;
        }
    }
}
