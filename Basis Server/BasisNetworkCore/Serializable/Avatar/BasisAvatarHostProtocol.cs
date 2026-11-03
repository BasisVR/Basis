namespace Basis.Network.Core
{
    public static class BasisAvatarHostProtocol
    {
        public const string MessageName = "basis.avatarhost";
        public const int IdBytes = 16;
        public const int ChunkBytes = 16 * 1024;
        public const int MaxOffersPerOwner = 4;
        public const int MaxRequestsPerRequester = 4;
        public const long MaxFileBytes = 16L * 1024 * 1024 * 1024;
        public const byte OpOffer = 0, OpWithdraw = 1, OpRequest = 2, OpOwnerChunk = 3, OpOwnerFailed = 4;
        public const byte OpOfferResult = 10, OpFetch = 11, OpChunk = 12, OpFailed = 13;
        public const byte ReasonNone = 0, ReasonDisabled = 1, ReasonTooLarge = 2, ReasonTooManyOffers = 3, ReasonUnknownId = 4, ReasonBadRange = 5, ReasonBusy = 6, ReasonOwnerFailed = 7, ReasonOwnerLeft = 8;
        public static void WriteId(NetDataWriter writer, byte[] id) => writer.Put(id, 0, IdBytes);
        public static bool TryReadId(NetDataReader reader, out byte[] id)
        {
            id = null;
            if (reader.AvailableBytes < IdBytes) return false;
            id = new byte[IdBytes];
            reader.GetBytes(id, 0, IdBytes);
            return true;
        }
        public static string IdToHex(byte[] id)
        {
            if (id == null || id.Length != IdBytes) return null;
            const string digits = "0123456789abcdef";
            char[] chars = new char[IdBytes * 2];
            for (int index = 0; index < IdBytes; index++)
            {
                chars[index * 2] = digits[id[index] >> 4];
                chars[index * 2 + 1] = digits[id[index] & 15];
            }
            return new string(chars);
        }
        public static bool TryHexToId(string hex, out byte[] id)
        {
            id = null;
            if (hex == null || hex.Length != IdBytes * 2) return false;
            byte[] result = new byte[IdBytes];
            for (int index = 0; index < IdBytes; index++)
            {
                int high = HexValue(hex[index * 2]), low = HexValue(hex[index * 2 + 1]);
                if (high < 0 || low < 0) return false;
                result[index] = (byte)((high << 4) | low);
            }
            id = result;
            return true;
        }
        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
