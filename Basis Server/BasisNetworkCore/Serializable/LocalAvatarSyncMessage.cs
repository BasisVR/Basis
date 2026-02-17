using Basis.Network.Core;
using Basis.Network.Core.Compression;
using System;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        // On-wire contract (v2, no length):
        // [DataQualityLevel:1][PayloadBytes:FixedByQuality][AdditionalSize:1][LinkedAvatarIndex?][Additional...]
        //
        // Payload layout (current order):
        // Position (12) -> Muscles(bitstream, varies by quality) -> Scale (2) -> Rotation (16)

        public byte DataQualityLevel; // 0=Low, 1=Medium, 2=High
        public byte[] array;          // payload bytes (length must match ConvertToSize(quality))

        public AdditionalAvatarData[] AdditionalAvatarDatas;
        public byte AdditionalAvatarDataSize;
        public byte LinkedAvatarIndex;

        public LocalAvatarSyncMessage(byte[] array) : this()
        {
            this.array = array;
        }

        private static bool TryGetExpectedPayloadLength(byte dataQualityLevel, out ushort expected)
        {
            expected = 0;

            var q = (BitQuality)dataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q))
                return false;

            expected = (ushort)BasisAvatarBitPacking.ConvertToSize(q);
            return expected != 0;
        }

        public void Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetByte(out DataQualityLevel))
            {
                BNL.LogError("Missing DataQualityLevel!");
                return;
            }

            if (!TryGetExpectedPayloadLength(DataQualityLevel, out ushort expected))
            {
                BNL.LogError($"Invalid DataQualityLevel={DataQualityLevel}");
                return;
            }

            if (reader.AvailableBytes < expected)
            {
                BNL.LogError($"Unable to read avatar payload. Need {expected}, have {reader.AvailableBytes}.");
                return;
            }

            if (array == null || array.Length != expected)
            {
                array = new byte[expected];
            }

            reader.GetBytes(array, expected);

            if (!reader.TryGetByte(out AdditionalAvatarDataSize))
            {
                BNL.LogError("Missing AdditionalAvatarDataSize!");
                return;
            }

            if (AdditionalAvatarDataSize == 0)
            {
                AdditionalAvatarDatas = null;
                return;
            }

            if (!reader.TryGetByte(out LinkedAvatarIndex))
            {
                BNL.LogError("Missing LinkedAvatarIndex!");
                return;
            }

            AdditionalAvatarDatas = new AdditionalAvatarData[AdditionalAvatarDataSize];
            for (int i = 0; i < AdditionalAvatarDataSize; i++)
            {
                AdditionalAvatarDatas[i] = new AdditionalAvatarData();
                AdditionalAvatarDatas[i].Deserialize(reader);
            }
        }

        public void Serialize(NetDataWriter writer, BitQuality Quality)
        {
            DataQualityLevel = (byte)Quality;
            if (!TryGetExpectedPayloadLength(DataQualityLevel, out ushort expected))
            {
                BNL.LogError($"Serialize invalid quality={Quality} (DataQualityLevel={DataQualityLevel})");
                // Still write something minimally parseable:
                writer.Put(DataQualityLevel);
                writer.Put((byte)0); // AdditionalAvatarDataSize
                return;
            }

            // Header
            writer.Put(DataQualityLevel);

            if (array == null)
            {
                BNL.LogError("array was null!!");
                // Can't write payload; make message parseable:
                writer.Put((byte)0); // AdditionalAvatarDataSize
                return;
            }

            // Strong validation: payload must match exactly
            if (array.Length != expected)
            {
                array = new byte[expected];
            }

            // Payload (no length on wire)
            writer.Put(array, 0, expected);

            // Additional data
            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0 || AdditionalAvatarDatas.Length > 256)
            {
                writer.Put((byte)0);
                return;
            }

            AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
            writer.Put(AdditionalAvatarDataSize);

            // Only include linked avatar if there is additional data
            writer.Put(LinkedAvatarIndex);

            for (int i = 0; i < AdditionalAvatarDataSize; i++)
            {
                AdditionalAvatarDatas[i].Serialize(writer);
            }
        }
    }
}
