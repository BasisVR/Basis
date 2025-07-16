using System;
using System.Threading;
using Basis.Network.Core;
using LiteNetLib;

public class StripedNetPeerArray
{
    private readonly NetPeer[] _peers;                     // Flat peer array
    private readonly ReaderWriterLockSlim[] _locks;        // One lock per peer
    private readonly int _maxConnections;

    public StripedNetPeerArray()
    {
        _maxConnections = BasisNetworkCommons.MaxConnections;

        if (_maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "MaxConnections must be greater than zero.");

        _peers = new NetPeer[_maxConnections];
        _locks = new ReaderWriterLockSlim[_maxConnections];

        for (int i = 0; i < _maxConnections; i++)
        {
            _locks[i] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }
    }

    public void SetPeer(ushort index, NetPeer value)
    {
        if (index >= _maxConnections)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        var lockObj = _locks[index];
        lockObj.EnterWriteLock();
        try
        {
            _peers[index] = value;
        }
        finally
        {
            lockObj.ExitWriteLock();
        }
    }

    public NetPeer GetPeer(ushort index)
    {
        if (index >= _maxConnections)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        var lockObj = _locks[index];
        lockObj.EnterReadLock();
        try
        {
            return _peers[index];
        }
        finally
        {
            lockObj.ExitReadLock();
        }
    }

    public bool TryGetPeer(ushort index, out NetPeer peer)
    {
        if (index >= _maxConnections)
        {
            peer = null;
            return false;
        }

        var lockObj = _locks[index];
        lockObj.EnterReadLock();
        try
        {
            peer = _peers[index];
            return peer != null;
        }
        finally
        {
            lockObj.ExitReadLock();
        }
    }

    public void ClearPeer(ushort index)
    {
        if (index >= _maxConnections)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        var lockObj = _locks[index];
        lockObj.EnterWriteLock();
        try
        {
            _peers[index] = null;
        }
        finally
        {
            lockObj.ExitWriteLock();
        }
    }
}
