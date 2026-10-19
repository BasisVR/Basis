using System;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Entry framing for <see cref="PacketProperty.CompactMerged"/> datagrams.
    ///
    /// <para>Bit 7 selects the extended 16-bit length form, bit 6 marks an opaque raw
    /// Ack/Channeled packet, and bits 0-5 carry the application channel for compact
    /// unreliable entries. LiteNetLib caps configured application channels at 64, so
    /// all valid channels fit in six bits.</para>
    /// </summary>
    internal static class CompactMerge
    {
        internal const byte LongLengthFlag = 0x80;
        internal const byte RawPacketFlag = 0x40;
        internal const byte ChannelMask = 0x3F;
        internal const int MaxShortLength = byte.MaxValue;
        internal const int ShortEntryOverhead = 2;
        internal const int LongEntryOverhead = 3;

        internal static int EntryOverhead(int payloadLength)
        {
            return payloadLength > MaxShortLength ? LongEntryOverhead : ShortEntryOverhead;
        }

        internal static int EntrySize(int payloadLength)
        {
            return payloadLength + EntryOverhead(payloadLength);
        }

        internal static bool CanCarryChannel(byte channel)
        {
            return channel <= ChannelMask;
        }

        private static int WriteHeader(byte[] destination, int offset, byte tag, int payloadLength)
        {
            if (payloadLength > MaxShortLength)
            {
                destination[offset++] = (byte)(tag | LongLengthFlag);
                FastBitConverter.GetBytes(destination, offset, (ushort)payloadLength);
                return LongEntryOverhead;
            }

            destination[offset] = tag;
            destination[offset + 1] = (byte)payloadLength;
            return ShortEntryOverhead;
        }

        /// <summary>Writes one compact unreliable entry and returns the bytes written.</summary>
        internal static int WriteUnreliableEntry(
            byte[] destination,
            int offset,
            byte channel,
            byte[] source,
            int sourceOffset,
            int payloadLength)
        {
            int overhead = WriteHeader(destination, offset, channel, payloadLength);
            Buffer.BlockCopy(source, sourceOffset, destination, offset + overhead, payloadLength);
            return overhead + payloadLength;
        }

        // Keep the original helper name for the existing direct codec tests and callers.
        internal static int WriteEntry(
            byte[] destination,
            int offset,
            byte channel,
            byte[] source,
            int sourceOffset,
            int payloadLength) =>
            WriteUnreliableEntry(destination, offset, channel, source, sourceOffset, payloadLength);

        /// <summary>Writes one complete raw Ack/Channeled packet and returns the bytes written.</summary>
        internal static int WriteRawEntry(
            byte[] destination,
            int offset,
            byte[] source,
            int sourceOffset,
            int packetLength)
        {
            int overhead = WriteHeader(destination, offset, RawPacketFlag, packetLength);
            Buffer.BlockCopy(source, sourceOffset, destination, offset + overhead, packetLength);
            return overhead + packetLength;
        }

        /// <summary>
        /// Reads the entry header at <paramref name="offset"/>, leaving it on the first payload
        /// byte. Raw entries are accepted only for Ack/Channeled packets, preventing recursive
        /// CompactMerged nesting and keeping the raw escape canonical.
        /// </summary>
        internal static bool TryReadEntry(
            byte[] source,
            int size,
            ref int offset,
            out bool isRawPacket,
            out byte channel,
            out int payloadLength)
        {
            isRawPacket = false;
            channel = 0;
            payloadLength = 0;

            if (source == null || size < 0 || size > source.Length || offset < 0 || size - offset < ShortEntryOverhead)
                return false;

            byte tag = source[offset++];
            isRawPacket = (tag & RawPacketFlag) != 0;
            channel = (byte)(tag & ChannelMask);

            // Raw entries have no application-channel field. Non-zero low bits would create
            // alternate encodings of the same packet, so reject them.
            if (isRawPacket && channel != 0)
                return false;

            if ((tag & LongLengthFlag) != 0)
            {
                if (size - offset < 2)
                    return false;
                payloadLength = FastBitConverter.Read<ushort>(source, offset);
                offset += 2;

                // Keep one canonical encoding: <=255 always uses the short form.
                if (payloadLength <= MaxShortLength)
                    return false;
            }
            else
            {
                payloadLength = source[offset++];
            }

            if (payloadLength > size - offset)
                return false;

            if (isRawPacket)
            {
                if (payloadLength < NetConstants.ChanneledHeaderSize || payloadLength > NetConstants.MaxPacketSize)
                    return false;

                PacketProperty property = (PacketProperty)(source[offset] & 0x1F);
                if (property != PacketProperty.Ack && property != PacketProperty.Channeled)
                    return false;
            }
            else if (payloadLength + NetConstants.UnreliableHeaderSize > NetConstants.MaxPacketSize)
            {
                return false;
            }

            return true;
        }

        // Compatibility overload for existing codec tests that only decode unreliable entries.
        internal static bool TryReadEntry(
            byte[] source,
            int size,
            ref int offset,
            out byte channel,
            out int payloadLength)
        {
            int originalOffset = offset;
            if (!TryReadEntry(source, size, ref offset, out bool isRawPacket, out channel, out payloadLength) || isRawPacket)
            {
                offset = originalOffset;
                channel = 0;
                payloadLength = 0;
                return false;
            }

            return true;
        }
    }
}
