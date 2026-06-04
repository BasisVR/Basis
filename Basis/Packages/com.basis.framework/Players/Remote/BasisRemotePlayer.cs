using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.Avatar;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.UI.NamePlate;
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static SerializableBasis;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Remote (non-local) player representation used by the Basis SDK.
    /// Handles avatar creation/loading, remote eye/bone driving, network pose consumption,
    /// mesh LOD adjustments, and remote name plate lifecycle.
    /// </summary>
    /// <remarks>
    /// This class owns a number of runtime-only components and addressable resources.
    /// Call <see cref="OnDestroy"/> to dispose drivers and release addressable instances
    /// created during <see cref="RemoteInitialize(ClientAvatarChangeMessage, ClientMetaDataMessage, string)"/>.
    /// </remarks>
    [System.Serializable]
    public class BasisRemotePlayer : BasisPlayer
    {
        #region Drivers & Receivers
        /// <summary>
        /// Driver responsible for avatar-specific remote updates (e.g., bone jobs hookup).
        /// </summary>
        [Header("Avatar Driver")]
        [SerializeField]
        public BasisRemoteAvatarDriver RemoteAvatarDriver = new BasisRemoteAvatarDriver();

        /// <summary>
        /// Network receiver that provides pose/animation buffers and messages for this player.
        /// </summary>
        [Header("Network Receiver")]
        [SerializeField]
        public BasisNetworkReceiver NetworkReceiver;

        /// <summary>
        /// Network Face Driver that provides eye and blink support
        /// </summary>
        [Header("Face Driver")]
        [SerializeField]
        public BasisRemoteFaceDriver RemoteFaceDriver;
        #endregion

        #region UI / Name Plate

        /// <summary>
        /// Fired when this player's failed-avatar-load state may have changed.
        /// Listeners (e.g. the nameplate) refresh their visual to reflect the flag.
        /// </summary>
        public Action OnAvatarFailedStateChanged;

        /// <summary>
        /// Fired to display a chat message for this player. Empty string clears the message.
        /// </summary>
        public Action<string> OnChatMessageReceived;

        /// <summary>
        /// Fired when this player's transient chat typing state changes.
        /// </summary>
        public Action<bool> OnChatTypingStateChanged;

        /// <summary>
        /// Fired when something that affects nameplate active-state has changed
        /// (block, range, visibility settings).
        /// </summary>
        public Action OnNamePlateActiveStateShouldRefresh;

        /// <summary>
        /// This player's current outgoing talk mode, used to color their nameplate.
        /// Driven from the network by <see cref="BasisTalkModeManager"/>.
        /// </summary>
        public BasisTalkMode TalkMode = BasisTalkMode.Normal;

        /// <summary>
        /// Fired when <see cref="TalkMode"/> changes so the nameplate can recolor.
        /// </summary>
        public Action OnTalkModeChanged;

        public void SetTalkMode(BasisTalkMode mode)
        {
            if (TalkMode == mode) return;
            TalkMode = mode;
            OnTalkModeChanged?.Invoke();
        }

        /// <summary>
        /// Whether this player has muted their own microphone. Driven from the network
        /// by <see cref="BasisTalkModeManager"/> and shown on the nameplate.
        /// </summary>
        public bool IsSelfMuted;

        public void SetSelfMuted(bool muted)
        {
            if (IsSelfMuted == muted) return;
            IsSelfMuted = muted;
            OnTalkModeChanged?.Invoke();
        }

        /// <summary>
        /// Fired during <see cref="OnDestroy"/> so attached subsystems can tear themselves
        /// down without the player holding direct references to them.
        /// </summary>
        public Action OnRemotePlayerDestroying;

        /// <summary>
        /// Provider for the nameplate's world transform. The nameplate registers itself
        /// here in its Initialize and clears it in DeInitialize. Callers must null-check.
        /// </summary>
        public Func<Transform> NamePlateTransformProvider;

        /// <summary>
        /// A cached prefab instance for name plates loaded via Addressables.
        /// </summary>
        /// <remarks>
        /// This static cache is never unloaded in the current implementation (intentional memoization),
        /// which means memory is retained for the lifetime of the process.
        /// </remarks>
        public static GameObject NamePlate;

        #endregion

        #region State / Data

        /// <summary>
        /// Whether this remote player is currently considered out of interaction range
        /// from the local player (used by higher-level systems to gate updates or rendering).
        /// </summary>
        public bool OutOfRangeFromLocal = false;

        /// <summary>
        /// The most recent avatar change message received for this player.
        /// </summary>
        public ClientAvatarChangeMessage CACM;

        /// <summary>
        /// Whether the remote player is within the range where avatar rendering is allowed.
        /// </summary>
        public bool InAvatarRange = true;

        /// <summary>
        /// Debounce state for avatar range transitions. View-cone and avatar-cap checks
        /// can flip <c>pAvatarRange[i]</c> rapidly when the local player rotates or
        /// crowds shift, which would otherwise trigger a burst of ReloadAvatar calls and
        /// flash every affected player to the loading avatar. We require the new value to
        /// remain stable for <see cref="AvatarRangeDebounceSeconds"/> before committing.
        /// </summary>
        [System.NonSerialized] public bool PendingRangeActive;
        [System.NonSerialized] public bool PendingRangeTarget;
        [System.NonSerialized] public float PendingRangeCommitTime;
        public const float AvatarRangeDebounceSeconds = 0.5f;

        /// <summary>
        /// Current mesh LOD level (0 = closest, 3 = furthest). Set by BasisTransmissionResults.
        /// Used to control pose update frequency — distant players update less often.
        /// </summary>
        public short CurrentLodLevel;

        /// <summary>
        /// Frame counter for LOD-based pose skip. When > 0, SetHumanPose and muscle
        /// interpolation are skipped this frame. Decremented each frame.
        /// </summary>
        public byte PoseSkipCounter;

        /// <summary>
        /// The "always-requested" load mode for the avatar.
        /// <list type="bullet">
        /// <item><description><c>0</c> – Downloading/remote mode</description></item>
        /// <item><description><c>1</c> – Local mode</description></item>
        /// </list>
        /// </summary>
        public byte AlwaysRequestedMode; // 0 downloading, 1 local

        /// <summary>
        /// The last bundle requested for this player (used by <see cref="ReloadAvatar"/>).
        /// </summary>
        [HideInInspector]
        public BasisLoadableBundle AlwaysRequestedAvatar;

        /// <summary>
        /// Index into a remote player data array managed elsewhere (for external systems).
        /// </summary>
        public int RemotePlayerDataIndex;

        /// <summary>
        /// Optional transform indicating the mouth position, used by lip sync or VFX.
        /// </summary>
        public Transform MouthTransform;

        /// <summary>
        /// Stores the error message when the avatar fails to load or is not found.
        /// Reset to null when a real avatar is successfully loaded.
        /// </summary>
        public string AvatarLoadErrorMessage;

        /// <summary>
        /// Terminal "give up" flag for avatar loading. Set when <see cref="BasisAvatarFactory.LoadAvatarRemote"/>
        /// fails so we stop re-attempting on every range change. Cleared only when the
        /// local user manually toggles Hide/Show Avatar for this player.
        /// </summary>
        [System.NonSerialized]
        public bool HasFailedAvatarLoadGlobally;

        /// <summary>
        /// Runtime cache of the per-player block state, mirrored from
        /// <see cref="BasisPlayerSettingsData.IsBlocked"/>. When true, this player's
        /// audio, avatar, and nameplate are hidden on the local client.
        /// Refreshed during avatar load and toggled by the user settings UI.
        /// </summary>
        public bool IsBlocked;

        /// <summary>
        /// Transient networked chat typing state for this remote player.
        /// </summary>
        public bool IsChatTyping;

        /// <summary>
        /// Session-scoped "temp block" set when the remote side (this player) has blocked
        /// the local player, delivered via EventType_PlayerTempBlock. Not persisted.
        /// Combined with <see cref="IsBlocked"/> to determine effective visibility —
        /// whichever side of the pair blocked first wins on both ends.
        /// </summary>
        public bool TempBlocked;

        /// <summary>
        /// Client-side performance gate: set when the avatar's metadata header tripped
        /// one of the user's configured performance limits in
        /// <see cref="Basis.Scripts.Avatar.BasisAvatarPerformanceLimits"/>. Unlike
        /// <see cref="IsBlocked"/> this is automatically cleared when the user relaxes
        /// the relevant limit (the settings bridge reloads the avatar). Not persisted.
        /// </summary>
        [System.NonSerialized]
        public bool IsBlockedByPerformance;

        /// <summary>
        /// Human-readable reason string for the current performance block
        /// (e.g. "Exceeds triangles limit (250k > 200k)"). Null when not blocked.
        /// Drives nameplate / info panel messaging.
        /// </summary>
        [System.NonSerialized]
        public string PerformanceBlockReason;

        /// <summary>
        /// Full per-player result of the last avatar performance pass — hard-block
        /// status plus counts of components destroyed by each trim category. Filled
        /// in after every successful load by <see cref="Basis.Scripts.Avatar.BasisAvatarFactory"/>
        /// (trim categories) and <see cref="Basis.Scripts.Drivers.BasisRemoteAvatarDriver"/>
        /// (jiggle rig ingestion). Read by the individual player menu so the local
        /// user can see exactly what the filter did to this specific remote avatar.
        /// </summary>
        [System.NonSerialized]
        public Basis.Scripts.Avatar.BasisAvatarPerformanceLimits.PerformanceInfo LastPerformanceInfo;

        [System.NonSerialized]
        public bool RequiresPerformanceReval;

        /// <summary>
        /// Per-player override that tells the avatar performance filter to treat this
        /// remote as if no limits were enabled. Set from the individual-player menu
        /// so the local user can look at a specific avatar at full fidelity without
        /// touching their global caps or the session-wide
        /// <see cref="Basis.Scripts.Avatar.BasisAvatarPerformanceLimits.BypassAllLimits"/>
        /// toggle. Deliberately <see cref="System.NonSerializedAttribute"/> — resets
        /// to false every launch, every reconnect, and every fresh player join, so
        /// there's no accidental "I forgot I disabled the filter for Alice".
        /// </summary>
        [System.NonSerialized]
        public bool BypassPerformanceLimits;

        /// <summary>
        /// Effective block state: local persisted block OR remote session temp block.
        /// Performance blocks are deliberately not folded in here because they don't
        /// hide the player entirely — only swap the avatar mesh for the fallback.
        /// </summary>
        public bool IsEffectivelyBlocked => IsBlocked || TempBlocked;

        #endregion

        #region Initialization / Addressables

        /// <summary>
        /// Loads (and caches) a name plate prefab from Addressables and returns the cached instance.
        /// </summary>
        /// <param name="LoadableNamePlatename">The Addressables key or path for the name plate prefab.</param>
        /// <returns>The cached name plate <see cref="GameObject"/> instance.</returns>
        /// <remarks>
        /// This method uses a static cache and does not release the loaded asset.
        /// As noted in the code comment, this currently leaks memory for the lifetime of the process.
        /// </remarks>
        public static GameObject LoadFromHandle(string LoadableNamePlatename)
        {
            if (NamePlate == null)
            {
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op =
                    Addressables.LoadAssetAsync<GameObject>(LoadableNamePlatename);
                NamePlate = op.WaitForCompletion();
            }
            return NamePlate;
        }

        /// <summary>
        /// Initializes this remote player with network-transported identity and UI state,
        /// creating and attaching a name plate instance.
        /// </summary>
        /// <param name="cACM">Initial avatar change message for this player.</param>
        /// <param name="PlayerMetaDataMessage">Player metadata containing display name and UUID.</param>
        /// <param name="LoadableNamePlatename">
        /// Optional Addressables key/path for the name plate prefab.
        /// Defaults to <c>"Assets/UI/Prefabs/NamePlate.prefab"</c>.
        /// </param>
        public void RemoteInitialize(
            ClientAvatarChangeMessage cACM,
            ClientMetaDataMessage PlayerMetaDataMessage,
            string LoadableNamePlatename = "Assets/UI/Prefabs/NamePlate.prefab")
        {
            CACM = cACM;
            DisplayName = PlayerMetaDataMessage.playerDisplayName;
            PlayerPlatform = PlayerMetaDataMessage.playerPlatform;
            SetSafeDisplayname();
            this.name = DisplayName;
            UUID = PlayerMetaDataMessage.playerUUID;
            IsLocal = false;

            GameObject data = GameObject.Instantiate(LoadFromHandle(LoadableNamePlatename), transform);
            if (data.TryGetComponent(out BasisRemoteNamePlate plate))
            {
                if (this == null)
                {
                    AddressableResourceProcess.ReleaseGameobject(data);
                    return;
                }
                plate.Initialize(this);
            }
        }

        #endregion

        #region Avatar Loading

        /// <summary>
        /// Loads the avatar from an initial <see cref="ClientAvatarChangeMessage"/> if no avatar exists yet.
        /// </summary>
        /// <param name="CACM">The message containing the initial avatar payload/bytes.</param>
        /// <remarks>
        /// This is an async-void method intended to be fire-and-forget on the main thread.
        /// Prefer <see cref="CreateAvatar(byte, BasisLoadableBundle)"/> for awaited flows.
        /// </remarks>
        public void LoadAvatarFromInitial(ClientAvatarChangeMessage CACM)
        {
            if (BasisAvatar == null)
            {
                this.CACM = CACM;
                BasisLoadableBundle BasisLoadedBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(CACM.byteArray);

                InAvatarRange = false;

                if (BasisLoadedBundle != null)
                {
                    AlwaysRequestedAvatar = BasisLoadedBundle;
                    AlwaysRequestedMode = CACM.loadMode;

                    BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this,Vector3.zero, Quaternion.identity);
                }
                else
                {
                    AvatarLoadErrorMessage = "Invalid initial avatar data: failed to convert network bytes to loadable bundle";
                    BasisDebug.LogError("Invalid Initial Data");
                }
            }
        }

        /// <summary>
        /// Re-creates the avatar using the last requested mode and bundle,
        /// if available (used after settings or visibility changes).
        /// </summary>
        /// <remarks>
        /// This is an async-void method intended for fire-and-forget usage.
        /// </remarks>
        public async void ReloadAvatar()
        {
            if (AlwaysRequestedAvatar != null)
            {
                await CreateAvatar(AlwaysRequestedMode, AlwaysRequestedAvatar);
            }
        }
        public bool IsLoadingAnAvatar = false;
        private bool _reloadQueuedDuringLoad = false;
        /// <summary>
        /// Creates or replaces the current avatar using the provided load mode and bundle.
        /// Applies user visibility settings and distance gating before loading,
        /// and falls back to the loading avatar if not visible/in range.
        /// </summary>
        /// <param name="Mode">Avatar load mode (e.g., 0 = remote/downloading, 1 = local).</param>
        /// <param name="BasisLoadableBundle">The bundle describing the avatar to load.</param>
        /// <returns>A task that completes when the avatar is loaded or a fallback is applied.</returns>
        public async Task CreateAvatar(byte Mode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (BasisLoadableBundle == null || string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                AvatarLoadErrorMessage = "Avatar bundle was empty or null";
                BasisDebug.LogError("trying to create Avatar with empty Bundle", BasisDebug.LogTag.Remote);
                BasisLoadableBundle = BasisAvatarFactory.LoadingAvatar;
                Mode = 0;
            }

            // Remember last requested avatar and mode for potential reloads.
            AlwaysRequestedAvatar = BasisLoadableBundle;
            AlwaysRequestedMode = Mode;

            if (IsLoadingAnAvatar)
            {
                _reloadQueuedDuringLoad = true;
                return;
            }
            IsLoadingAnAvatar = true;
            BasisPlayerSettingsData BasisPlayerSettingsData = default;
            try
            {
                // Fetch per-player visibility settings.
                BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(UUID);

                // The await above is file I/O — the player can disconnect and be destroyed
                // mid-await. BasisAvatarFactory.CancelPlayerLoad can't help here because the
                // per-player cancellation token is created inside LoadAvatarRemote, after
                // this point. Bail before touching any Unity native members.
                if (this == null)
                {
                    return;
                }

                IsBlocked = BasisPlayerSettingsData.IsBlocked;

                bool effectivelyBlocked = IsEffectivelyBlocked;

                // Pre-load performance gate. Inspect the metadata header and refuse to
                // download/instantiate avatars that exceed any enabled limit. Skipped
                // for the fallback/loading avatar itself — otherwise a silly MaxBones=0
                // setting would block the fallback and leave the player headless.
                // Also skipped when this player has the per-player session bypass
                // enabled from the individual-player menu.
                BasisAvatarPerformanceLimits.Result perfResult =
                    (BasisAvatarFactory.IsLoadingAvatar(BasisLoadableBundle) || BypassPerformanceLimits)
                        ? BasisAvatarPerformanceLimits.Result.Pass
                        : BasisAvatarPerformanceLimits.Evaluate(BasisLoadableBundle.BasisBundleConnector);
                IsBlockedByPerformance = perfResult.Blocked;
                PerformanceBlockReason = perfResult.Blocked ? perfResult.Reason : null;

                // Reset the per-player performance report for this load. Trim counts
                // and jiggle ingestion stats are filled in later by BasisAvatarFactory
                // and BasisRemoteAvatarDriver respectively. We record the hard-block
                // result here so the individual-player menu has something to show even
                // if the avatar never makes it past the Evaluate gate.
                LastPerformanceInfo = new BasisAvatarPerformanceLimits.PerformanceInfo
                {
                    Blocked = perfResult.Blocked,
                    BlockReason = perfResult.Reason,
                };

                if (BasisPlayerSettingsData.AvatarVisible && !effectivelyBlocked && !IsBlockedByPerformance && InAvatarRange && !HasFailedAvatarLoadGlobally)
                {
                    await BasisAvatarFactory.LoadAvatarRemote(this, Mode, BasisLoadableBundle, Vector3.zero, Quaternion.identity);
                }
                else if (!IsConsideredFallBackAvatar)
                {
                    BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this,Vector3.zero, Quaternion.identity);
                }

                if (BasisAvatar != null)
                {
                    bool shouldBeActive = !effectivelyBlocked;
                    if (BasisAvatar.gameObject.activeSelf != shouldBeActive)
                    {
                        BasisAvatar.gameObject.SetActive(shouldBeActive);
                    }
                }
            }
            finally
            {
                // Always release the guard, even if RequestPlayerSettings / LoadAvatarRemote
                // throws — otherwise this player is permanently stuck and can never reload.
                IsLoadingAnAvatar = false;
            }

            if (_reloadQueuedDuringLoad)
            {
                _reloadQueuedDuringLoad = false;
                await CreateAvatar(AlwaysRequestedMode, AlwaysRequestedAvatar);
                return;
            }

            // Any terminal "pin to fallback" state must skip the range-based re-evaluation
            // below — otherwise the mismatch check fires every iteration (fallback is the
            // correct state for these, but the check reads it as drift) and ReloadAvatar
            // recurses forever, hanging Unity. Applies to: block, global load failure,
            // performance block, and the user hiding the avatar via the per-player menu.
            if (IsEffectivelyBlocked || HasFailedAvatarLoadGlobally || IsBlockedByPerformance || !BasisPlayerSettingsData.AvatarVisible)
            {
                return;
            }

            // If state drifted during the load, re-evaluate immediately.
            // Otherwise set cooldown to prevent oscillation.
            bool stateMismatch = (InAvatarRange && IsConsideredFallBackAvatar) || (!InAvatarRange && !IsConsideredFallBackAvatar);
            if (stateMismatch)
            {
                ReloadAvatar();
            }
        }

        #endregion

        #region Teardown

        /// <summary>
        /// Disposes owned drivers and releases addressable instances (name plate, bone jobs).
        /// </summary>
        public void OnDestroy()
        {
            if (RemoteFaceDriver != null)
            {
                RemoteFaceDriver.OnDestroy();
            }

            OnRemotePlayerDestroying?.Invoke();

            RemoveFromBoneDriver();
        }

        /// <summary>
        /// Unregisters this player from the bone job system. Must run while the avatar
        /// transforms are still alive (before any destroy) or the parallel SoA desyncs.
        /// Idempotent via the InBoneDriver guard.
        /// </summary>
        public void RemoveFromBoneDriver()
        {
            if (RemoteAvatarDriver.InBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(NetworkReceiver.playerId);
                RemoteAvatarDriver.InBoneDriver = false;
            }
        }

        #endregion

        #region LOD

        /// <summary>
        /// Computes and applies a mesh LOD level for all avatar renderers based on the
        /// distance to the local player and a reduction multiplier.
        /// </summary>
        /// Multiplier applied to the distance before mapping to LOD levels.
        /// Higher values cause LODs to drop off sooner.
        /// </param>
        public void ChangeMeshLOD(short grid)
        {
            if (BasisAvatar != null && BasisAvatar.Renders != null)
            {
                int length = BasisAvatar.Renders.Length;
                for (int Index = 0; Index < length; Index++)
                {
                    Renderer renderer = BasisAvatar.Renders[Index];
                    if (renderer != null)
                    {
                        renderer.forceMeshLod = grid;
                    }
                }
            }
        }

        #endregion
    }
}
