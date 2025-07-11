using Basis.Network.Core;
using Basis.Scripts.Networking;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using static BasisNetworkCore.Serializable.SerializableBasis;

public static class BasisNetworkIdResolver
{

    public static ConcurrentDictionary<string, ushort> KnownIdMap = new ConcurrentDictionary<string, ushort>();
    private static ConcurrentDictionary<string, TaskCompletionSource<ushort>> PendingResolutions = new ConcurrentDictionary<string, TaskCompletionSource<ushort>>();
    private const int TimeoutMilliseconds = 10000; // 10 seconds

    public static async Task<BasisIdResolutionResult> ResolveAsync(string stringId)
    {
        if (string.IsNullOrEmpty(stringId))
        {
            BasisDebug.LogError("Invalid Request: stringId cannot be null or empty.", BasisDebug.LogTag.Networking);
            return new BasisIdResolutionResult(0, false);
        }

        if (KnownIdMap.TryGetValue(stringId, out ushort existingId))
        {
            return new BasisIdResolutionResult(existingId, true);
        }

        if (PendingResolutions.TryGetValue(stringId, out var existingTcs))
        {
            return await AwaitWithTimeout(existingTcs.Task, stringId);
        }

        var tcs = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingResolutions.TryAdd(stringId, tcs))
        {
            return await AwaitWithTimeout(PendingResolutions[stringId].Task, stringId);
        }

        try
        {
            NetDataWriter writer = new NetDataWriter();
            NetIDMessage requestMessage = new NetIDMessage { UniqueID = stringId };
            requestMessage.Serialize(writer);

            BasisNetworkManagement.LocalPlayerPeer.Send(writer, BasisNetworkCommons.netIDAssignChannel, LiteNetLib.DeliveryMethod.ReliableOrdered);

            return await AwaitWithTimeout(tcs.Task, stringId);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Exception while sending ID request for '{stringId}': {ex.Message}", BasisDebug.LogTag.Networking);
            PendingResolutions.TryRemove(stringId, out _);
            return new BasisIdResolutionResult(0, false);
        }
    }

    private static async Task<BasisIdResolutionResult> AwaitWithTimeout(Task<ushort> task, string stringId)
    {
        using var cts = new CancellationTokenSource(TimeoutMilliseconds);

        var completedTask = await Task.WhenAny(task, Task.Delay(TimeoutMilliseconds, cts.Token));
        if (completedTask == task)
        {
            try
            {
                var result = await task;
                return new BasisIdResolutionResult(result, true);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to resolve task for '{stringId}': {ex.Message}", BasisDebug.LogTag.Networking);
                PendingResolutions.TryRemove(stringId, out _);
                return new BasisIdResolutionResult(0, false);
            }
        }
        else
        {
            BasisDebug.LogError($"Timeout while waiting for ID resolution of '{stringId}'.", BasisDebug.LogTag.Networking);
            PendingResolutions.TryRemove(stringId, out _);
            return new BasisIdResolutionResult(0, false);
        }
    }

    public static void CompleteMessageDelegation(ServerNetIDMessage serverMessage)
    {
        string stringId = serverMessage.NetIDMessage.UniqueID;
        ushort resolvedId = serverMessage.UshortUniqueIDMessage.UniqueIDUshort;

        if (string.IsNullOrEmpty(stringId))
        {
            BasisDebug.LogError("Invalid Data: Cannot resolve null or empty stringId.", BasisDebug.LogTag.Networking);
            return;
        }

        if (KnownIdMap.TryAdd(stringId, resolvedId))
        {
            BasisDebug.Log($"Mapping Added: '{stringId}' → {resolvedId}", BasisDebug.LogTag.Networking);
        }
        else if (KnownIdMap.TryGetValue(stringId, out ushort existingId))
        {
            if (existingId != resolvedId)
            {
                BasisDebug.LogError($"ID Conflict: '{stringId}' already mapped to {existingId}, attempted to remap to {resolvedId}.", BasisDebug.LogTag.Networking);
            }
            else
            {
                BasisDebug.LogError($"Redundant Mapping: '{stringId}' already maps to {resolvedId}.", BasisDebug.LogTag.Networking);
            }
        }
        else
        {
            BasisDebug.LogError($"Unexpected Failure: Could not add or confirm mapping for '{stringId}'.", BasisDebug.LogTag.Networking);
        }

        if (PendingResolutions.TryRemove(stringId, out var tcs))
        {
            tcs.TrySetResult(resolvedId);
        }
    }
}
