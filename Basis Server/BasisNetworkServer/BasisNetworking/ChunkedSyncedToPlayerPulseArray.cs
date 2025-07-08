using Basis.Network.Core;
using System;

namespace Basis.Network.Server.Generic
{
    // Generic implementation with per-player locking
    public class ChunkedSyncedToPlayerPulseArray<T> where T : struct
    {
        private readonly object[] _playerLocks;
        private readonly T[] _pulses;
        private readonly int _maxPlayers;

        public ChunkedSyncedToPlayerPulseArray()
        {
            _maxPlayers = BasisNetworkCommons.MaxConnections;

            if (_maxPlayers <= 0)
                throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "MaxConnections must be greater than zero.");

            _pulses = new T[_maxPlayers];
            _playerLocks = new object[_maxPlayers];

            for (int i = 0; i < _maxPlayers; i++)
            {
                _playerLocks[i] = new object();
            }
        }

        public void SetPulse(int index, T pulse)
        {
            if (index < 0 || index >= _maxPlayers)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            lock (_playerLocks[index])
            {
                _pulses[index] = pulse;
            }
        }

        public T GetPulse(int index)
        {
            if (index < 0 || index >= _maxPlayers)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            lock (_playerLocks[index])
            {
                return _pulses[index];
            }
        }
    }
}
