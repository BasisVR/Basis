using System.IO.Compression;

namespace Basis.Scripts.BasisSdk.Players
{
    [Serializable]
    public struct BasisAvatarNetworkLoad
    {
        public string URL;
        public string UnlockPassword;
        /// <summary>
        /// Opaque content-version tag for the bee at <see cref="URL"/>, letting a wearer say "same
        /// url, new bytes" so receivers invalidate their cache for content published to a static
        /// address. Appended after the original two-field format shipped — see
        /// <see cref="DecodeFromBytes"/> for why it must be read optionally.
        /// <para>KEEP IN STEP with the client copy in
        /// com.basis.framework/Players/Common/BasisAvatarNetworkLoad.cs — these two encode the same
        /// wire format and a divergence here corrupts every avatar change between them.</para>
        /// </summary>
        public string VersionTag;

        public const int MaxDecodedBytes = 256 * 1024;

        /// <summary>
        /// Encodes the structure to compressed byte data using custom string serialization and DeflateStream compression.
        /// </summary>
        public byte[] EncodeToBytes()
        {
            using var memoryStream = new MemoryStream();
            using (var writer = new BinaryWriter(memoryStream))
            {
                WriteString(writer, URL);
                WriteString(writer, UnlockPassword);
                WriteString(writer, VersionTag);
            }

            byte[] rawData = memoryStream.ToArray();

            using var compressedStream = new MemoryStream();
            using (var deflateStream = new DeflateStream(compressedStream, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                deflateStream.Write(rawData, 0, rawData.Length);
            }

            return compressedStream.ToArray();
        }

        /// <summary>
        /// Decodes from compressed byte data back to the structure using custom string deserialization and DeflateStream decompression.
        /// </summary>
        public static BasisAvatarNetworkLoad DecodeFromBytes(byte[] compressedData)
        {
            using var compressedStream = new MemoryStream(compressedData);
            using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
            byte[] rawData = Basis.Network.Core.BasisBoundedStream.ReadAllBounded(deflateStream, MaxDecodedBytes, "Avatar network load");

            using var memoryStream = new MemoryStream(rawData);
            using var reader = new BinaryReader(memoryStream);

            return new BasisAvatarNetworkLoad
            {
                URL = ReadString(reader),
                UnlockPassword = ReadString(reader),
                // TryReadString, not ReadString: a record produced before VersionTag existed ends
                // after the two strings above and ReadString's ReadUInt16 would throw at
                // end-of-stream, turning every older peer's avatar change into a decode failure.
                VersionTag = TryReadString(reader, memoryStream, out string versionTag) ? versionTag : string.Empty
            };
        }

        /// <summary>
        /// Writes a string to the BinaryWriter with its length as a ushort.
        /// </summary>
        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write((ushort)bytes.Length); // Write the length as a ushort
            writer.Write(bytes); // Write the string bytes
        }

        /// <summary>
        /// Reads a string from the BinaryReader based on its length (stored as a ushort).
        /// </summary> 
        private static string ReadString(BinaryReader reader)
        {
            ushort length = reader.ReadUInt16(); // Read the length
            byte[] bytes = reader.ReadBytes(length); // Read the string bytes
            return System.Text.Encoding.UTF8.GetString(bytes); // Convert back to a string
        }

        /// <summary>
        /// Reads an OPTIONAL trailing string, reporting absence instead of throwing. Used for
        /// fields appended after the format shipped, so a shorter record written by an older
        /// client decodes cleanly rather than failing at end-of-stream.
        /// </summary>
        private static bool TryReadString(BinaryReader reader, Stream stream, out string value)
        {
            value = string.Empty;
            if (stream.Position + sizeof(ushort) > stream.Length)
            {
                return false;
            }

            ushort length = reader.ReadUInt16();
            if (stream.Position + length > stream.Length)
            {
                return false;
            }

            byte[] bytes = reader.ReadBytes(length);
            value = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}
