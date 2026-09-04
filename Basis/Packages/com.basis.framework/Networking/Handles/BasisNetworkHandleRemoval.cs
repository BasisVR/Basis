using System;
using System.Collections.Concurrent;
using Basis.Scripts.Avatar;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Network.Core;
using UnityEngine;

public static class BasisNetworkHandleRemoval
{
    // Pending player-lifecycle work (joins + leaves), drained on the main thread
    // with a per-frame budget so a mass join/leave event can't stall the renderer.
    // Single queue preserves temporal order between joins and leaves.
    public static readonly ConcurrentQueue<Action> LifecycleQueue = new();

    /// <summary>
    /// Maximum number of join/leave actions to process per main-thread frame. Now a backstop
    /// rather than the real limiter — see <see cref="LifecycleBudgetMillisecondsPerFrame"/>.
    /// </summary>
    public static int LifecycleBudgetPerFrame = 20;

    /// <summary>
    /// Wall-clock ceiling on the lifecycle drain per frame.
    /// </summary>
    /// <remarks>
    /// The count above was documented as "tuned for ~1ms at ~60fps", but it cannot know what an
    /// entry costs. A join runs <c>CreateRemotePlayer</c>, and its spawn payload path installs the
    /// loading avatar — an Instantiate plus a full <c>RemoteCalibration</c>, profiled in the field
    /// at 0.285 ms each. Twenty of those is 5.8 ms, not 1 ms, and a join storm hits the count every
    /// frame until it drains. A time budget bounds the actual cost the way the count never could;
    /// leftover entries stay queued and drain on later frames exactly as an over-count did.
    /// </remarks>
    public static float LifecycleBudgetMillisecondsPerFrame = 1.5f;

    private static readonly System.Diagnostics.Stopwatch sLifecycleClock = new System.Diagnostics.Stopwatch();

    /// <summary>
    /// Drains queued lifecycle actions on the main thread until either budget is spent.
    /// Called every Update from BasisEventDriver.
    /// </summary>
    public static int ProcessLifecycleQueue(int maxPerFrame)
    {
        int processed = 0;
        long budgetTicks = (long)(LifecycleBudgetMillisecondsPerFrame * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        sLifecycleClock.Restart();
        while (processed < maxPerFrame && LifecycleQueue.TryDequeue(out Action action))
        {
            try { action.Invoke(); }
            catch (Exception ex) { BasisDebug.LogError($"Lifecycle action failed: {ex}"); }
            processed++;

            // Checked AFTER the action so the budget reflects what these joins actually cost
            // rather than a guess, and so the drain always makes at least one entry of progress —
            // a single heavy join can never starve the queue into never advancing.
            if (budgetTicks > 0 && sLifecycleClock.ElapsedTicks >= budgetTicks)
            {
                break;
            }
        }
        sLifecycleClock.Stop();
        return processed;
    }

    public static void HandleDisconnection(NetPacketReader reader)
    {
        while (reader.AvailableBytes >= sizeof(ushort))
        {
            if (!reader.TryGetUShort(out ushort disconnectValue))
            {
                BasisDebug.LogError("Tried to read disconnect message but data was missing!");
                break;
            }

            HandleDisconnectId(disconnectValue);
        }
    }

    public static void HandleDisconnectId(ushort disconnectedID)
    {
        if (BasisNetworkPlayer.LocalPlayer != null && disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
        {
            BasisDebug.LogError("LocalPlayer Matched Disconnected ID returning early");
            return;
        }

        // Defer to the budgeted main-thread queue so a burst of disconnects doesn't
        // chain N synchronous GameObject.Destroy / avatar-unload calls in one frame.
        LifecycleQueue.Enqueue(() => HandleDisconnectIdImmediate(disconnectedID));
    }

    public static void HandleDisconnectIdImmediate(ushort disconnectedID)
    {
        if (BasisNetworkPlayer.LocalPlayer != null && disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
        {
           // BasisDebug.LogError("LocalPlayer Matched Disconnected ID returning early");
            return;
        }

        // Remove from network manager
        if (BasisNetworkPlayers.RemovePlayer(disconnectedID, out BasisNetworkPlayer network) && network != null)
        {
            // Notify scripts about remote player leaving
            if (network.Player != null)
            {
                BasisNetworkPlayer.OnRemotePlayerLeft?.Invoke(network, (Basis.Scripts.BasisSdk.Players.BasisRemotePlayer)network.Player);
            }
            else
            {
                BasisDebug.LogError($"Missing Player for removing ID {disconnectedID}");
            }
            BasisNetworkPlayer.OnPlayerLeft?.Invoke(network);

            // Clean up any announce audio for this player
            BasisAnnounceAudioDriver.RemovePlayer(disconnectedID);
            Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.OnPlayerLeft(disconnectedID);

            // Notify avatar BasisAvatarMonoBehaviours that the network is going away
            // before the avatar GameObject is destroyed below.
            network.NotifyNetworkBehavioursTerminated();

            // Shutdown networking
            network.DeInitialize();

            // Cancel any in-flight async avatar load to prevent orphaned avatar GameObjects
            if (network.Player != null)
            {
                BasisAvatarFactory.CancelPlayerLoad(network.Player);
            }

            // Tear the remote player down BEFORE its avatar is unloaded, while the avatar's
            // FaceRenderer is still alive: OnDestroy unregisters from the bone job, fires
            // OnRemotePlayerDestroying (the nameplate unsubscribes from the renderer-visibility
            // callback and releases itself) and drops the mouth marker. If this ran after
            // DeleteLastAvatar, the avatar's OnBecameInvisible could fire into a released nameplate.
            if (network.Player is Basis.Scripts.BasisSdk.Players.BasisRemotePlayer remoteToDestroy)
            {
                remoteToDestroy.OnDestroy();
            }

            if (network.Player != null)
            {
                BasisAvatarFactory.DeleteLastAvatar(network.Player);
            }
            else
            {
                BasisDebug.LogError($"B Missing Player for removing ID {disconnectedID}");
            }
        }
        else
        {
            BasisDebug.LogErrorOnce($"Disconnect for unregistered player id {disconnectedID} (duplicate, late, or never joined); skipping teardown.", BasisDebug.LogTag.Networking);
        }
    }
}
