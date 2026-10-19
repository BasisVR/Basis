using Unity.Scripting.LifecycleManagement;
using System;
using System.Text;
using System.Threading.Tasks;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

/// <summary>
/// Shared playback for a <see cref="BasisMediaPlayer"/>: one owner drives the
/// URL, the transport commands and the playhead, and every other client
/// follows.
///
/// Convergence is the engine's job, not this component's. The owner broadcasts
/// its position on a heartbeat and receivers hand it to
/// <see cref="BasisMediaPlayer.SetSyncTarget"/>, which corrects through a dead
/// band, then a bounded rate slew, then a seek as the last resort, and
/// extrapolates the target at 1x between beats. Live sources take no target at
/// all: they have no shared timeline to land on, and divergence is bounded by
/// the player's own maxDivergenceMs instead.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BasisMediaPlayer))]
[NoAutoStaticsCleanup]
public sealed class BasisMediaPlayerNetworking : BasisNetworkBehaviour, IBasisMediaTickConsumer
{
    public enum SyncedPlaybackState : byte
    {
        Stopped = 0,
        Playing = 1,
        Paused = 2,
    }

    private enum MessageId : byte
    {
        FullState = 1,
        Play = 2,
        Pause = 3,
        Stop = 4,
        Seek = 5,
        RequestState = 6,
        Settings = 7,
        Position = 8,
    }

    [Flags]
    private enum SettingsFlags : byte
    {
        None = 0,
        AdminOnly = 1 << 0,
        AllowAnyoneToTakeControl = 1 << 1,
        AnyoneCanControl = 1 << 2,
    }

    // Matches the panel's Perm_Control constant; admins (perm "*") also satisfy it.
    public const string PermControl = "basis.mediaplayer.control";
    public const string PermAdmin = "*";

    [Header("Permissions")]
    [Tooltip("If true, only clients with the basis.mediaplayer.control or * permission may take ownership and control playback. Overrides AllowAnyoneToTakeControl.")]
    public bool AdminOnly = false;

    [Tooltip("If true, any client may take ownership and control playback. If false, only the current owner can call SetUrl/Play/Stop/Pause/Resume/Seek. Ignored when AdminOnly is true.")]
    public bool AllowAnyoneToTakeControl = true;

    [Tooltip("If true, clients WITHOUT the basis.mediaplayer.control (or *) permission may also load URLs and drive playback on this player, and the menu shows them the playback controls. Ignored when AdminOnly is true.")]
    public bool AnyoneCanControl = false;

    [Header("Sync")]
    [Tooltip("While playing seekable media, the owner broadcasts its position every this many seconds and receivers feed it to the engine's sync ladder. 0 disables the heartbeat, which leaves receivers free-running between transport commands.")]
    [Min(0f)] public float PositionHeartbeatSeconds = 3f;

    [Tooltip("Verbose log lines for join/leave sync, sync targets, rejected control attempts.")]
    public bool VerboseLogging = false;

    private static readonly Encoding UrlEncoding = new UTF8Encoding(false, false);
    // FullState payload after the 1-byte MessageId: [state:1][positionTicks:8][loadNonce:2][settingsFlags:1][urlLen:2] then url bytes.
    // positionTicks is 0 when the source is live (no seekable timeline); receivers treat 0 as "no position".
    // loadNonce bumps per SetUrl so re-loading the same URL is applied as a fresh load, not a no-op.
    private const int SettingsBlockSize = 1;
    private const int FullStateNonceOffset = 1 + 1 + 8;
    private const int FullStateSettingsOffset = FullStateNonceOffset + 2;
    private const int FullStateUrlLenOffset = FullStateSettingsOffset + SettingsBlockSize;
    private const int FullStateHeaderSize = FullStateUrlLenOffset + 2;
    private const int SettingsPayloadSize = 1 + SettingsBlockSize;
    private const int SeekPayloadSize = 1 + 8;
    // Position carries the owner's wall clock alongside the playhead, so a receiver
    // can tell a stalled owner (wall advancing, playhead frozen) from a paused one
    // and stop feeding the ladder a target it would drag itself backwards towards.
    private const int PositionPayloadSize = 1 + 8 + 8;

    private const float PendingApplyStaleSeconds = 60f;

    private const float ResyncAnswerTimeoutSeconds = 3f;

    // Cached single-byte command payloads; SendCustomNetworkEvent does not retain references.
    private static readonly byte[] PlayBytes = { (byte)MessageId.Play };
    private static readonly byte[] PauseBytes = { (byte)MessageId.Pause };
    private static readonly byte[] StopBytes = { (byte)MessageId.Stop };
    private static readonly byte[] RequestStateBytes = { (byte)MessageId.RequestState };

    private BasisMediaPlayer mediaPlayer;
    private string currentSyncedUrl = string.Empty;

    /// <summary>The URL shared with peers for the current source: the input/page URL, not the per-client resolved stream.</summary>
    public string SyncedUrl => currentSyncedUrl;
    private bool sendOnNetworkReady;
    private bool sendOnNetworkReadyFreshLoad;
    private bool applyingRemoteCommand;
    private bool eventsHooked;
    private ushort loadNonce;
    private ushort lastAppliedLoadNonce;
    private bool syncedUrlFromSetUrl;
    private float heartbeatTimer;

    // Local playback state, sampled each frame: the player reports a state enum
    // rather than raising started/paused events, so transitions are detected here.
    private BmState lastObservedState = BmState.Idle;
    private bool lastObservedPlayWhenReady = true;
    private int lastObservedLoadGeneration;
    private bool announcedThisLoad;

    // Each SetUrl takes the next number; an older one that resumes after a newer
    // one (a slow answer, a slow control grant) stands down instead of loading.
    private int setUrlOperation;

    private string heldUrl;
    private int heldRequest;

    // Ownership can arrive without anyone asking for it: the framework's join-time
    // ownership query silently claims an ownerless object server-side and reports the
    // joiner as its owner. Such an implicit owner holds nothing but the scene's default
    // state, so it keeps acting like a passive receiver (accepts FullState, never
    // broadcasts, no heartbeat) until a local control action makes its state deliberate.
    // World scripts must drive playback through this component's async API for that
    // promotion to happen; calling TakeOwnershipAsync directly stays implicit.
    private bool deliberateControl;

    private bool IsDrivingOwner => IsOwnedLocallyOnClient && deliberateControl;

    /// <summary>In a session and following its owner, whose position is the
    /// truth for this player.</summary>
    internal bool IsFollowerInSession => HasNetworkID && !IsDrivingOwner;

    // Answering a joiner or a state request with only the scene default would spread
    // that default over the instance; an implicit owner answers once custodians have
    // fed it the synced state, a deliberate owner always.
    private bool CanAnswerStateQueries => IsOwnedLocallyOnClient && (deliberateControl || !string.IsNullOrEmpty(currentSyncedUrl)) && (heldUrl == null || pendingRemoteApply);

