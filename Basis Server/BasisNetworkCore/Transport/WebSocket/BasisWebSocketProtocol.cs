using System;
using System.Buffers.Binary;

namespace Basis.Network.Core
{
    public static class BasisWebSocketProtocol
    {
        public const string SubProtocol = "basis.v1";
        public const uint ConnectMagic = 0x57534142;
        public const byte Version = 1;
        public const byte KindData = 0;
        public const byte KindConnect = 1;
        public const byte KindAccept = 2;
        public const byte KindReject = 3;
        public const byte KindDisconnect = 4;
        public const byte KindPing = 5;
        public const byte KindPong = 6;
        public const byte KindQuery = 7;
        public const byte KindQueryResponse = 8;
        public const int MaxVarUIntBytes = 5;
        public const int DataHeaderBytes = 2;
        public const int ConnectHeaderBytes = 5;
        public const int AcceptBodyBytes = 16;
        public const int PingBodyBytes = 8;
        public const int PongBodyBytes = 16;
        public const int ChannelCount = 256;
        public const long UnixEpochTicks = 621355968000000000L;

        public static bool IsDroppable(DeliveryMethod method)
        {
            return method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced;
        }

        public static bool IsValidMethod(byte method)
        {
            return method <= (byte)DeliveryMethod.Unreliable;
        }

        public static int VarUIntSize(uint value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }
            return size;
        }

        public static int WriteVarUInt(byte[] buffer, int offset, uint value)
        {
            int start = offset;
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
            return offset - start;
        }

        public static bool TryReadVarUInt(byte[] buffer, ref int offset, int end, out uint value)
        {
            value = 0;
            int shift = 0;
            for (int index = 0; index < MaxVarUIntBytes; index++)
            {
                if (offset >= end) return false;
                byte current = buffer[offset++];
                if (index == MaxVarUIntBytes - 1 && current > 0x0F) return false;
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        public static bool TryReadFrame(byte[] buffer, ref int offset, int end, out byte kind, out int bodyOffset, out int bodyLength)
        {
            kind = 0;
            bodyOffset = 0;
            bodyLength = 0;
            int cursor = offset;
            if (!TryReadVarUInt(buffer, ref cursor, end, out uint length)) return false;
            if (length == 0 || length > (uint)(end - cursor)) return false;
            kind = buffer[cursor];
            bodyOffset = cursor + 1;
            bodyLength = (int)length - 1;
            offset = cursor + (int)length;
            return true;
        }

        public static int FrameSize(int bodyLength)
        {
            uint length = (uint)(bodyLength + 1);
            return VarUIntSize(length) + (int)length;
        }

        public static int WriteFrameHeader(byte[] buffer, int offset, byte kind, int bodyLength)
        {
            int written = WriteVarUInt(buffer, offset, (uint)(bodyLength + 1));
            buffer[offset + written] = kind;
            return written + 1;
        }

        public static void WriteInt64(byte[] buffer, int offset, long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(buffer, offset, 8), value);
        }

        public static long ReadInt64(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 8));
        }

        public static void WriteInt32(byte[] buffer, int offset, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buffer, offset, 4), value);
        }

        public static int ReadInt32(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 4));
        }

        public static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buffer, offset, 4), value);
        }

        public static uint ReadUInt32(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 4));
        }
    }
}
