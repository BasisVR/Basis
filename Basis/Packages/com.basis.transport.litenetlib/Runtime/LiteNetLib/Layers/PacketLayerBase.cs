using System.Net;

namespace LiteNetLib.Layers
{
    public abstract class PacketLayerBase
    {
        public readonly int ExtraPacketSizeForLayer;

        protected PacketLayerBase(int extraPacketSizeForLayer)
        {
            ExtraPacketSizeForLayer = extraPacketSizeForLayer;
        }

        public virtual int ExtraSizeFor(byte property) => ExtraPacketSizeForLayer;

        public virtual int InboundExtraSizeFor(byte property) => 0;

        public virtual bool AllowsEndPointChange => true;

        public abstract void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length);
        public abstract void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length);
    }
}
