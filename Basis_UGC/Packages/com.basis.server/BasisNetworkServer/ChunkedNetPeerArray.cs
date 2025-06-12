using System;
using System.Threading;
using Basis.Network.Core;
using LiteNetLib;

public class StripedNetPeerArray
{
    private readonly NetPeer[][] _chunks;           // Data storage in chunks
    private readonly ReaderWriterLockSlim[] _locks; // Striped locks
    private readonly ushort _chunkSize;            // Size of each chunk
    private readonly ushort _lockCount;            // Number of locks
    public StripedNetPeerArray(ushort chunkSize = 64, ushort lockCount = 32)
    {
        if (BasisNetworkCommons.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");
        if (lockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(lockCount), "Lock count must be greater than zero.");

        _chunkSize = chunkSize;
        _lockCount = lockCount;

        ushort numChunks = (ushort)Math.Ceiling((double)BasisNetworkCommons.MaxConnections / chunkSize);
        _chunks = new NetPeer[numChunks][];
        _locks = new ReaderWriterLockSlim[lockCount];

        for (ushort i = 0; i < numChunks; i++)
        {
            _chunks[i] = new NetPeer[chunkSize];
        }

        for (ushort i = 0; i < lockCount; i++)
        {
            _locks[i] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }
    }

    public void SetPeer(ushort index, NetPeer value)
    {
        if (index >= BasisNetworkCommons.MaxConnections)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        ushort chunkIndex = (ushort)(index / _chunkSize);
        ushort localIndex = (ushort)(index % _chunkSize);
        ushort lockIndex = (ushort)(index % _lockCount); // Lock striping

        var lockObj = _locks[lockIndex];
        lockObj.EnterWriteLock();
        try
        {
            _chunks[chunkIndex][localIndex] = value;
        }
        finally
        {
            lockObj.ExitWriteLock();
        }
    }

    public NetPeer GetPeer(ushort index)
    {
        if (index >= BasisNetworkCommons.MaxConnections)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        ushort chunkIndex = (ushort)(index / _chunkSize);
        ushort localIndex = (ushort)(index % _chunkSize);

        // Use Volatile.Read to avoid locking for read-only access
        return Volatile.Read(ref _chunks[chunkIndex][localIndex]);
    }
}
