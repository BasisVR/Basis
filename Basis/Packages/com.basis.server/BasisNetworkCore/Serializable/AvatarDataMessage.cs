using LiteNetLib.Utils;
using System;
public static partial class SerializableBasis
{
    public struct AvatarDataMessage
    {
        public PlayerIdMessage PlayerIdMessage;
        public byte messageIndex;
        public byte AvatarLinkIndex;
        public ushort recipientsSize;
        /// <summary>
        /// If null, it's for everyone. Otherwise, send only to the listed entries.
        /// </summary>
        public ushort[] recipients;
        public byte[] payload;

        public void Deserialize(NetDataReader Writer)
        {
            PlayerIdMessage.Deserialize(Writer);
            if (!Writer.TryGetByte(out AvatarLinkIndex))
            {
                throw new ArgumentException("Failed to read AvatarLinkIndex.");
            }
            // Read the messageIndex safely
            if (!Writer.TryGetByte(out messageIndex))
            {
                throw new ArgumentException("Failed to read messageIndex.");
            }
            // Read the recipientsSize safely
            if (Writer.TryGetUShort(out recipientsSize))
            {
                // Guard against negative or absurd sizes
                if (recipientsSize > Writer.AvailableBytes / sizeof(ushort))
                {
                    throw new ArgumentException($"Invalid recipientsSize: {recipientsSize}");
                }
                if (recipients == null || recipients.Length != recipientsSize)
                {
                    recipients = new ushort[recipientsSize];
                }
               // BNL.Log("Recipients is " + recipientsSize);
                for (int index = 0; index < recipientsSize; index++)
                {
                    if (!Writer.TryGetUShort(out recipients[index]))
                    {
                        throw new ArgumentException($"Failed to read recipient at index {index}.");
                    }
                }

                // Read remaining bytes as payload
                if (Writer.AvailableBytes > 0)
                {
                    if (payload != null && payload.Length == Writer.AvailableBytes)
                    {
                        Writer.GetBytes(payload, Writer.AvailableBytes);
                    }
                    else
                    {
                        payload = Writer.GetRemainingBytes();
                    }
                }
            }
            else
            {
                recipients = null;
                payload = null;
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            PlayerIdMessage.Serialize(Writer);
            Writer.Put(AvatarLinkIndex);
            // Write the messageIndex
            Writer.Put(messageIndex);

            // Determine and write the recipientsSize
            if (recipients == null || recipients.Length == 0)
            {
                recipientsSize = 0;
            }
            else
            {
                recipientsSize = (ushort)recipients.Length;
            }
            Writer.Put(recipientsSize);
           // BNL.Log("Recipients is " + recipientsSize);
            // Write the recipients array if present
            if (recipients != null && recipients.Length > 0)
            {
                for (int index = 0; index < recipientsSize; index++)
                {
                    Writer.Put(recipients[index]);
                }
            }

            // Write the payload if present
            if (payload != null && payload.Length > 0)
            {
                Writer.Put(payload);
            }
        }
    }
}
