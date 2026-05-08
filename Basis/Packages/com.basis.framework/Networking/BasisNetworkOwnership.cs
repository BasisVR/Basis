using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
using static SerializableBasis;

public static partial class BasisNetworkOwnership
{
    public static async Task<BasisOwnershipResult> RemoveOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000, CancellationToken externalToken = default)
    {
        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cts.Token.Register(() =>
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            tcs.TrySetResult(BasisOwnershipResult.Failed);
        });

        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

        var ownershipTransferMessage = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)BasisNetworkConnection.LocalPlayerPeer.RemoteId,
            },
            ownershipID = UniqueNetworkId
        };

        var netDataWriter = new NetDataWriter();
        ownershipTransferMessage.Serialize(netDataWriter);

        var peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer != null)
        {
            peer.Send(netDataWriter, BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, DeliveryMethod.ReliableSequenced);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OwnershipTransfer, netDataWriter.Length);
        }
        else
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            return BasisOwnershipResult.Failed;
        }

        cts.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
    public static async Task<BasisOwnershipResult> TakeOwnershipAsync(string UniqueNetworkId, int NewOwner, int timeoutMs = 5000, CancellationToken externalToken = default)
    {
        return await TakeOwnershipAsync(UniqueNetworkId, (ushort)NewOwner, timeoutMs, externalToken);
    }
    public static async Task<BasisOwnershipResult> TakeOwnershipAsync(string UniqueNetworkId, ushort NewOwner, int timeoutMs = 5000, CancellationToken externalToken = default)
    {
        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId && playerID == NewOwner)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cts.Token.Register(() =>
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            tcs.TrySetResult(BasisOwnershipResult.Failed);
        });

        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

        var ownershipTransferMessage = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = NewOwner
            },
            ownershipID = UniqueNetworkId
        };

        var netDataWriter = new NetDataWriter();
        ownershipTransferMessage.Serialize(netDataWriter);

        var peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer != null)
        {
            peer.Send(netDataWriter, BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, DeliveryMethod.ReliableOrdered);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OwnershipTransfer, netDataWriter.Length);
        }
        else
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            return BasisOwnershipResult.Failed;
        }

        cts.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
    /// <summary>
    /// skips asking the server potentially about ownership
    /// useful sometimes
    /// </summary>
    public static bool IsOwnerLocalValidation(string OwnershipId)
    {
        if (BasisNetworkPlayers.OwnershipPairing.TryGetValue(OwnershipId, out ushort Unique))
        {
            if (Unique == (ushort)BasisNetworkConnection.LocalPlayerPeer.RemoteId)
            {
                return true;
            }
        }
        return false;
    }
    public static async Task<BasisOwnershipResult> RequestCurrentOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000, CancellationToken externalToken = default)
    {
        if (BasisNetworkPlayers.OwnershipPairing.TryGetValue(UniqueNetworkId, out ushort Unique))
        {
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort LocalID))
            {
                bool isLocalOwner = Unique == LocalID;

                BasisNetworkPlayer.OnOwnershipTransfer?.Invoke(UniqueNetworkId, Unique, isLocalOwner);
            }
            return new BasisOwnershipResult(true, Unique);
        }

        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cts.Token.Register(() =>
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            tcs.TrySetResult(BasisOwnershipResult.Failed);
        });

        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

        var ownershipTransferMessage = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)BasisNetworkConnection.LocalPlayerPeer.RemoteId,
            },
            ownershipID = UniqueNetworkId
        };

        var netDataWriter = new NetDataWriter();
        ownershipTransferMessage.Serialize(netDataWriter);

        var peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer != null)
        {
            peer.Send(netDataWriter, BasisNetworkCommons.GetCurrentOwnerRequestChannel, DeliveryMethod.ReliableOrdered);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.RequestOwnershipTransfer, netDataWriter.Length);
        }
        else
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            return BasisOwnershipResult.Failed;
        }

        cts.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
}
