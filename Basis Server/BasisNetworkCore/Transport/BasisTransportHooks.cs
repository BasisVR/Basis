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
}
