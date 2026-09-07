namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public struct PendingAvatarSend
    {
        public byte[] Source;
        public int Length;
        public byte Channel;
        public byte Interval;
        public byte IntervalOffset; // 1 for byte-id, 2 for ushort-id
    }
}
