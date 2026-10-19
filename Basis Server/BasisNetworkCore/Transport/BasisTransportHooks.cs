namespace Basis.Network.Core
{
    public interface IBasisHealthWriter
    {
        void Number(string name, long value);
        void Flag(string name, bool value);
        void Text(string name, string value);
    }

    public interface IBasisTransportHealth
    {
        void WriteHealth(IBasisHealthWriter writer);
    }

    public interface IBasisTransportTimeouts
    {
        int DisconnectTimeoutMs { get; }
    }

    public interface IBasisTransportScaling
    {
        double PeerUpdatePressure { get; }
        long PeersUpdatedTotal { get; }
        long PeerUpdateBusyMicros { get; }
        int PeerUpdateWorkers { get; }
        double PeerUpdatePassMs { get; }
        double PeerUpdatePassTargetMs { get; }
        int UnreliableQueuePerPeer { get; }
        int PriorityUnreliableQueuePerPeer { get; }
        int MaxSendSockets { get; }
        int BoundSendSocketCount { get; }
        bool CanAddSendSockets { get; }
        bool AllowSendSocketGrowth { get; set; }
        bool TryAddSendSocket();
        void ApplyCpuBudget(double machineUtilization, int peerUpdateWorkerCap);
    }
}