    // Owner state stashed while a remote URL loads locally (resolution and the
    // engine's own open are both asynchronous): applied once the session is
    // running, so a late joiner lands at the owner's position instead of at zero.
    private bool pendingRemoteApply;
    private SyncedPlaybackState pendingRemoteState;
    private long pendingRemotePositionTicks;
    private float pendingRemoteStashedAt;
    // The player's LoadGeneration when the load the stash waits on was asked for. A page
    // URL is resolved before it opens and the session it replaces runs on meanwhile, so
    // the stash belongs to whatever opens after this, never to the session still playing.
    private int pendingRemoteLoadGeneration;
    // The player was already in Error when that load was asked for, so an Error seen
    // before the load starts is the dead session's and says nothing about the new one.
    private bool pendingRemoteErrorAtRequest;

    // A resync this client asked for on its own behalf (ResyncEveryone). The stash above
    // is then our own state, so the pending-apply gate has to run even though we are the
    // driving owner, which it otherwise skips.
    private bool selfResyncApply;

    // Suppress the one ready-settle broadcast the reopened self-resync load would otherwise
    // send. ObserveLocalPlayback runs before the stash is applied in the same tick, so that
    // broadcast would carry a playhead still near zero and drag still-resolving peers back to
    // the start. The room already has our real state and position from ResyncEveryone's
    // up-front broadcast, and the heartbeat keeps it fresh. A separate flag rather than
    // pre-setting announcedThisLoad, because it has to survive the LoadGeneration-change
    // reset of that flag (the page-URL path bumps the generation asynchronously).
    private bool suppressResyncSettleBroadcast;

    // Ask-the-room local resync (ResyncLocal): a RequestState has gone out and we are waiting
    // for any owner/custodian FullState to answer it. On timeout we reload what we already
    // hold instead of waiting forever.
    private bool forcedResyncPending;
    private float resyncAnswerDeadline = -1f;

    // Last heartbeat seen from the owner, to spot a stalled playhead.
    private long lastOwnerPositionTicks = -1;
    private long lastOwnerWallTicks = -1;
    private bool syncTargetActive;

    // Main-thread scratch; Unity callbacks are serial so these don't need locking.
    private readonly ushort[] singleRecipient = new ushort[1];
    private readonly byte[] seekScratch = new byte[SeekPayloadSize];
    private readonly byte[] positionScratch = new byte[PositionPayloadSize];
    private readonly byte[] settingsScratch = new byte[SettingsPayloadSize];
    private byte[] fullStateScratch = Array.Empty<byte>();
    private byte[] cachedUrlBytes = Array.Empty<byte>();
    private string cachedUrlBytesSource;

    public BasisMediaPlayer MediaPlayer => mediaPlayer;

    public bool CanLocallyControl
    {
        get
        {
            if (!HasNetworkID)
            {
                return true;
            }

            if (IsOwnedLocallyOnClient)
            {
                return true;
            }

            if (IsLocalAdmin())
            {
                return true;
            }

            if (AdminOnly)
            {
                return false;
            }

            return AllowAnyoneToTakeControl || AnyoneCanControl;
        }
    }

    /// <summary>True when this player's controls are open to clients that hold no control permission.</summary>
    public bool ControlOpenToEveryone => AnyoneCanControl && !AdminOnly;

    public static bool IsLocalAdmin()
    {
        var perms = BasisNetworkManagement.LocalPermissions;
        return perms != null && (perms.Contains(PermAdmin) || perms.Contains(PermControl));
    }

    public void Awake()
    {
        TryGetComponent(out mediaPlayer);
    }

    private void OnEnable()
    {
        if (mediaPlayer == null)
        {
            TryGetComponent(out mediaPlayer);
        }

        HookPlayerEvents();
        if (mediaPlayer != null)
        {
            mediaPlayer.AddTickConsumer(this);
        }
    }

    private void OnDisable()
    {
        if (mediaPlayer != null)
        {
            mediaPlayer.RemoveTickConsumer(this);
        }

        UnhookPlayerEvents();
        ClearSyncTarget();
    }

    BasisMediaTickStage IBasisMediaTickConsumer.TickStage => BasisMediaTickStage.Networking;

    // Runs from the player's tick, after the poll: what is observed and
    // broadcast below is this frame's state and position.
    void IBasisMediaTickConsumer.MediaTick()
    {
        if (mediaPlayer == null)
        {
            return;
        }

        TickForcedResync();
        ObserveLocalPlayback();
        ApplyPendingRemoteStateWhenReady();
        BroadcastHeartbeat();
    }

    // The ask-the-room resync fell silent: nobody answered within the window, so reload what
    // we hold. A FullState answer clears forcedResyncPending in ApplyRemoteFullState before
    // this fires, so the fallback only runs when the room genuinely did not respond.
    private void TickForcedResync()
    {
        if (!forcedResyncPending || resyncAnswerDeadline < 0f
            || Time.realtimeSinceStartup < resyncAnswerDeadline)
        {
            return;
        }

        forcedResyncPending = false;
        resyncAnswerDeadline = -1f;
        if (VerboseLogging)
        {
            BasisDebug.LogWarning($"{nameof(BasisMediaPlayerNetworking)} local resync: nobody "
                + "answered, reloading what we hold.", BasisDebug.LogTag.Video);
        }

        ReloadSelfInPlace();
    }

    // Owner position heartbeat: a small latest-wins ping (Sequenced, like the
    // framework's other position streams) so receivers keep a fresh target for
    // the engine ladder. Only while playing seekable media: live sources have
    // no timeline to correct against.
    private void BroadcastHeartbeat()
    {
        if (PositionHeartbeatSeconds <= 0f) return;
        if (!HasNetworkID || !IsDrivingOwner) return;
        // A stash waiting to land (a self-resync reload in flight) means the playhead below
        // is the reopened load's near-zero one, not our real position. Broadcasting it would
        // drag resolving peers to the start.
        if (pendingRemoteApply) return;
        // A re-open's new session reads near zero until its restore seek lands; a beat then
        // would pull every follower back to the start.
        if (mediaPlayer.RestoringPosition) return;
        if (GetLocalState() != SyncedPlaybackState.Playing) return;
        if (mediaPlayer.DurationSeconds <= 0d) return;
        heartbeatTimer += Time.deltaTime;
        if (heartbeatTimer < PositionHeartbeatSeconds) return;
        heartbeatTimer = 0f;
        positionScratch[0] = (byte)MessageId.Position;
        WriteLong(positionScratch, 1, PositionTicks());
        WriteLong(positionScratch, 9, DateTime.UtcNow.Ticks);
        SendCustomNetworkEvent(positionScratch, DeliveryMethod.Sequenced);
    }

    public override void OnNetworkReady()
    {
        if (sendOnNetworkReady)
        {
            sendOnNetworkReady = false;
            bool freshLoad = sendOnNetworkReadyFreshLoad;
            sendOnNetworkReadyFreshLoad = false;
            BroadcastFullState(freshLoad);
        }
    }

