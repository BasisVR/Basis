using System;

namespace HVR.Basis.Comms
{
    public interface ICommsNetworkable
    {
        void OnGuidAssigned(int guidIndex, Guid guid);
    }
}