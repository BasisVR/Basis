using Basis.Network.Core;
using System;
using System.Threading;

namespace Basis.Network.Server.Generic
{
    public class ChunkedSyncedToPlayerPulseArray<T> where T : struct
    {
        private readonly ReaderWriterLockSlim[] _playerLocks;
        private readonly T[] _pulses;
        private readonly int _maxPlayers;

        public ChunkedSyncedToPlayerPulseArray()
        {
            _maxPlayers = BasisNetworkCommons.MaxConnections;

            if (_maxPlayers <= 0)
                throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "MaxConnections must be greater than zero.");

            _pulses = new T[_maxPlayers];
            _playerLocks = new ReaderWriterLockSlim[_maxPlayers];

            for (int Index = 0; Index < _maxPlayers; Index++)
            {
                _playerLocks[Index] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            }
        }

        public void SetPulse(int index, T pulse)
        {
            if (index < 0 || index >= _maxPlayers)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            var rwLock = _playerLocks[index];
            rwLock.EnterWriteLock();
            try
            {
                _pulses[index] = pulse;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public T GetPulse(int index)
        {
            if (index < 0 || index >= _maxPlayers)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            var rwLock = _playerLocks[index];
            rwLock.EnterReadLock();
            try
            {
                return _pulses[index];
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
    }
}