    public override void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null)
        {
            return;
        }

        var local = BasisNetworkPlayer.LocalPlayer;
        if (local != null && player.playerId == local.playerId)
        {
            return;
        }

        if (IsOwnedLocallyOnClient)
        {
            if (!CanAnswerStateQueries)
            {
                return;
            }

            singleRecipient[0] = player.playerId;
            SendFullStateTo(singleRecipient);
            if (VerboseLogging)
            {
                BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} sent late-join state to player {player.playerId}.", BasisDebug.LogTag.Video);
            }

            return;
        }

        // Custodian answer: when the last controller disconnects the server wipes its
        // ownership and keeps no media state, so no owner is left to tell a joiner what
        // is playing and the joiner stays on the scene default forever. Every present
        // client holding settled synced state answers the joiner directly (the
        // BasisSyncedObject.OnPlayerJoined pattern); duplicates collapse on the joiner
        // because they all carry the same url and load nonce.
        if (string.IsNullOrEmpty(currentSyncedUrl) || pendingRemoteApply || heldUrl != null)
        {
            return;
        }

        if (HasPresentOwner())
        {
            return;
        }

        singleRecipient[0] = player.playerId;
        SendFullStateTo(singleRecipient);
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} sent custodian late-join state to player {player.playerId}.", BasisDebug.LogTag.Video);
        }
    }

    private bool HasPresentOwner()
        => BasisNetworkPlayers.OwnershipPairing.TryGetValue(clientIdentifier, out ushort ownerId)
           && BasisNetworkPlayers.GetPlayerById(ownerId, out _);

    public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
    {
        if (IsOwnedLocallyOnClient)
        {
            // We drive from here, so stop chasing the position we were handed.
            ClearSyncTarget();
            // Only deliberate ownership announces state. The implicit join-time grant
            // lands here holding just the scene default; broadcasting that would reset
            // the whole instance (content and settings) whenever anyone joined an
            // ownerless player.
            if (deliberateControl)
            {
                BroadcastFullState();
                return;
            }

            // Owning ourselves through that grant also means nobody will ever tell us
            // what is playing: the owner branch of RequestState is us, and the custodian
            // answer in OnPlayerJoined already ran while this component was still
            // unregistered. That is invisible for a player active at join time, but a
            // world that keeps its screen disabled until someone switches it on
            // registers long after every custodian has answered, so ask the room here.
            if (HasNetworkID && string.IsNullOrEmpty(currentSyncedUrl))
            {
                SendCustomNetworkEvent(RequestStateBytes, DeliveryMethod.ReliableOrdered, null);
                if (VerboseLogging)
                {
                    BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} owns an ownerless player implicitly and holds no state, asked the room.", BasisDebug.LogTag.Video);
                }
            }

            return;
        }

        deliberateControl = false;

        if (!HasNetworkID)
        {
            return;
        }

        // Holding nothing of our own, ask the room instead of only the owner. An owner
        // that holds the object through the join-time grant cannot answer (it is not a
        // driving owner and has no url), and an ownerless object has nobody to target at
        // all, so a targeted request is silence in both cases. Custodians answer a
        // broadcast only while they still read the object as ownerless, so a real
        // controlling owner keeps answering on its own and this adds no duplicate.
        if (string.IsNullOrEmpty(currentSyncedUrl))
        {
            SendCustomNetworkEvent(RequestStateBytes, DeliveryMethod.ReliableOrdered, null);
            if (VerboseLogging)
            {
                BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} holds no state, asked the room rather than owner {CurrentOwnerId}.", BasisDebug.LogTag.Video);
            }

            return;
        }

        ushort owner = CurrentOwnerId;
        var local = BasisNetworkPlayer.LocalPlayer;
        if (local != null && owner == local.playerId)
        {
            return;
        }

        singleRecipient[0] = owner;
        SendCustomNetworkEvent(RequestStateBytes, DeliveryMethod.ReliableOrdered, singleRecipient);
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} requested state from new owner {owner}.", BasisDebug.LogTag.Video);
        }
    }

    /// <summary>Nobody owns this player any more. Keep playing free-running rather
    /// than freezing on the departed owner's last target.</summary>
    public override void OnServerOwnershipDestroyed()
    {
        ClearSyncTarget();
    }

    /// <summary>Load a URL for the whole room.
    /// <see cref="TrySetUrlAsync"/> is the same call reporting whether the
    /// load went ahead.</summary>
    public Task SetUrl(string url) => TrySetUrlAsync(url);

    /// <summary>Load a URL for the whole room. Completes with true once the load
    /// has been issued, and with false when the user declined the URL, it was
    /// refused, a later load superseded it, or control could not be taken.</summary>
    public Task<bool> TrySetUrlAsync(string url) => TrySetUrlAsync(url, false);

    internal Task<bool> TrySetUrlAsync(string url, bool viewerAsked)
    {
        if (mediaPlayer == null || string.IsNullOrEmpty(url))
        {
            return Task.FromResult(false);
        }

        // Asked before anything is announced: a URL the user declines must never
        // reach the room.
        int operation = ++setUrlOperation;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediaPlayer.WhenApproved(
            url,
            approved => Complete(done, SetApprovedUrl(approved, operation, viewerAsked)),
            _ => done.TrySetResult(false),
            viewerAsked);
        return done.Task;
    }

    private static async void Complete(TaskCompletionSource<bool> done, Task<bool> load)
    {
        try
        {
            done.TrySetResult(await load);
        }
        catch (Exception e)
        {
            done.TrySetException(e);
        }
    }

    /// <summary>Does nothing: the engine's sync ladder decides when a follower
    /// seeks rather than slews, from its own measurements.</summary>
    [Obsolete("The Rust engine's sync ladder owns the seek threshold; SetDriftSeekThresholdSeconds does nothing. This member only exists so content written against the C player keeps loading.")]
    public Task SetDriftSeekThresholdSeconds(float value)
    {
        BasisDebug.LogWarningOnce("[BasisMedia] SetDriftSeekThresholdSeconds does nothing on this engine; the sync ladder owns the threshold.", BasisDebug.LogTag.Video);
        return Task.CompletedTask;
    }

    private async Task<bool> SetApprovedUrl(string url, int operation, bool viewerAsked)
    {
        if (string.IsNullOrEmpty(url) || operation != setUrlOperation)
        {
            return false;
        }

        if (!await AcquireControlAsync() || operation != setUrlOperation)
        {
            return false;
        }

        currentSyncedUrl = url;
        loadNonce++;
        // A new deliberate load has its own settle broadcast to send, and its own timeline;
        // an in-flight resync of the previous source must not carry over, or this media would
        // reload onto the old playhead (and the heartbeat would stay suppressed until it did).
        suppressResyncSettleBroadcast = false;
        pendingRemoteApply = false;
        selfResyncApply = false;
        heldUrl = null;
        heldRequest++;
        syncedUrlFromSetUrl = true;

        // FullState is the only message carrying a URL, so it goes out up front rather than
        // waiting for the session to come up: peers that never see a broadcast never learn
        // what to load. It also hides resolution latency: a page URL costs each client
        // seconds of yt-dlp work, and announcing immediately lets peers resolve in parallel
        // with us. Opening a session starts it playing, the later ready broadcast settles
        // state, and the position heartbeat keeps everyone converged.
        BroadcastFullState(freshLoad: true);

        BasisDebug.Log($"[BasisMedia] {mediaPlayer.name}: sharing '{BasisMediaUrlRouter.Redact(url)}'", BasisDebug.LogTag.Video);
        ClearSyncTarget();
        mediaPlayer.LoadUrl(url, viewerAsked);
        return true;
    }

    /// <summary>Force every client, this one included, back onto this player's current
    /// timeline. Same control gate as playback; a no-op when nothing is loaded, since
    /// announcing an empty URL would stop the room.</summary>
    public async Task ResyncEveryone()
    {
        if (mediaPlayer == null || string.IsNullOrEmpty(GetActiveUrl()))
        {
            return;
        }

        // Nothing playing here is not a timeline to resync the room to: broadcasting our
        // Stopped state would close every peer while this client reopens and plays alone.
        if (GetLocalState() == SyncedPlaybackState.Stopped)
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        if (string.IsNullOrEmpty(currentSyncedUrl))
        {
            currentSyncedUrl = GetActiveUrl();
        }

        // Bump the load nonce so peers read a fresh load of the URL they already hold and
        // reload onto our state and position, rather than the announcement collapsing into
        // a no-op (ApplyRemoteFullState treats an unchanged URL and nonce as nothing to do).
        loadNonce++;
        syncedUrlFromSetUrl = true;
        BroadcastFullState();
        ReloadSelfInPlace();
    }

    /// <summary>Re-align this client and nobody else. Asks the room for the current state and
    /// reloads onto the answer; if nobody answers within <see cref="ResyncAnswerTimeoutSeconds"/>
    /// it reloads what it already holds. Takes no ownership and needs no permission, so a
    /// viewer whose stream went bad can straighten itself out. An owner, or a client with
    /// nothing playing or no network id, has no one to ask and just reloads in place.</summary>
    public void ResyncLocal()
    {
        if (mediaPlayer == null || forcedResyncPending)
        {
            return;
        }

        // Nobody to ask: we drive the object, or we are not networked. Reload in place, which
        // adopts our own url if we hold no synced one (a directly-opened player).
        if (!HasNetworkID || IsDrivingOwner)
        {
            ReloadSelfInPlace();
            return;
        }

        // Networked and not the owner: ask the room, even with no synced url yet (a late joiner
        // or a directly-opened player), which is when the answer is most useful.
        // currentSyncedUrl is left untouched so this client does not briefly answer a joiner as
        // a custodian with an adopted local url; a silent room is handled by TickForcedResync.

        forcedResyncPending = true;
        resyncAnswerDeadline = Time.realtimeSinceStartup + ResyncAnswerTimeoutSeconds;
        SendCustomNetworkEvent(RequestStateBytes, DeliveryMethod.ReliableOrdered, null);
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} local resync: asked the room "
                + "for the current state.", BasisDebug.LogTag.Video);
        }
    }

    // Called just before the open a stash waits on.
    private void NotePendingLoadRequest()
    {
        pendingRemoteLoadGeneration = mediaPlayer.LoadGeneration;
        pendingRemoteErrorAtRequest = mediaPlayer.State == BmState.Error;
    }

    // Re-open what this client is showing without losing our place, so the initiator's own
    // screen is fixed by the same press that fixes the room. The stash is applied once the
    // re-opened session is running (ApplyPendingRemoteStateWhenReady); selfResyncApply lets
    // that run despite this client being the driving owner.
    private void ReloadSelfInPlace()
    {
        if (mediaPlayer == null)
        {
            return;
        }

        if (string.IsNullOrEmpty(currentSyncedUrl))
        {
            currentSyncedUrl = GetActiveUrl();
        }

        if (string.IsNullOrEmpty(currentSyncedUrl))
        {
            return;
        }

        // A stash already in flight is the one to keep: mid-reload the playhead reads near
        // zero, so re-capturing it here would throw away the place we are holding.
        if (!pendingRemoteApply)
        {
            pendingRemoteState = GetLocalState();
            pendingRemotePositionTicks = PositionTicks();
            pendingRemoteStashedAt = Time.realtimeSinceStartup;
            pendingRemoteApply = true;
        }

        selfResyncApply = true;
        suppressResyncSettleBroadcast = true;
        ClearSyncTarget();
        OpenSyncedUrl(currentSyncedUrl, true);
    }

    public async Task Play()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        StartOrResumeLocal(true);
    }

    public async Task Stop()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        pendingRemoteApply = false;
        selfResyncApply = false;
        suppressResyncSettleBroadcast = false;
        mediaPlayer.Stop();
        // Closing the session raises no event, so we broadcast directly.
        SendOwnerSimple(MessageId.Stop);
    }

    public async Task Pause()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        mediaPlayer.Pause();
    }

    public async Task Resume()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        StartOrResumeLocal(true);
    }

    public async Task Seek(TimeSpan position)
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        mediaPlayer.Seek(position.TotalSeconds);
    }

    public async Task SetAdminOnly(bool value)
    {
        if (AdminOnly == value)
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        AdminOnly = value;
        BroadcastSettings();
    }

    public async Task SetAllowAnyoneToTakeControl(bool value)
    {
        if (AllowAnyoneToTakeControl == value)
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        AllowAnyoneToTakeControl = value;
        BroadcastSettings();
    }

    public async Task SetAnyoneCanControl(bool value)
    {
        if (AnyoneCanControl == value)
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        AnyoneCanControl = value;
        BroadcastSettings();
    }

    private async Task<bool> AcquireControlAsync()
    {
        if (!HasNetworkID)
        {
            return true;
        }

        if (IsOwnedLocallyOnClient)
        {
            // A local control action promotes implicit ownership to deliberate: from
            // here our state is the authoritative one to announce.
            deliberateControl = true;
            return true;
        }

        if (!IsLocalAdmin())
        {
            if (AdminOnly)
            {
                if (VerboseLogging)
                {
                    BasisDebug.LogWarning($"{nameof(BasisMediaPlayerNetworking)} control rejected: AdminOnly is on and this client lacks the {PermControl} (or {PermAdmin}) permission.", BasisDebug.LogTag.Video);
                }

                return false;
            }

            if (!AllowAnyoneToTakeControl && !AnyoneCanControl)
            {
                if (VerboseLogging)
                {
                    BasisDebug.LogWarning($"{nameof(BasisMediaPlayerNetworking)} control rejected: AllowAnyoneToTakeControl and AnyoneCanControl are both false and this client is not the owner.", BasisDebug.LogTag.Video);
                }

                return false;
            }
        }

        var result = await TakeOwnershipAsync();
        if (result.Success)
        {
            deliberateControl = true;
            ClearSyncTarget();
        }
        else if (VerboseLogging)
        {
            BasisDebug.LogWarning($"{nameof(BasisMediaPlayerNetworking)} ownership request was denied by the server.", BasisDebug.LogTag.Video);
        }

        return result.Success;
    }

    public override void OnNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod deliveryMethod)
    {
        if (buffer == null || buffer.Length < 1)
        {
            return;
        }

        var id = (MessageId)buffer[0];

        switch (id)
        {
            case MessageId.RequestState:
                if (CanAnswerStateQueries)
                {
                    singleRecipient[0] = senderId;
                    SendFullStateTo(singleRecipient);
                    return;
                }

                // The asker owns itself through the join-time grant, so the owner answer
                // above is the asker and nobody replies. Custodians answer it exactly as
                // they answer a joiner in OnPlayerJoined (the grant is unicast, so this
                // side still reads the object as ownerless), and duplicates collapse on
                // the asker because every copy carries the same url and load nonce.
                if (!IsOwnedLocallyOnClient && !pendingRemoteApply && heldUrl == null && !string.IsNullOrEmpty(currentSyncedUrl) && !HasPresentOwner())
                {
                    singleRecipient[0] = senderId;
                    SendFullStateTo(singleRecipient);
                    if (VerboseLogging)
                    {
                        BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} sent custodian state to player {senderId} on request.", BasisDebug.LogTag.Video);
                    }
                }

                return;

            case MessageId.FullState:
                if (IsDrivingOwner)
                {
                    return;
                }

                if (!TryDeserializeFullState(buffer, out string url, out var state, out long fullPos, out ushort fullNonce))
                {
                    return;
                }

                ApplyRemoteFullState(senderId, url, state, fullPos, fullNonce);
                return;

            case MessageId.Play:
                if (IsDrivingOwner)
                {
                    return;
                }

                ApplyRemotePlay();
                return;

            case MessageId.Pause:
                if (IsDrivingOwner)
                {
                    return;
                }

                ApplyRemotePause();
                return;

            case MessageId.Stop:
                if (IsDrivingOwner)
                {
                    return;
                }

                ApplyRemoteStop();
                return;

            case MessageId.Seek:
                if (IsDrivingOwner)
                {
                    return;
                }

                if (buffer.Length < SeekPayloadSize)
                {
                    return;
                }

                ApplyRemoteSeek(ReadLong(buffer, 1));
                return;

            case MessageId.Settings:
                if (IsDrivingOwner)
                {
                    return;
                }

                if (buffer.Length < SettingsPayloadSize)
                {
                    return;
                }

                ApplyRemoteSettings(buffer, 1);
                return;

            case MessageId.Position:
                if (IsDrivingOwner)
                {
                    return;
                }

                if (buffer.Length < PositionPayloadSize)
                {
                    return;
                }

                // Drift-only: state changes ride FullState and the transport
                // commands; the heartbeat never starts or pauses playback.
                if (GetLocalState() != SyncedPlaybackState.Playing)
                {
                    return;
                }

                ApplyOwnerPosition(ReadLong(buffer, 1), ReadLong(buffer, 9));
                return;
        }
    }

    // While a load is in flight the session on hand is the outgoing or still-opening one,
    // and the stash overwrites whatever it is told once the load lands. A transport command
    // arriving in that window is recorded on the stash instead. A negative position keeps
    // the stashed one, aged up to now when the owner was playing.
    private bool RestampPendingRemoteState(SyncedPlaybackState state, long positionTicks)
    {
        if (!pendingRemoteApply)
        {
            return false;
        }

        if (positionTicks < 0)
        {
            positionTicks = StashedPositionTicks();
        }

        pendingRemoteState = state;
        pendingRemotePositionTicks = positionTicks;
        pendingRemoteStashedAt = Time.realtimeSinceStartup;
        return true;
    }

    private long StashedPositionTicks() => pendingRemoteState == SyncedPlaybackState.Playing ? pendingRemotePositionTicks + (long)((Time.realtimeSinceStartup - pendingRemoteStashedAt) * TimeSpan.TicksPerSecond) : pendingRemotePositionTicks;

    private void ApplyRemotePlay()
    {
        if (RestampPendingRemoteState(SyncedPlaybackState.Playing, -1))
        {
            return;
        }

        applyingRemoteCommand = true;
        try
        {
            StartOrResumeLocal(false);
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemotePause()
    {
        if (RestampPendingRemoteState(SyncedPlaybackState.Paused, -1))
        {
            return;
        }

        applyingRemoteCommand = true;
        try
        {
            mediaPlayer.Pause();
            ClearSyncTarget();
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemoteStop()
    {
        applyingRemoteCommand = true;
        try
        {
            ClearSyncTarget();
            pendingRemoteApply = false;
            mediaPlayer.Stop();
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemoteSeek(long ticks)
    {
        if (ticks < 0)
        {
            return;
        }

        if (RestampPendingRemoteState(pendingRemoteState, ticks))
        {
            return;
        }

        applyingRemoteCommand = true;
        try
        {
            mediaPlayer.Seek(TimeSpan.FromTicks(ticks).TotalSeconds);
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemoteFullState(ushort senderId, string url, SyncedPlaybackState state, long positionTicks, ushort remoteLoadNonce)
    {
        // Any full state answers a pending ask-the-room resync. Capture that before clearing
        // it: a resync's whole job is to reload onto the answer, so it must force the reload
        // even when the answer carries the same url and nonce, or a stuck stream would be left
        // as-is (the unchanged-load path only start/resumes, it does not re-open).
        bool forceReload = forcedResyncPending;
        forcedResyncPending = false;
        resyncAnswerDeadline = -1f;

        // Reload when the URL changes, the owner issued a fresh load of the same URL (loadNonce
        // bumps per SetUrl), or a local resync asked for this answer. Never on an empty url,
        // which would wipe currentSyncedUrl.
        bool loadChanged = !string.IsNullOrEmpty(url) &&
            (forceReload || url != currentSyncedUrl || remoteLoadNonce != lastAppliedLoadNonce);

        // The same load re-announced while this client is still resolving it (a second
        // custodian answering the same join, or the owner's OnReady settle broadcast):
        // refresh the stashed snapshot instead of discarding it, otherwise the on-ready
        // position apply is lost and playback starts at zero.
        if (!loadChanged && pendingRemoteApply)
        {
            pendingRemoteState = state;
            pendingRemotePositionTicks = positionTicks;
            pendingRemoteStashedAt = Time.realtimeSinceStartup;
            return;
        }

        applyingRemoteCommand = true;
        pendingRemoteApply = false; /* superseded by whatever this state says */
        selfResyncApply = false;
        try
        {
            if (loadChanged)
            {
                if (!string.Equals(url, heldUrl, StringComparison.Ordinal))
                {
                    heldUrl = null;
                    heldRequest++;
                }

                currentSyncedUrl = url;
                lastAppliedLoadNonce = remoteLoadNonce;
                // Adopt the announced nonce as our own so a snapshot we later send (a
                // custodian answer, or owner state after an implicit grant) re-announces
                // this load under the same identity instead of forcing a reload.
                loadNonce = remoteLoadNonce;
                ClearSyncTarget();

                if (state == SyncedPlaybackState.Stopped)
                {
                    mediaPlayer.Stop();
                    return;
                }

                // Resolved CDN URLs are per-client and expiring: a page URL
                // (YouTube/Twitch/…) goes through the router and this client resolves
                // it itself. That and the engine's own open are asynchronous, and the
                // session starts playing as soon as it is up. The owner's position/pause
                // snapshot is stashed and applied once the session leaves Opening (aged
                // by the elapsed time); the heartbeat refines it after that.
                pendingRemoteState = state;
                pendingRemotePositionTicks = positionTicks;
                pendingRemoteStashedAt = Time.realtimeSinceStartup;
                pendingRemoteApply = true;
                BasisDebug.Log($"[BasisMedia] {mediaPlayer.name}: loading '{BasisMediaUrlRouter.Redact(url)}' "
                    + $"from player {senderId}, {state} at {TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s", BasisDebug.LogTag.Video);
                OpenSyncedUrl(url, forceReload);
                return;
            }

            switch (state)
            {
                case SyncedPlaybackState.Stopped:
                    ClearSyncTarget();
                    mediaPlayer.Stop();
                    break;

                case SyncedPlaybackState.Playing:
                    StartOrResumeLocal(false);
                    ApplyOwnerPosition(positionTicks, 0);
                    break;

                case SyncedPlaybackState.Paused:
                    mediaPlayer.Pause();
                    ClearSyncTarget();
                    if (positionTicks > 0)
                    {
                        mediaPlayer.Seek(TimeSpan.FromTicks(positionTicks).TotalSeconds);
                    }

                    break;
            }
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    // The stashed owner state lands once the local session has opened.
    // Runs on every client that does not drive state, which includes an implicit
    // owner being fed by custodians.
    private void ApplyPendingRemoteStateWhenReady()
    {
        // A self-initiated resync stashes our own state and then drives the reload, so it
        // has to pass this gate that an ordinary driving owner (broadcasting, not applying)
        // does not.
        if (!pendingRemoteApply || heldUrl != null || (IsDrivingOwner && !selfResyncApply))
        {
            return;
        }

        BmState state = mediaPlayer.State;
        bool loadStarted = mediaPlayer.LoadGeneration != pendingRemoteLoadGeneration;
        // Buffering is left out of `settling`. A seek and pause sent before the first frame
        // shows and the clock starts take effect before anything at 0 is seen or heard, and
        // the engine holds the pause across the seek. A page URL moves the generation when
        // the resolver takes it, while the session being replaced plays on: LoadPending says
        // the state on hand is still that session's.
        bool settling = state == BmState.Idle || state == BmState.Opening || mediaPlayer.LoadPending;
        // A resolve that fails reports Error without ever opening, and that releases the
        // stash too.
        bool failedBeforeOpening = !loadStarted && state == BmState.Error && !pendingRemoteErrorAtRequest;
        if (settling || (!loadStarted && !failedBeforeOpening))
        {
            // A load that never reaches playback (a resolve that fails back to Idle without
            // setting Error) would otherwise hold the stash forever, and with it the
            // heartbeat gate and the settle-broadcast suppression.
            if (Time.realtimeSinceStartup - pendingRemoteStashedAt > PendingApplyStaleSeconds)
            {
                pendingRemoteApply = false;
                selfResyncApply = false;
                suppressResyncSettleBroadcast = false;
            }

            return;
        }

        pendingRemoteApply = false;
        selfResyncApply = false;
        // The reason to skip one settle broadcast is gone once the reopened load lands (or
        // fails). Clearing it here also covers ownership moving away mid-reload, where
        // ObserveLocalPlayback's announce branch never runs to consume it.
        suppressResyncSettleBroadcast = false;
        if (state == BmState.Error)
        {
            return;
        }

        // The seek below is asynchronous, so the playhead can still read near zero on this
        // tick; make the next heartbeat wait a full interval rather than broadcast that.
        heartbeatTimer = 0f;

        applyingRemoteCommand = true;
        try
        {
            if (pendingRemotePositionTicks > 0 && mediaPlayer.DurationSeconds > 0d)
            {
                // The owner's snapshot aged while this client resolved and opened;
                // advance it by the elapsed time when the owner was playing. The
                // heartbeat corrects the residual.
                mediaPlayer.Seek(TimeSpan.FromTicks(StashedPositionTicks()).TotalSeconds);
            }

            // Opening a session starts it playing, so only the paused case needs acting on.
            if (pendingRemoteState == SyncedPlaybackState.Paused)
            {
                mediaPlayer.Pause();
            }
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    /// <summary>
    /// Hand the owner's playhead to the engine's sync ladder. Nothing is corrected
    /// here: the engine converges through a dead band, a bounded rate slew and
    /// finally a seek, and extrapolates the target at 1x between beats.
    /// </summary>
    private void ApplyOwnerPosition(long positionTicks, long ownerWallTicks)
    {
        if (positionTicks <= 0 || mediaPlayer.DurationSeconds <= 0d)
        {
            return;
        }

        // An owner whose wall clock is advancing while its playhead is not has
        // stalled (buffering, or wedged). Chasing a frozen target would drag this
        // client backwards, so hold position until it moves again.
        if (ownerWallTicks > 0 && lastOwnerWallTicks > 0 &&
            ownerWallTicks > lastOwnerWallTicks && positionTicks == lastOwnerPositionTicks)
        {
            lastOwnerWallTicks = ownerWallTicks;
            ClearSyncTarget();
            return;
        }

        lastOwnerPositionTicks = positionTicks;
        if (ownerWallTicks > 0)
        {
            lastOwnerWallTicks = ownerWallTicks;
        }

        double seconds = TimeSpan.FromTicks(positionTicks).TotalSeconds;
        mediaPlayer.SetSyncTarget(seconds);
        syncTargetActive = true;
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} sync target {seconds:F2}s (local {mediaPlayer.PositionSeconds:F2}s).", BasisDebug.LogTag.Video);
        }
    }

    private void ClearSyncTarget()
    {
        lastOwnerPositionTicks = -1;
        lastOwnerWallTicks = -1;
        if (!syncTargetActive || mediaPlayer == null)
        {
            return;
        }

        syncTargetActive = false;
        mediaPlayer.ClearSyncTarget();
    }

    /// <summary>Start playing, whichever state the session is in: resume a paused
    /// one, and re-open the synced URL when there is no session left to resume.</summary>
    private void StartOrResumeLocal(bool viewerAsked)
    {
        switch (mediaPlayer.State)
        {
            case BmState.Opening:
            case BmState.Playing:
                return;
            // A buffering session may be landing a seek it will pause on; Play withdraws
            // that pause and is otherwise a no-op there.
            case BmState.Buffering:
            case BmState.Paused:
                mediaPlayer.Play();
                return;
            default:
                string url = GetActiveUrl();
                if (string.IsNullOrEmpty(url))
                {
                    return;
                }

                if (viewerAsked)
                {
                    RestampPendingRemoteState(SyncedPlaybackState.Playing, -1);
                }

                OpenSyncedUrl(url, viewerAsked);
                return;
        }
    }

    private void OpenSyncedUrl(string url, bool viewerAsked)
    {
        if (!viewerAsked && string.Equals(url, heldUrl, StringComparison.Ordinal))
        {
            return;
        }

        int request = ++heldRequest;
        heldUrl = url;
        if (mediaPlayer.NeedsApproval(url))
        {
            BasisDebug.Log($"[BasisMedia] {mediaPlayer.name}: asking before opening '{BasisMediaUrlRouter.Redact(url)}'", BasisDebug.LogTag.Video);
            ClearSyncTarget();
            mediaPlayer.Stop();
        }

        mediaPlayer.WhenApproved(url, approved => OpenApprovedSyncedUrl(request, approved), viewerSaidNo => DeclineSyncedUrl(request, viewerSaidNo), viewerAsked);
    }

    private void OpenApprovedSyncedUrl(int request, string url)
    {
        if (request != heldRequest)
        {
            return;
        }

        heldUrl = null;
        if (RestampPendingRemoteState(pendingRemoteState, -1) && IsDrivingOwner)
        {
            selfResyncApply = true;
        }

        NotePendingLoadRequest();
        mediaPlayer.LoadUrl(url);
    }

    private void DeclineSyncedUrl(int request, bool viewerSaidNo)
    {
        if (request != heldRequest)
        {
            return;
        }

        selfResyncApply = false;
        suppressResyncSettleBroadcast = false;
        if (viewerSaidNo)
        {
            BasisDebug.Log($"[BasisMedia] {mediaPlayer.name}: not opening '{BasisMediaUrlRouter.Redact(heldUrl)}', the viewer declined it", BasisDebug.LogTag.Video);
            return;
        }

        heldUrl = null;
        pendingRemoteApply = false;
    }

    private void HookPlayerEvents()
    {
        if (eventsHooked || mediaPlayer == null)
        {
            return;
        }

        mediaPlayer.OnSeeked += HandleLocalSeeked;
        lastObservedState = mediaPlayer.State;
        lastObservedPlayWhenReady = mediaPlayer.PlayWhenReady;
        lastObservedLoadGeneration = mediaPlayer.LoadGeneration;
        // OnEnded is deliberately not acted on: end-of-stream is per-client. Every peer
        // plays the same source and reaches its own end; broadcasting a stop on the
        // owner's end would cut off any client still behind its playhead (a late
        // joiner, by its join latency). Deliberate stops broadcast from Stop() directly.
        eventsHooked = true;
    }

    private void UnhookPlayerEvents()
    {
        if (!eventsHooked || mediaPlayer == null)
        {
            return;
        }

        mediaPlayer.OnSeeked -= HandleLocalSeeked;
        eventsHooked = false;
    }

    /// <summary>
    /// Watches the player for a load reaching playback, and for the viewer's
    /// pause and resume, and broadcasts them when we are the owner.
    /// </summary>
    private void ObserveLocalPlayback()
    {
        BmState state = mediaPlayer.State;
        int generation = mediaPlayer.LoadGeneration;
        lastObservedState = state;
        bool wantsPlay = mediaPlayer.PlayWhenReady;
        bool previouslyWantedPlay = lastObservedPlayWhenReady;
        lastObservedPlayWhenReady = wantsPlay;

        if (generation != lastObservedLoadGeneration)
        {
            lastObservedLoadGeneration = generation;
            announcedThisLoad = false;
        }

        if (applyingRemoteCommand || !IsDrivingOwner)
        {
            return;
        }

        // First frame this load actually reached playback: settle peers on the real
        // URL, state and position. While a resolver holds the load, the state and position
        // on hand are the session being replaced, not this load's.
        if (!announcedThisLoad && !mediaPlayer.LoadPending
            && (state == BmState.Playing || state == BmState.Paused))
        {
            announcedThisLoad = true;
            AdoptActiveUrlIfUnset();
            // The queued load has arrived, so a fresh-load broadcast still waiting on a
            // network ID is superseded: from here the player's own state and position
            // are the truth.
            sendOnNetworkReadyFreshLoad = false;
            // A self-resync already announced the real state and position up front; its
            // reopened load reaches here before the stash is applied, so its playhead is
            // still near zero. Skip this one broadcast; the stash apply plus the heartbeat
            // carry the real position.
            if (suppressResyncSettleBroadcast)
            {
                suppressResyncSettleBroadcast = false;
                return;
            }
            BroadcastFullState();
            return;
        }

        // Transport replicates what the viewer asked for, not the engine's state. A seek on
        // a paused session passes through Buffering and lands paused by itself, and a Play
        // pressed before it lands is seen as Buffering to Playing, never Paused to Playing;
        // the intent flag moves exactly when Pause and Play are called.
        if (wantsPlay == previouslyWantedPlay)
        {
            return;
        }

        SendOwnerSimple(wantsPlay ? MessageId.Play : MessageId.Pause);
    }

    private void AdoptActiveUrlIfUnset()
    {
        // currentSyncedUrl is the URL we share. When SetUrl drove this load it's the
        // input/page URL peers must resolve themselves, so keep it: the resolved CDN URL
        // is per-client and expiring and works for no one else. When the load bypassed
        // SetUrl (a world script opening the player directly), adopt what it opened so we
        // don't keep broadcasting a stale URL.
        if (!syncedUrlFromSetUrl)
        {
            currentSyncedUrl = ResolveShareableUrl();
        }

        syncedUrlFromSetUrl = false;
    }

    private void HandleLocalSeeked(double seconds)
    {
        if (applyingRemoteCommand || !IsDrivingOwner || !HasNetworkID)
        {
            return;
        }

        seekScratch[0] = (byte)MessageId.Seek;
        WriteLong(seekScratch, 1, TimeSpan.FromSeconds(seconds).Ticks);
        SendCustomNetworkEvent(seekScratch, DeliveryMethod.ReliableOrdered);
    }

    private void SendOwnerSimple(MessageId id)
    {
        if (!HasNetworkID)
        {
            sendOnNetworkReady = true;
            return;
        }

        byte[] payload = id switch
        {
            MessageId.Play => PlayBytes,
            MessageId.Pause => PauseBytes,
            MessageId.Stop => StopBytes,
            MessageId.RequestState => RequestStateBytes,
            _ => new byte[] { (byte)id },
        };
        SendCustomNetworkEvent(payload, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastFullState(bool freshLoad = false)
    {
        if (!HasNetworkID)
        {
            sendOnNetworkReady = true;
            // A queued fresh load outranks a queued ordinary broadcast: the deferred send
            // still has to describe the pending load, not the source being replaced.
            sendOnNetworkReadyFreshLoad |= freshLoad;
            return;
        }

        SendCustomNetworkEvent(SerializeFullState(freshLoad), DeliveryMethod.ReliableOrdered);
    }

    private void SendFullStateTo(ushort[] recipients)
    {
        if (!HasNetworkID)
        {
            return;
        }

        SendCustomNetworkEvent(SerializeFullState(), DeliveryMethod.ReliableOrdered, recipients);
    }

    private SyncedPlaybackState GetLocalState()
    {
        switch (mediaPlayer.State)
        {
            case BmState.Paused:
                return SyncedPlaybackState.Paused;
            case BmState.Opening:
            case BmState.Buffering:
            case BmState.Playing:
                // A session mid-seek or still opening with a pause asked for is paused as
                // far as the room is concerned; the engine lands it paused.
                return mediaPlayer.PlayWhenReady ? SyncedPlaybackState.Playing : SyncedPlaybackState.Paused;
            default:
                return SyncedPlaybackState.Stopped;
        }
    }

    // The viewer's place: during a reopen's restoration that is where the session is
    // returning to, not the new session's start.
    private long PositionTicks() =>
        mediaPlayer.DurationSeconds > 0d
            ? TimeSpan.FromSeconds(mediaPlayer.RestoringPosition ? mediaPlayer.RestoringToSeconds : mediaPlayer.PositionSeconds).Ticks
            : 0L;

    /// <summary>The URL peers can act on: what the world or the menu asked for,
    /// never the per-client, expiring stream a resolver produced.</summary>
    private string ResolveShareableUrl()
    {
        BasisResolvedMedia media = mediaPlayer.Media;
        if (media == null)
        {
            // Nothing resolved this load, so what the player was opened with is
            // the URL itself.
            return mediaPlayer.url ?? string.Empty;
        }

        // A resolver handled it. Its page URL is the only shareable one; the
        // player's own URL is now the stream that was extracted, which is issued
        // per client and expires. Sharing nothing beats sharing that.
        return media.SourceUrl ?? string.Empty;
    }

    private string GetActiveUrl() =>
        !string.IsNullOrEmpty(currentSyncedUrl) ? currentSyncedUrl : ResolveShareableUrl();

    // freshLoad describes the load we are about to start rather than the source still
    // loaded: the player has not swapped over yet, so its state and position still belong
    // to the outgoing media and would otherwise be applied as the new source's start
    // position on peers.
    private byte[] SerializeFullState(bool freshLoad = false)
    {
        string url = GetActiveUrl();
        bool urlChanged = !string.Equals(cachedUrlBytesSource, url, StringComparison.Ordinal);
        if (urlChanged)
        {
            cachedUrlBytes = string.IsNullOrEmpty(url) ? Array.Empty<byte>() : UrlEncoding.GetBytes(url);
            if (cachedUrlBytes.Length > ushort.MaxValue)
            {
                BasisDebug.LogError($"{nameof(BasisMediaPlayerNetworking)} URL exceeds {ushort.MaxValue} bytes; truncating.", BasisDebug.LogTag.Video);
                Array.Resize(ref cachedUrlBytes, ushort.MaxValue);
            }

            cachedUrlBytesSource = url;
        }

        byte[] urlBytes = cachedUrlBytes;
        int totalSize = FullStateHeaderSize + urlBytes.Length;
        bool sizeChanged = fullStateScratch.Length != totalSize;
        if (sizeChanged)
        {
            fullStateScratch = new byte[totalSize];
            fullStateScratch[0] = (byte)MessageId.FullState;
            WriteUShort(fullStateScratch, FullStateUrlLenOffset, (ushort)urlBytes.Length);
        }

        if ((urlChanged || sizeChanged) && urlBytes.Length > 0)
        {
            Buffer.BlockCopy(urlBytes, 0, fullStateScratch, FullStateHeaderSize, urlBytes.Length);
        }

        // Opening a session starts it playing, so a load we are announcing ahead of
        // time is always announced as playing.
        // A pending stash means the live state and position belong to a session that is
        // still reopening (a self-resync), with a near-zero playhead. Late-join and
        // state-request answers carry the stashed snapshot instead, or a client that joins or
        // asks inside the reload window lands at zero.
        bool useStash = !freshLoad && pendingRemoteApply;
        fullStateScratch[1] = (byte)(freshLoad ? SyncedPlaybackState.Playing
            : useStash ? pendingRemoteState : GetLocalState());
        WriteLong(fullStateScratch, 2, freshLoad ? 0L
            : useStash ? StashedPositionTicks() : PositionTicks());
        WriteUShort(fullStateScratch, FullStateNonceOffset, loadNonce);
        WriteSettingsBlock(fullStateScratch, FullStateSettingsOffset);
        return fullStateScratch;
    }

    private byte[] SerializeSettings()
    {
        settingsScratch[0] = (byte)MessageId.Settings;
        WriteSettingsBlock(settingsScratch, 1);
        return settingsScratch;
    }

    private void WriteSettingsBlock(byte[] buf, int offset)
    {
        SettingsFlags flags = SettingsFlags.None;
        if (AdminOnly)
        {
            flags |= SettingsFlags.AdminOnly;
        }

        if (AllowAnyoneToTakeControl)
        {
            flags |= SettingsFlags.AllowAnyoneToTakeControl;
        }

        if (AnyoneCanControl)
        {
            flags |= SettingsFlags.AnyoneCanControl;
        }

        buf[offset] = (byte)flags;
    }

    private void ReadSettingsBlock(byte[] buf, int offset)
    {
        var flags = (SettingsFlags)buf[offset];
        AdminOnly = (flags & SettingsFlags.AdminOnly) != 0;
        AllowAnyoneToTakeControl = (flags & SettingsFlags.AllowAnyoneToTakeControl) != 0;
        AnyoneCanControl = (flags & SettingsFlags.AnyoneCanControl) != 0;
    }

    private void ApplyRemoteSettings(byte[] buffer, int offset)
    {
        ReadSettingsBlock(buffer, offset);
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} applied remote settings: AdminOnly={AdminOnly}, AllowAnyoneToTakeControl={AllowAnyoneToTakeControl}, AnyoneCanControl={AnyoneCanControl}.", BasisDebug.LogTag.Video);
        }
    }

    private void BroadcastSettings()
    {
        if (!HasNetworkID)
        {
            sendOnNetworkReady = true;
            return;
        }

        SendCustomNetworkEvent(SerializeSettings(), DeliveryMethod.ReliableOrdered);
    }

    private bool TryDeserializeFullState(byte[] buffer, out string url, out SyncedPlaybackState state, out long positionTicks, out ushort remoteLoadNonce)
    {
        url = string.Empty;
        state = SyncedPlaybackState.Stopped;
        positionTicks = 0;
        remoteLoadNonce = 0;
        if (buffer == null || buffer.Length < FullStateHeaderSize)
        {
            return false;
        }

        byte stateByte = buffer[1];
        if (stateByte > (byte)SyncedPlaybackState.Paused)
        {
            return false;
        }

        state = (SyncedPlaybackState)stateByte;
        positionTicks = ReadLong(buffer, 2);
        remoteLoadNonce = ReadUShort(buffer, FullStateNonceOffset);
        ReadSettingsBlock(buffer, FullStateSettingsOffset);
        ushort urlLen = ReadUShort(buffer, FullStateUrlLenOffset);
        if (buffer.Length < FullStateHeaderSize + urlLen)
        {
            return false;
        }

        if (urlLen > 0)
        {
            url = UrlEncoding.GetString(buffer, FullStateHeaderSize, urlLen);
        }

        return true;
    }

    private static void WriteLong(byte[] buf, int offset, long value)
    {
        for (int i = 0; i < 8; i++)
        {
            buf[offset + i] = (byte)(value >> (i * 8));
        }
    }

    private static long ReadLong(byte[] buf, int offset)
    {
        long v = 0;
        for (int i = 0; i < 8; i++)
        {
            v |= (long)buf[offset + i] << (i * 8);
        }

        return v;
    }

    private static void WriteUShort(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static ushort ReadUShort(byte[] buf, int offset)
    {
        return (ushort)(buf[offset] | (buf[offset + 1] << 8));
    }
}
