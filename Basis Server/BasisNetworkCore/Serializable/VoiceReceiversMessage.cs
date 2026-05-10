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

        /// <summary>
        /// Equivalent to <c>Serialize(writer, largeCount: true)</c> — always writes
        /// a 2-byte (ushort) count. Use only when sending on
        /// <see cref="BasisNetworkCommons.AudioRecipientsLargeChannel"/> (channel 39).
        /// For <see cref="BasisNetworkCommons.AudioRecipientsChannel"/> (channel 5),
        /// call the largeCount overload with <c>false</c> so the length field width
        /// matches what the server expects.
        /// </summary>
        public void Serialize(NetDataWriter writer)
        {
            Serialize(writer, largeCount: true);
        }

        /// <param name="largeCount">
        /// Must match the channel the packet will be sent on:
        /// false = byte count (AudioRecipientsChannel, ≤255 recipients);
        /// true  = ushort count (AudioRecipientsLargeChannel, up to 65535 recipients).
        /// Mismatch desyncs the wire format and the server will log "Protocol mismatch?".
        /// </param>
        public void Serialize(NetDataWriter writer, bool largeCount)
        {
            int usersLength = Users?.Length ?? 0;
            int maxCount = largeCount ? ushort.MaxValue : byte.MaxValue;

            if (usersLength == 0)
            {
                // Still write a 0-length so read side stays in sync
                if (largeCount) writer.Put((ushort)0);
                else writer.Put((byte)0);
                return;
            }

            if (usersLength > maxCount)
            {
                BNL.LogError(
                    $"VoiceReceiversMessage: Users.Length={usersLength} exceeds " +
                    $"{(largeCount ? "ushort.MaxValue" : "byte.MaxValue")} for this channel. Truncating.");
            }

            int count = Math.Min(usersLength, maxCount);
            if (largeCount) writer.Put((ushort)count);
            else writer.Put((byte)count);

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
