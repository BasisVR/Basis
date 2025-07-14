using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
using static SerializableBasis;

public static partial class BasisNetworkOwnership
{
    public static async Task<BasisOwnershipResult> RemoveOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cancellationTokenSource = new CancellationTokenSource();

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                cancellationTokenSource.Cancel(); // Stop timeout countdown
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cancellationTokenSource.Token.Register(() =>
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            tcs.TrySetResult(BasisOwnershipResult.Failed);
        });

        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

        var ownershipTransferMessage = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId,
            },
            ownershipID = UniqueNetworkId
        };

        var netDataWriter = new NetDataWriter();
        ownershipTransferMessage.Serialize(netDataWriter);

        var peer = BasisNetworkManagement.LocalPlayerPeer;
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

        cancellationTokenSource.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
    public static async Task<BasisOwnershipResult> TakeOwnershipAsync(string UniqueNetworkId, int NewOwner, int timeoutMs = 5000)
    {
        return await TakeOwnershipAsync(UniqueNetworkId, (ushort)NewOwner, timeoutMs);
    }
    public static async Task<BasisOwnershipResult> TakeOwnershipAsync(string UniqueNetworkId, ushort NewOwner, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cancellationTokenSource = new CancellationTokenSource();

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId && playerID == NewOwner)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                cancellationTokenSource.Cancel(); // Stop timeout countdown
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cancellationTokenSource.Token.Register(() =>
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

        var peer = BasisNetworkManagement.LocalPlayerPeer;
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

        cancellationTokenSource.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
    /// <summary>
    /// skips asking the server potentially about ownership
    /// useful sometimes
    /// </summary>
    public static bool IsOwnerLocalValidation(string OwnershipId)
    {
        if (BasisNetworkManagement.OwnershipPairing.TryGetValue(OwnershipId, out ushort Unique))
        {
            if (Unique == (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId)
            {
                return true;
            }
        }
        return false;
    }
    public static async Task<BasisOwnershipResult> RequestCurrentOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000)
    {
        if (BasisNetworkManagement.OwnershipPairing.TryGetValue(UniqueNetworkId, out ushort Unique))
        {
            return new BasisOwnershipResult(true, Unique);
        }

        var tcs = new TaskCompletionSource<BasisOwnershipResult>();
        using var cancellationTokenSource = new CancellationTokenSource();

        void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
        {
            if (ownershipID == UniqueNetworkId)
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                cancellationTokenSource.Cancel(); // Stop timeout countdown
                tcs.TrySetResult(new BasisOwnershipResult(true, playerID));
            }
        }

        cancellationTokenSource.Token.Register(() =>
        {
            BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
            tcs.TrySetResult(BasisOwnershipResult.Failed);
        });

        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

        var ownershipTransferMessage = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId,
            },
            ownershipID = UniqueNetworkId
        };

        var netDataWriter = new NetDataWriter();
        ownershipTransferMessage.Serialize(netDataWriter);

        var peer = BasisNetworkManagement.LocalPlayerPeer;
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

        cancellationTokenSource.CancelAfter(timeoutMs);
        return await tcs.Task;
    }
}
