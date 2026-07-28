namespace Basis.ImagePickup
{
    /// <summary>Header validation shared by file security checks.</summary>
    internal static class BasisGifDecoder
    {
        public static bool TryReadDimensions(byte[] bytes, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;
            if (bytes == null || bytes.Length < 10)
            {
                error = "GIF header truncated";
                return false;
            }
            if (
                bytes[0] != (byte)'G'
                || bytes[1] != (byte)'I'
                || bytes[2] != (byte)'F'
                || bytes[3] != (byte)'8'
                || (bytes[4] != (byte)'7' && bytes[4] != (byte)'9')
                || bytes[5] != (byte)'a'
            )
            {
                error = "Not a GIF (bad signature)";
                return false;
            }

            width = bytes[6] | (bytes[7] << 8);
            height = bytes[8] | (bytes[9] << 8);
            if (width <= 0 || height <= 0)
            {
                error = "Invalid GIF dimensions";
                return false;
            }
            return true;
        }
    }
}
