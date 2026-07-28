using Basis.Network.Core;
using Basis.Network.Core.Compression;
using System;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        // On-wire contract:
        // Client→Server (channel 2):  [DataQualityLevel:1][PayloadBytes:FixedByQuality][AdditionalSize:1][LinkedAvatarIndex?][Additional...]
        // Server→Client (even ch):    [PayloadBytes:FixedByQuality]
        // Server→Client (odd ch):     [PayloadBytes:FixedByQuality][AdditionalSize:1][LinkedAvatarIndex:1][Additional...]
        //   Quality and additional-data presence are derived from the channel number.
        //
        // Payload layout (current order):
        // Position (12) -> bone rotations (bitstream, varies by quality) -> Posit16 scale (2) -> rotation (7) -> hips tail

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

        /// <summary>
        /// Deserialize when DataQualityLevel is in the payload (client→server path).
        /// </summary>
        public void Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetByte(out DataQualityLevel))
            {
                BNL.LogError("Missing DataQualityLevel!");
                return;
            }

            DeserializePayload(reader);
        }

        /// <summary>
        /// Deserialize when quality and additional-data presence are derived from the channel (server→client path).
        /// Even channels carry no additional data section at all. Odd channels carry additional data.
        /// </summary>
        public void Deserialize(NetDataReader reader, byte channelDerivedQuality, bool hasAdditionalData)
        {
            DataQualityLevel = channelDerivedQuality;

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

            if (!hasAdditionalData)
            {
                AdditionalAvatarDataSize = 0;
                AdditionalAvatarDatas = null;
                return;
            }

            DeserializeAdditionalData(reader);
        }

        private void DeserializePayload(NetDataReader reader)
        {
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

            DeserializeAdditionalEntries(reader);
        }

        /// <summary>
        /// Reads the additional-data section [size:1][linkedIndex:1][entries...]. Public so the
        /// avatar delta receive path can attach blendshape data after reconstructing the pose payload.
        /// </summary>
        public void DeserializeAdditionalData(NetDataReader reader)
        {
            if (!reader.TryGetByte(out AdditionalAvatarDataSize))
            {
                BNL.LogError("Missing AdditionalAvatarDataSize!");
                return;
            }

            DeserializeAdditionalEntries(reader);
        }

        private void DeserializeAdditionalEntries(NetDataReader reader)
        {
            if (!reader.TryGetByte(out LinkedAvatarIndex))
            {
                BNL.LogError("Missing LinkedAvatarIndex!");
                return;
            }

            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length != AdditionalAvatarDataSize)
            {
                AdditionalAvatarDatas = new AdditionalAvatarData[AdditionalAvatarDataSize];
            }
            for (int i = 0; i < AdditionalAvatarDataSize; i++)
            {
                AdditionalAvatarDatas[i] = new AdditionalAvatarData();
                AdditionalAvatarDatas[i].Deserialize(reader);
            }
        }

        /// <summary>
        /// Serialize with DataQualityLevel in the payload (initial player creation, non-quality channels).
        /// </summary>
        public void Serialize(NetDataWriter writer, BitQuality Quality)
        {
            DataQualityLevel = (byte)Quality;
            if (!TryGetExpectedPayloadLength(DataQualityLevel, out ushort expected))
            {
                BNL.LogError($"Serialize invalid quality={Quality} (DataQualityLevel={DataQualityLevel})");
                writer.Put(DataQualityLevel);
                writer.Put((byte)0);
                return;
            }

            writer.Put(DataQualityLevel);

            if (array == null)
            {
                BNL.LogError("array was null!!");
                writer.Put((byte)0);
                return;
            }

            if (array.Length != expected)
            {
                array = new byte[expected];
            }

            writer.Put(array, 0, expected);

            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0 || AdditionalAvatarDatas.Length > 255)
            {
                writer.Put((byte)0);
                return;
            }

            AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
            writer.Put(AdditionalAvatarDataSize);
            writer.Put(LinkedAvatarIndex);

            for (int i = 0; i < AdditionalAvatarDataSize; i++)
            {
                AdditionalAvatarDatas[i].Serialize(writer);
            }
        }

        /// <summary>
        /// Serialize for the channel-based path (quality channels).
        /// Quality and additional-data presence are encoded in the channel — not written to the payload.
        /// </summary>
        public void SerializeForChannel(NetDataWriter writer, BitQuality Quality)
        {
            DataQualityLevel = (byte)Quality;
            if (!TryGetExpectedPayloadLength(DataQualityLevel, out ushort expected))
            {
                BNL.LogError($"SerializeForChannel invalid quality={Quality}");
                return;
            }

            if (array == null)
            {
                BNL.LogError("array was null!!");
                return;
            }

            if (array.Length != expected)
            {
                array = new byte[expected];
            }

            writer.Put(array, 0, expected);

            // Additional data only written when present — the channel tells the receiver.
            if (AdditionalAvatarDatas != null && AdditionalAvatarDatas.Length > 0 && AdditionalAvatarDatas.Length <= 255)
            {
                SerializeAdditionalOnly(writer);
            }
        }

        /// <summary>
        /// Writes just the additional-data section [size:1][linkedIndex:1][entries...] — the uplink
        /// delta path appends this after the delta body (the delta header's additional bit tells the
        /// receiver it is there), mirroring DeserializeAdditionalData.
        /// </summary>
        public void SerializeAdditionalOnly(NetDataWriter writer)
        {
            if (AdditionalAvatarDatas == null || AdditionalAvatarDatas.Length == 0 || AdditionalAvatarDatas.Length > 255)
            {
                return;
            }
            AdditionalAvatarDataSize = (byte)AdditionalAvatarDatas.Length;
            writer.Put(AdditionalAvatarDataSize);
            writer.Put(LinkedAvatarIndex);

            for (int i = 0; i < AdditionalAvatarDataSize; i++)
            {
                AdditionalAvatarDatas[i].Serialize(writer);
            }
        }
    }
}
