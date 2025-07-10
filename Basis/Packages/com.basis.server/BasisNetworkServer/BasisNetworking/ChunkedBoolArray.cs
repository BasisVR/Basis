using Basis.Network.Core;
using System;
public class LockedBoolArray
{
    private readonly bool[] _array;
    private readonly object[] _locks;
    private readonly int _totalSize;

    public LockedBoolArray()
    {
        if (BasisNetworkCommons.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");

        _totalSize = BasisNetworkCommons.MaxConnections;
        _array = new bool[_totalSize];
        _locks = new object[_totalSize];
        for (int i = 0; i < _totalSize; i++)
            _locks[i] = new object();
    }

    public void SetBool(int index, bool value)
    {
        lock (_locks[index])
        {
            _array[index] = value;
        }
    }

    public bool GetBool(int index)
    {
        lock (_locks[index])
        {
            return _array[index];
        }
    }
    public class LockedServerSideReducablePlayerArray
    {
        private readonly ServerSideReducablePlayer[] _array;
        private readonly object[] _locks;
        private readonly int _totalSize;

        public LockedServerSideReducablePlayerArray()
        {
            if (BasisNetworkCommons.MaxConnections <= 0)
                throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");

            _totalSize = BasisNetworkCommons.MaxConnections;
            _array = new ServerSideReducablePlayer[_totalSize];
            _locks = new object[_totalSize];
            for (int i = 0; i < _totalSize; i++)
                _locks[i] = new object();
        }

        public void SetPlayer(int index, ServerSideReducablePlayer player)
        {
            lock (_locks[index])
            {
                _array[index] = player;
            }
        }

        public ServerSideReducablePlayer GetPlayer(int index)
        {
            lock (_locks[index])
            {
                return _array[index];
            }
        }
    }
    public class LockedSyncedToPlayerPulseArray
    {
        private readonly SyncedToPlayerPulse[] _array;
        private readonly object[] _locks;
        public const int TotalSize = 1024;

        public LockedSyncedToPlayerPulseArray()
        {
            _array = new SyncedToPlayerPulse[TotalSize];
            _locks = new object[TotalSize];
            for (int i = 0; i < TotalSize; i++)
                _locks[i] = new object();
        }

        public void SetPulse(int index, SyncedToPlayerPulse pulse)
        {
            lock (_locks[index])
            {
                _array[index] = pulse;
            }
        }

        public SyncedToPlayerPulse GetPulse(int index)
        {
            lock (_locks[index])
            {
                return _array[index];
            }
        }
    }
}
