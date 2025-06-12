using System.IO.Compression;

namespace Basis.Scripts.BasisSdk.Players
{
    [Serializable]
    public struct AvatarNetworkLoadInformation
    {
        public string AvatarBundleUrl;
        public string AvatarMetaUrl;
        public string UnlockPassword;

        /// <summary>
        /// Encodes the structure to compressed byte data using custom string serialization and DeflateStream compression.
        /// </summary>
        public byte[] EncodeToBytes()
        {
            using var memoryStream = new MemoryStream();
            using (var writer = new BinaryWriter(memoryStream))
            {
                WriteString(writer, AvatarBundleUrl);
                WriteString(writer, AvatarMetaUrl);
                WriteString(writer, UnlockPassword);
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
        public static AvatarNetworkLoadInformation DecodeFromBytes(byte[] compressedData)
        {
            using var compressedStream = new MemoryStream(compressedData);
            using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            deflateStream.CopyTo(decompressedStream);

            byte[] rawData = decompressedStream.ToArray();

            using var memoryStream = new MemoryStream(rawData);
            using var reader = new BinaryReader(memoryStream);

            return new AvatarNetworkLoadInformation
            {
                AvatarBundleUrl = ReadString(reader),
                AvatarMetaUrl = ReadString(reader),
                UnlockPassword = ReadString(reader)
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
    }
}
