using System;
using System.Text;
using System.Threading.Tasks;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BasisMediaPlayer))]
public sealed class BasisMediaPlayerNetworking : BasisNetworkBehaviour
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
    [Tooltip("Remote clients seek to catch up when their position drifts more than this many seconds from the owner's last-broadcast position. Set to 0 to disable drift correction.")]
    [Min(0f)] public float DriftSeekThresholdSeconds = 2f;

    [Tooltip("While playing seekable media, the owner broadcasts its position every this many seconds so passive clients re-converge between state events. 0 disables the heartbeat.")]
    [Min(0f)] public float PositionHeartbeatSeconds = 5f;

    [Tooltip("Verbose log lines for join/leave sync, drift corrections, rejected control attempts.")]
    public bool VerboseLogging = false;

    private static readonly Encoding UrlEncoding = new UTF8Encoding(false, false);
    // FullState payload after the 1-byte MessageId: [state:1][positionTicks:8][loadNonce:2][settingsFlags:1][driftSec:4][urlLen:2] then url bytes.
    // positionTicks is 0 when the source is live (no seekable timeline); receivers treat 0 as "no position".
    // loadNonce bumps per SetUrl so re-loading the same URL is applied as a fresh load, not a no-op.
    private const int SettingsBlockSize = 1 + 4;
    private const int FullStateNonceOffset = 1 + 1 + 8;
    private const int FullStateSettingsOffset = FullStateNonceOffset + 2;
    private const int FullStateUrlLenOffset = FullStateSettingsOffset + SettingsBlockSize;
    private const int FullStateHeaderSize = FullStateUrlLenOffset + 2;
    private const int SettingsPayloadSize = 1 + SettingsBlockSize;
    private const int SeekPayloadSize = 1 + 8;

    // Cached single-byte command payloads; SendCustomNetworkEvent does not retain references.
    private static readonly byte[] PlayBytes = { (byte)MessageId.Play };
    private static readonly byte[] PauseBytes = { (byte)MessageId.Pause };
    private static readonly byte[] StopBytes = { (byte)MessageId.Stop };
    private static readonly byte[] RequestStateBytes = { (byte)MessageId.RequestState };

    private BasisMediaPlayer mediaPlayer;
    private string currentSyncedUrl = string.Empty;

    /// <summary>The URL shared with peers for the current source — the input/page URL, not the per-client resolved stream.</summary>
    public string SyncedUrl => currentSyncedUrl;
    private bool sendOnNetworkReady;
    private bool sendOnNetworkReadyFreshLoad;
    private bool applyingRemoteCommand;
    private bool eventsHooked;
    private ushort loadNonce;
    private ushort lastAppliedLoadNonce;
    private bool syncedUrlFromSetUrl;
    private float heartbeatTimer;

    // Ownership can arrive without anyone asking for it: the framework's join-time
    // ownership query silently claims an ownerless object server-side and reports the
    // joiner as its owner. Such an implicit owner holds nothing but the scene's default
    // state, so it keeps acting like a passive receiver (accepts FullState, never
    // broadcasts, no heartbeat) until a local control action makes its state deliberate.
    // World scripts must drive playback through this component's async API for that
    // promotion to happen; calling TakeOwnershipAsync directly stays implicit.
    private bool deliberateControl;

    private bool IsDrivingOwner => IsOwnedLocallyOnClient && deliberateControl;

    // Answering a joiner or a state request with only the scene default would spread
    // that default over the instance; an implicit owner answers once custodians have
    // fed it the synced state, a deliberate owner always.
    private bool CanAnswerStateQueries => IsOwnedLocallyOnClient && (deliberateControl || !string.IsNullOrEmpty(currentSyncedUrl));

    // Owner state stashed while a remote page URL resolves locally (async):
    // applied on the resolved source's OnReady, so a late joiner lands at the
    // owner's position instead of always starting at zero.
    private bool pendingRemoteApply;
    private SyncedPlaybackState pendingRemoteState;
    private long pendingRemotePositionTicks;
    private float pendingRemoteStashedAt;

    // Main-thread scratch — Unity callbacks are serial so these don't need locking.
    private readonly ushort[] singleRecipient = new ushort[1];
    private readonly byte[] seekScratch = new byte[SeekPayloadSize];
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
    }

    private void OnDisable()
    {
        UnhookPlayerEvents();
    }

    // Owner position heartbeat: a small latest-wins ping (Sequenced, like the
    // framework's other position streams) so passive clients re-converge and a
    // client that joined mid-resolve lands close. Only while playing seekable
    // media — live sources have no timeline to correct against.
    private void Update()
    {
        if (PositionHeartbeatSeconds <= 0f || mediaPlayer == null) return;
        if (!HasNetworkID || !IsDrivingOwner) return;
        if (!mediaPlayer.IsPlaying || mediaPlayer.IsPaused) return;
        if (mediaPlayer.Duration <= TimeSpan.Zero) return;
        heartbeatTimer += Time.deltaTime;
        if (heartbeatTimer < PositionHeartbeatSeconds) return;
        heartbeatTimer = 0f;
        seekScratch[0] = (byte)MessageId.Position;
        WriteLong(seekScratch, 1, mediaPlayer.Position.Ticks);
        SendCustomNetworkEvent(seekScratch, DeliveryMethod.Sequenced);
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
        if (string.IsNullOrEmpty(currentSyncedUrl) || pendingRemoteApply)
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
        // that holds the object through the join-time grant cannot answer - it is not a
        // driving owner and has no url - and an ownerless object has nobody to target at
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
        if (owner == 0)
        {
            return;
        }

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

    public async Task SetUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        currentSyncedUrl = url;
        loadNonce++;
        syncedUrlFromSetUrl = true;

        // FullState is the only message carrying a URL, so it goes out up front rather than
        // waiting on OnReady — peers that never see a broadcast never learn what to load. It
        // also hides resolution latency: a page URL costs each client seconds of yt-dlp work,
        // and announcing immediately lets peers resolve in parallel with us instead of starting
        // only after our OnReady. Peers auto-play their resolved source
        // (AutoPlayOnSourceAssigned), the later OnReady broadcast settles state, and the
        // position heartbeat keeps everyone converged.
        BroadcastFullState(freshLoad: true);

        mediaPlayer.LoadUrl(url);
        // OnReady fires BroadcastFullState once the source resolves, settling state/position.
    }

    public async Task Play()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        mediaPlayer.Play();
    }

    public async Task Stop()
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        mediaPlayer.Stop();
        // BasisMediaPlayer.Stop fires no event, so we broadcast directly.
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

        mediaPlayer.Resume();
    }

    public async Task Seek(TimeSpan position)
    {
        if (!await AcquireControlAsync())
        {
            return;
        }

        mediaPlayer.Seek(position);
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

    public async Task SetDriftSeekThresholdSeconds(float value)
    {
        float clamped = Mathf.Max(0f, value);
        if (Mathf.Approximately(DriftSeekThresholdSeconds, clamped))
        {
            return;
        }

        if (!await AcquireControlAsync())
        {
            return;
        }

        DriftSeekThresholdSeconds = clamped;
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
                // they answer a joiner in OnPlayerJoined - the grant is unicast, so this
                // side still reads the object as ownerless - and duplicates collapse on
                // the asker because every copy carries the same url and load nonce.
                if (!IsOwnedLocallyOnClient && !pendingRemoteApply && !string.IsNullOrEmpty(currentSyncedUrl) && !HasPresentOwner())
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

                ApplyRemoteFullState(url, state, fullPos, fullNonce);
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

                long seekTicks = ReadLong(buffer, 1);
                ApplyRemoteSeek(seekTicks);
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

                if (buffer.Length < SeekPayloadSize)
                {
                    return;
                }

                // Drift-only: state changes ride FullState and the transport
                // commands; the heartbeat never starts or pauses playback.
                if (!mediaPlayer.IsPlaying || mediaPlayer.IsPaused)
                {
                    return;
                }

                applyingRemoteCommand = true;
                try { MaybeCorrectDrift(ReadLong(buffer, 1)); }
                finally { applyingRemoteCommand = false; }
                return;
        }
    }

    private void ApplyRemotePlay()
    {
        applyingRemoteCommand = true;
        try
        {
            if (mediaPlayer.IsPlaying && mediaPlayer.IsPaused)
            {
                mediaPlayer.Resume();
            }
            else if (!mediaPlayer.IsPlaying)
            {
                mediaPlayer.Play();
            }
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemotePause()
    {
        applyingRemoteCommand = true;
        try
        {
            if (!mediaPlayer.IsPlaying)
            {
                mediaPlayer.Play();
            }

            if (!mediaPlayer.IsPaused)
            {
                mediaPlayer.Pause();
            }
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
            if (mediaPlayer.IsPlaying)
            {
                mediaPlayer.Stop();
            }
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

        applyingRemoteCommand = true;
        try
        {
            mediaPlayer.Seek(TimeSpan.FromTicks(ticks));
        }
        catch (NotSupportedException)
        {
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyRemoteFullState(string url, SyncedPlaybackState state, long positionTicks, ushort remoteLoadNonce)
    {
        // Reload when the URL changes OR the owner issued a fresh load of the same URL
        // (loadNonce bumps per SetUrl). Without the nonce, re-loading the same URL on the
        // owner would be a no-op here and the two clients would drift apart.
        bool loadChanged = !string.IsNullOrEmpty(url) &&
            (url != currentSyncedUrl || remoteLoadNonce != lastAppliedLoadNonce);

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
        try
        {
            if (loadChanged)
            {
                currentSyncedUrl = url;
                lastAppliedLoadNonce = remoteLoadNonce;
                // Adopt the announced nonce as our own so a snapshot we later send (a
                // custodian answer, or owner state after an implicit grant) re-announces
                // this load under the same identity instead of forcing a reload.
                loadNonce = remoteLoadNonce;

                // A page URL (YouTube/Twitch/…) is resolved per-client: resolved CDN URLs
                // are per-client and expiring, so they can't be shared. Route it through
                // LoadUrl so this client resolves the page URL itself. Resolution is async,
                // so the resolved source auto-plays via the player's default
                // AutoPlayOnSourceAssigned; the owner's position/pause snapshot is stashed
                // and applied on the resolved source's OnReady (aged by the resolve time),
                // then the heartbeat refines it.
                if (!BasisMediaUrlRouter.IsDirectlyPlayable(url))
                {
                    pendingRemoteState = state;
                    pendingRemotePositionTicks = positionTicks;
                    pendingRemoteStashedAt = Time.realtimeSinceStartup;
                    pendingRemoteApply = true;
                    mediaPlayer.LoadUrl(url);
                    return;
                }

                var media = BasisMediaSource.FromUrl(url);
                media.StartPosition = positionTicks > 0 ? TimeSpan.FromTicks(positionTicks) : TimeSpan.Zero;

                bool savedAutoPlay = mediaPlayer.AutoPlayOnSourceAssigned;
                mediaPlayer.AutoPlayOnSourceAssigned = state == SyncedPlaybackState.Playing || state == SyncedPlaybackState.Paused;
                mediaPlayer.LoadSource(media);
                mediaPlayer.AutoPlayOnSourceAssigned = savedAutoPlay;

                if (state == SyncedPlaybackState.Paused)
                {
                    mediaPlayer.Pause();
                }
                else if (state == SyncedPlaybackState.Stopped)
                {
                    mediaPlayer.Stop();
                }

                return;
            }

            switch (state)
            {
                case SyncedPlaybackState.Stopped:
                    if (mediaPlayer.IsPlaying)
                    {
                        mediaPlayer.Stop();
                    }

                    break;

                case SyncedPlaybackState.Playing:
                    if (!mediaPlayer.IsPlaying)
                    {
                        mediaPlayer.Play();
                    }
                    else if (mediaPlayer.IsPaused)
                    {
                        mediaPlayer.Resume();
                    }

                    MaybeCorrectDrift(positionTicks);
                    break;

                case SyncedPlaybackState.Paused:
                    if (!mediaPlayer.IsPlaying)
                    {
                        mediaPlayer.Play();
                    }

                    if (!mediaPlayer.IsPaused)
                    {
                        mediaPlayer.Pause();
                    }

                    MaybeCorrectDrift(positionTicks);
                    break;
            }
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void ApplyPendingRemoteState()
    {
        applyingRemoteCommand = true;
        try
        {
            if (pendingRemoteState == SyncedPlaybackState.Stopped)
            {
                if (mediaPlayer.IsPlaying) mediaPlayer.Stop();
                return;
            }

            // The owner's advertised state is authoritative here. Don't rely on the resolved
            // source having auto-started: AutoPlayOnSourceAssigned is the peer's own setting
            // and may be off, which would strand it stopped while the owner plays. The
            // direct-URL path forces the same thing around LoadSource. IsPlaying and IsPaused
            // are independent, so a paused source needs resuming rather than starting.
            if (pendingRemoteState == SyncedPlaybackState.Playing)
            {
                if (mediaPlayer.IsPlaying && mediaPlayer.IsPaused) mediaPlayer.Resume();
                else if (!mediaPlayer.IsPlaying) mediaPlayer.Play();
            }
            else if (pendingRemoteState == SyncedPlaybackState.Paused)
            {
                if (!mediaPlayer.IsPlaying) mediaPlayer.Play();
                if (!mediaPlayer.IsPaused) mediaPlayer.Pause();
            }

            if (pendingRemotePositionTicks > 0)
            {
                // The owner's snapshot aged while this client resolved; advance
                // it by the elapsed time when the owner was playing. The
                // heartbeat corrects the residual.
                long ticks = pendingRemotePositionTicks;
                if (pendingRemoteState == SyncedPlaybackState.Playing)
                    ticks += (long)((Time.realtimeSinceStartup - pendingRemoteStashedAt) * TimeSpan.TicksPerSecond);
                try { mediaPlayer.Seek(TimeSpan.FromTicks(ticks)); }
                catch (NotSupportedException) { /* resolved to a live/unindexed source */ }
            }
        }
        finally
        {
            applyingRemoteCommand = false;
        }
    }

    private void MaybeCorrectDrift(long positionTicks)
    {
        if (DriftSeekThresholdSeconds <= 0f)
        {
            return;
        }

        if (positionTicks <= 0)
        {
            return;
        }

        var target = TimeSpan.FromTicks(positionTicks);
        var current = mediaPlayer.Position;
        double diff = Math.Abs((target - current).TotalSeconds);
        if (diff <= DriftSeekThresholdSeconds)
        {
            return;
        }

        try
        {
            mediaPlayer.Seek(target);
            if (VerboseLogging)
            {
                BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} drift-corrected by {diff:F2}s.", BasisDebug.LogTag.Video);
            }
        }
        catch (NotSupportedException)
        {
            // Live / non-seekable sources can't drift-correct; that's fine.
        }
    }

    private void HookPlayerEvents()
    {
        if (eventsHooked || mediaPlayer == null)
        {
            return;
        }

        mediaPlayer.OnReady += HandleLocalReady;
        mediaPlayer.OnStarted += HandleLocalStarted;
        mediaPlayer.OnPaused += HandleLocalPaused;
        mediaPlayer.OnSeekCompleted += HandleLocalSeekCompleted;
        // OnEnded is deliberately not hooked: end-of-stream is per-client. Every peer plays
        // the same source and reaches its own end; broadcasting a stop on the owner's EOS
        // would cut off any client still behind its playhead (a late joiner, by its join
        // latency). Deliberate stops broadcast from Stop() directly.
        eventsHooked = true;
    }

    private void UnhookPlayerEvents()
    {
        if (!eventsHooked || mediaPlayer == null)
        {
            return;
        }

        mediaPlayer.OnReady -= HandleLocalReady;
        mediaPlayer.OnStarted -= HandleLocalStarted;
        mediaPlayer.OnPaused -= HandleLocalPaused;
        mediaPlayer.OnSeekCompleted -= HandleLocalSeekCompleted;
        eventsHooked = false;
    }

    private void HandleLocalReady()
    {
        // A remote page URL finished resolving locally: apply the owner state that
        // arrived with it. Runs on every client that does not drive state, which
        // includes an implicit owner being fed by custodians.
        if (pendingRemoteApply && !IsDrivingOwner)
        {
            pendingRemoteApply = false;
            ApplyPendingRemoteState();
        }

        if (applyingRemoteCommand)
        {
            return;
        }

        if (!IsDrivingOwner)
        {
            return;
        }

        // currentSyncedUrl is the URL we share. When SetUrl drove this load it's the input/page
        // URL peers must resolve themselves — keep it (overwriting with the resolved CDN URL
        // would broadcast a per-client/expiring URL that works for no one else). When the load
        // bypassed SetUrl (a direct LoadSource), adopt the active source's URL so we don't keep
        // broadcasting a stale one from an earlier SetUrl.
        if (!syncedUrlFromSetUrl)
        {
            var media = mediaPlayer.ActiveMediaSource;
            currentSyncedUrl = media != null && !string.IsNullOrEmpty(media.Uri) ? media.Uri : string.Empty;
        }
        syncedUrlFromSetUrl = false;

        // The queued load has arrived, so a fresh-load broadcast still waiting on a network
        // ID is superseded: from here the player's own state and position are the truth, and
        // later local commands must not be re-serialised as a pending load at position zero.
        sendOnNetworkReadyFreshLoad = false;
        BroadcastFullState();
    }

    private void HandleLocalStarted()
    {
        if (applyingRemoteCommand)
        {
            return;
        }

        if (!IsDrivingOwner)
        {
            return;
        }

        SendOwnerSimple(MessageId.Play);
    }

    private void HandleLocalPaused()
    {
        if (applyingRemoteCommand)
        {
            return;
        }

        if (!IsDrivingOwner)
        {
            return;
        }

        SendOwnerSimple(MessageId.Pause);
    }

    private void HandleLocalSeekCompleted(TimeSpan position)
    {
        if (applyingRemoteCommand)
        {
            return;
        }

        if (!IsDrivingOwner)
        {
            return;
        }

        if (!HasNetworkID)
        {
            return;
        }

        seekScratch[0] = (byte)MessageId.Seek;
        WriteLong(seekScratch, 1, position.Ticks);
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
        if (!mediaPlayer.IsPlaying)
        {
            return SyncedPlaybackState.Stopped;
        }

        return mediaPlayer.IsPaused ? SyncedPlaybackState.Paused : SyncedPlaybackState.Playing;
    }

    private string GetActiveUrl()
    {
        // Broadcast the URL that was set — the page URL for resolved sources, or the
        // direct stream URL — never the resolved CDN URL (per-client/expiring), so each
        // client resolves the page URL itself. currentSyncedUrl is back-filled from the
        // active source only when nothing was set explicitly (direct LoadSource).
        if (!string.IsNullOrEmpty(currentSyncedUrl))
        {
            return currentSyncedUrl;
        }

        var media = mediaPlayer.ActiveMediaSource;
        return media != null && !string.IsNullOrEmpty(media.Uri) ? media.Uri : string.Empty;
    }

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

        fullStateScratch[1] = (byte)(freshLoad
            ? (mediaPlayer.AutoPlayOnSourceAssigned ? SyncedPlaybackState.Playing : SyncedPlaybackState.Stopped)
            : GetLocalState());
        long positionTicks = !freshLoad && mediaPlayer.Duration > TimeSpan.Zero ? mediaPlayer.Position.Ticks : 0L;
        WriteLong(fullStateScratch, 2, positionTicks);
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
        WriteFloat(buf, offset + 1, DriftSeekThresholdSeconds);
    }

    private void ReadSettingsBlock(byte[] buf, int offset)
    {
        var flags = (SettingsFlags)buf[offset];
        AdminOnly = (flags & SettingsFlags.AdminOnly) != 0;
        AllowAnyoneToTakeControl = (flags & SettingsFlags.AllowAnyoneToTakeControl) != 0;
        AnyoneCanControl = (flags & SettingsFlags.AnyoneCanControl) != 0;
        float drift = ReadFloat(buf, offset + 1);
        if (drift < 0f || float.IsNaN(drift) || float.IsInfinity(drift))
        {
            drift = 0f;
        }

        DriftSeekThresholdSeconds = drift;
    }

    private void ApplyRemoteSettings(byte[] buffer, int offset)
    {
        ReadSettingsBlock(buffer, offset);
        if (VerboseLogging)
        {
            BasisDebug.Log($"{nameof(BasisMediaPlayerNetworking)} applied remote settings: AdminOnly={AdminOnly}, AllowAnyoneToTakeControl={AllowAnyoneToTakeControl}, AnyoneCanControl={AnyoneCanControl}, DriftSeekThresholdSeconds={DriftSeekThresholdSeconds}.", BasisDebug.LogTag.Video);
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

    private bool TryDeserializeFullState(byte[] buffer, out string url, out SyncedPlaybackState state, out long positionTicks, out ushort loadNonce)
    {
        url = string.Empty;
        state = SyncedPlaybackState.Stopped;
        positionTicks = 0;
        loadNonce = 0;
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
        loadNonce = ReadUShort(buffer, FullStateNonceOffset);
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

    private static void WriteFloat(byte[] buf, int offset, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        buf[offset] = (byte)bits;
        buf[offset + 1] = (byte)(bits >> 8);
        buf[offset + 2] = (byte)(bits >> 16);
        buf[offset + 3] = (byte)(bits >> 24);
    }

    private static float ReadFloat(byte[] buf, int offset)
    {
        int bits = buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);
        return BitConverter.Int32BitsToSingle(bits);
    }
}
