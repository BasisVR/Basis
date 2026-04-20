using System;
using System.Buffers;
using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct VoiceReceiversMessage
    {
        // Hard cap to avoid giant allocations if data is corrupted
        private const int MaxUsers = ushort.MaxValue;

        public ushort[] Users;
        public int UsersLength; // actual count (rented array may be larger)

        /// <param name="largeCount">
        /// false = byte count (AudioRecipientsChannel, ≤255 recipients).
        /// true  = ushort count (AudioRecipientsLargeChannel, >255 recipients).
        /// </param>
        public void Deserialize(NetDataReader reader, bool largeCount)
        {
            int remainingBytes = reader.AvailableBytes;

            if (remainingBytes <= 0)
            {
                Users = Array.Empty<ushort>();
                return;
            }

            int countSize = largeCount ? sizeof(ushort) : sizeof(byte);
            if (remainingBytes < countSize)
            {
                BNL.LogError(
                    $"VoiceReceiversMessage: not enough bytes for length. " +
                    $"Remaining={remainingBytes}");
                SkipRemaining(reader);
                Users = Array.Empty<ushort>();
                return;
            }

            ushort count = largeCount ? reader.GetUShort() : reader.GetByte();

            if (count == 0)
            {
                ReturnPool();
                Users = null;
                UsersLength = 0;
                return;
            }

            if (count > MaxUsers)
            {
                BNL.LogError($"VoiceReceiversMessage: reported count={count} exceeds MaxUsers={MaxUsers}. Possible protocol mismatch or corrupted packet.");
                SkipRemaining(reader);
                ReturnPool();
                Users = null;
                UsersLength = 0;
                return;
            }

            int bytesNeeded = count * sizeof(ushort);

            if (reader.AvailableBytes < bytesNeeded)
            {
                BNL.LogError($"VoiceReceiversMessage: count={count} needs {bytesNeeded} bytes, but only {reader.AvailableBytes} available. Protocol mismatch?");
                SkipRemaining(reader);
                ReturnPool();
                Users = null;
                UsersLength = 0;
                return;
            }

            // Return previous rented array before renting a new one
            ReturnPool();
            Users = ArrayPool<ushort>.Shared.Rent(count);
            UsersLength = count;
            for (int i = 0; i < count; i++)
            {
                Users[i] = reader.GetUShort();
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            if (Users == null || Users.Length == 0)
            {
                // Still write a 0-length so read side stays in sync
                writer.Put((ushort)0);
                return;
            }

            if (Users.Length > ushort.MaxValue)
            {
                BNL.LogError(
                    $"VoiceReceiversMessage: Users.Length={Users.Length} exceeds ushort.MaxValue. " +
                    "Truncating.");
            }

            ushort count = (ushort)Math.Min(Users.Length, ushort.MaxValue);
            writer.Put(count);

            for (int i = 0; i < count; i++)
            {
                writer.Put(Users[i]);
            }
        }

        /// <summary>
        /// Returns the rented array to the pool. Call after resolving to peers.
        /// </summary>
        public void ReturnPool()
        {
            if (Users != null)
            {
                ArrayPool<ushort>.Shared.Return(Users);
                Users = null;
                UsersLength = 0;
            }
        }

        private static void SkipRemaining(NetDataReader reader)
        {
            if (reader.AvailableBytes > 0)
            {
                reader.SkipBytes(reader.AvailableBytes);
            }
        }
    }
}
