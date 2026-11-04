using System;
using System.Net;
using LiteNetLib.Utils;

namespace LiteNetLib
{
   internal sealed class NetConnectRequestPacket
    {
        public const int HeaderSize = 18;
        public const int CookieSize = 16;
        private const byte CookieFlag = 0x80;
        public readonly byte[] Cookie;
        public readonly long ConnectionTime;
        public byte ConnectionNumber;
        public readonly byte[] TargetAddress;
        public readonly NetDataReader Data;
        public readonly int PeerId;

        private NetConnectRequestPacket(long connectionTime, byte connectionNumber, int localId, byte[] targetAddress, NetDataReader data, byte[] cookie)
        {
            Cookie = cookie;
            ConnectionTime = connectionTime;
            ConnectionNumber = connectionNumber;
            TargetAddress = targetAddress;
            Data = data;
            PeerId = localId;
        }

        public static int GetProtocolId(NetPacket packet)
        {
            return FastBitConverter.Read<int>(packet.RawData, 1);
        }

        public static NetConnectRequestPacket FromData(NetPacket packet)
        {
            if (packet.ConnectionNumber >= NetConstants.MaxConnectionNumber)
                return null;

            //Getting connection time for peer
            long connectionTime = FastBitConverter.Read<long>(packet.RawData, 5);

            //Get peer id
            int peerId = FastBitConverter.Read<int>(packet.RawData, 13);

            //Get target address
            int addrByte = packet.RawData[HeaderSize-1];
            bool hasCookie = (addrByte & CookieFlag) != 0;
            int addrSize = addrByte & ~CookieFlag;
            if (addrSize != 16 && addrSize != 28)
                return null;
            int dataStart = HeaderSize + addrSize + (hasCookie ? CookieSize : 0);
            if (packet.Size < dataStart)
                return null;
            byte[] addressBytes = new byte[addrSize];
            Buffer.BlockCopy(packet.RawData, HeaderSize, addressBytes, 0, addrSize);
            byte[] cookie = null;
            if (hasCookie)
            {
                cookie = new byte[CookieSize];
                Buffer.BlockCopy(packet.RawData, HeaderSize + addrSize, cookie, 0, CookieSize);
            }

            // Read data and create request
            var reader = new NetDataReader(null, 0, 0);
            if (packet.Size > dataStart)
                reader.SetSource(packet.RawData, dataStart, packet.Size);

            return new NetConnectRequestPacket(connectionTime, packet.ConnectionNumber, peerId, addressBytes, reader, cookie);
        }

        public static NetPacket WithCookie(NetPacket request, byte[] cookie, int offset)
        {
            int addrByte = request.RawData[HeaderSize - 1];
            int addrSize = addrByte & ~CookieFlag;
            int oldCookie = (addrByte & CookieFlag) != 0 ? CookieSize : 0;
            int tail = request.Size - (HeaderSize + addrSize + oldCookie);
            var packet = new NetPacket(PacketProperty.ConnectRequest, addrSize + CookieSize + tail);
            Buffer.BlockCopy(request.RawData, 0, packet.RawData, 0, HeaderSize + addrSize);
            packet.RawData[HeaderSize - 1] = (byte)(addrSize | CookieFlag);
            Buffer.BlockCopy(cookie, offset, packet.RawData, HeaderSize + addrSize, CookieSize);
            Buffer.BlockCopy(request.RawData, HeaderSize + addrSize + oldCookie, packet.RawData, HeaderSize + addrSize + CookieSize, tail);
            return packet;
        }

        public static NetPacket Make(NetDataWriter connectData, SocketAddress addressBytes, long connectTime, int localId)
        {
            //Make initial packet
            var packet = new NetPacket(PacketProperty.ConnectRequest, connectData.Length+addressBytes.Size);

            //Add data
            FastBitConverter.GetBytes(packet.RawData, 1, NetConstants.ProtocolId);
            FastBitConverter.GetBytes(packet.RawData, 5, connectTime);
            FastBitConverter.GetBytes(packet.RawData, 13, localId);
            packet.RawData[HeaderSize-1] = (byte)addressBytes.Size;
            for (int i = 0; i < addressBytes.Size; i++)
                packet.RawData[HeaderSize + i] = addressBytes[i];
            Buffer.BlockCopy(connectData.Data, 0, packet.RawData, HeaderSize + addressBytes.Size, connectData.Length);
            return packet;
        }
        public static NetPacket Make(ReadOnlySpan<byte> connectData, SocketAddress addressBytes, long connectTime, int localId)
        {
            //Make initial packet
            var packet = new NetPacket(PacketProperty.ConnectRequest, connectData.Length+addressBytes.Size);

            //Add data
            FastBitConverter.GetBytes(packet.RawData, 1, NetConstants.ProtocolId);
            FastBitConverter.GetBytes(packet.RawData, 5, connectTime);
            FastBitConverter.GetBytes(packet.RawData, 13, localId);
            packet.RawData[HeaderSize - 1] = (byte)addressBytes.Size;
            for (int i = 0; i < addressBytes.Size; i++)
                packet.RawData[HeaderSize + i] = addressBytes[i];
            connectData.CopyTo(packet.RawData.AsSpan(HeaderSize + addressBytes.Size));
            return packet;
        }
    }

    internal sealed class NetConnectAcceptPacket
    {
        public const int Size = 15;
        public readonly long ConnectionTime;
        public readonly byte ConnectionNumber;
        public readonly int PeerId;
        public readonly bool PeerNetworkChanged;

        private NetConnectAcceptPacket(long connectionTime, byte connectionNumber, int peerId, bool peerNetworkChanged)
        {
            ConnectionTime = connectionTime;
            ConnectionNumber = connectionNumber;
            PeerId = peerId;
            PeerNetworkChanged = peerNetworkChanged;
        }

        public static NetConnectAcceptPacket FromData(NetPacket packet)
        {
            if (packet.Size != Size)
                return null;

            long connectionId = FastBitConverter.Read<long>(packet.RawData, 1);

            //check connect num
            byte connectionNumber = packet.RawData[9];
            if (connectionNumber >= NetConstants.MaxConnectionNumber)
                return null;

            //check reused flag
            byte isReused = packet.RawData[10];
            if (isReused > 1)
                return null;

            //get remote peer id
            int peerId = FastBitConverter.Read<int>(packet.RawData, 11);
            if (peerId < 0)
                return null;

            return new NetConnectAcceptPacket(connectionId, connectionNumber, peerId, isReused == 1);
        }

        public static NetPacket Make(long connectTime, byte connectNum, int localPeerId)
        {
            var packet = new NetPacket(PacketProperty.ConnectAccept, 0);
            FastBitConverter.GetBytes(packet.RawData, 1, connectTime);
            packet.RawData[9] = connectNum;
            FastBitConverter.GetBytes(packet.RawData, 11, localPeerId);
            return packet;
        }
        
        public static NetPacket MakeNetworkChanged(NetPeer peer)
        {
            var packet = new NetPacket(PacketProperty.PeerNotFound, Size-1);
            FastBitConverter.GetBytes(packet.RawData, 1, peer.ConnectTime);
            packet.RawData[9] = peer.ConnectionNum;
            packet.RawData[10] = 1;
            FastBitConverter.GetBytes(packet.RawData, 11, peer.RemoteId);
            return packet;
        }
    }
}
