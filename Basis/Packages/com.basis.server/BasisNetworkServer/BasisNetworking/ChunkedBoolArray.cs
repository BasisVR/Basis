using Basis.Network.Core;
using System;
using System.Threading;
using static BasisServerReductionSystem;

public class ChunkedBoolArray
{
    private readonly bool[][] _chunks;
    private readonly int _chunkSize;
    private readonly int _numChunks;
    private readonly int _totalSize;

    public ChunkedBoolArray(int chunkSize = 256)
    {
        if (BasisNetworkCommons.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");

        _chunkSize = chunkSize;
        _numChunks = (int)Math.Ceiling((double)BasisNetworkCommons.MaxConnections / chunkSize);
        _totalSize = _chunkSize * _numChunks;

        _chunks = new bool[_numChunks][];
        for (int i = 0; i < _numChunks; i++)
        {
            _chunks[i] = new bool[chunkSize];
        }
    }

    public void SetBool(int index, bool value)
    {
        if (index < 0 || index >= _totalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        Volatile.Write(ref _chunks[chunkIndex][localIndex], value);
    }

    public bool GetBool(int index)
    {
        if (index < 0 || index >= _totalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        return Volatile.Read(ref _chunks[chunkIndex][localIndex]);
    }
}

public class ChunkedServerSideReducablePlayerArray
{
    private readonly ServerSideReducablePlayer[][] _chunks;
    private readonly int _chunkSize;
    private readonly int _numChunks;
    private readonly int _totalSize;

    public ChunkedServerSideReducablePlayerArray(int chunkSize = 256)
    {
        if (BasisNetworkCommons.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");

        _chunkSize = chunkSize;
        _numChunks = (int)Math.Ceiling((double)BasisNetworkCommons.MaxConnections / chunkSize);
        _totalSize = _chunkSize * _numChunks;

        _chunks = new ServerSideReducablePlayer[_numChunks][];
        for (int i = 0; i < _numChunks; i++)
        {
            _chunks[i] = new ServerSideReducablePlayer[chunkSize];
        }
    }

    public void SetPlayer(int index, ServerSideReducablePlayer player)
    {
        if (index < 0 || index >= _totalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        Volatile.Write(ref _chunks[chunkIndex][localIndex], player);
    }

    public ServerSideReducablePlayer GetPlayer(int index)
    {
        if (index < 0 || index >= _totalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        return Volatile.Read(ref _chunks[chunkIndex][localIndex]);
    }
}
public class ChunkedSyncedToPlayerPulseArray
{
    private readonly SyncedToPlayerPulse[][] _chunks;
    private readonly int _chunkSize;
    private readonly int _numChunks;
    public const int TotalSize = 1024;

    public ChunkedSyncedToPlayerPulseArray(int chunkSize = 256)
    {
        if (TotalSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(TotalSize), "Total size must be greater than zero.");
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");

        _chunkSize = chunkSize;
        _numChunks = (int)Math.Ceiling((double)TotalSize / chunkSize);
        _chunks = new SyncedToPlayerPulse[_numChunks][];

        for (int i = 0; i < _numChunks; i++)
        {
            _chunks[i] = new SyncedToPlayerPulse[chunkSize];
        }
    }

    public void SetPulse(int index, SyncedToPlayerPulse pulse)
    {
        if (index < 0 || index >= TotalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        Volatile.Write(ref _chunks[chunkIndex][localIndex], pulse);
    }

    public SyncedToPlayerPulse GetPulse(int index)
    {
        if (index < 0 || index >= TotalSize)
            throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

        int chunkIndex = index / _chunkSize;
        int localIndex = index % _chunkSize;

        return Volatile.Read(ref _chunks[chunkIndex][localIndex]);
    }
}
