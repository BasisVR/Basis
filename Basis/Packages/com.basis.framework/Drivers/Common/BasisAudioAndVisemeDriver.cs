using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Connects an OpenLipSync (ONNX neural, 15 visemes) inference context to an avatar's
    /// facial rig. Contexts are created lazily when the player enters viseme range and
    /// released when they leave range, so only nearby players consume ONNX resources.
    /// This scales to 1000+ players because distant players never allocate inference contexts.
    /// </summary>
    [System.Serializable]
    public class BasisAudioAndVisemeDriver
    {
        /// <summary>
        /// Per-viseme availability flags derived from <c>Avatar.FaceVisemeMovement</c>.
        /// </summary>
        public bool[] HasViseme;

        /// <summary>
        /// Number of viseme entries on the avatar (length of <c>FaceVisemeMovement</c>).
        /// </summary>
        public int BlendShapeCount;

        /// <summary>
        /// Player whose avatar/renderer provide the viseme mesh and visibility state.
        /// </summary>
        public IBasisPlayer Player;

        /// <summary>
        /// Avatar containing the viseme mesh and movement indices.
        /// </summary>
        public BasisAvatar Avatar;

        /// <summary>
        /// OpenLipSync context for neural-network-based viseme processing (15 visemes).
        /// Created lazily when the player enters viseme range. Null when out of range
        /// or if the player doesn't have an OpenLipSync slot.
        /// </summary>
        public BasisOpenLipSyncContext openLipSyncContext;

        /// <summary>
        /// True when this driver currently holds an OpenLipSync inference context.
        /// </summary>
        public bool UseOpenLipSync;

        /// <summary>
        /// True if this player's avatar supports OpenLipSync (has a viseme mesh).
        /// The actual context is created lazily when they enter viseme range.
        /// </summary>
        public bool EligibleForOpenLipSync;

        /// <summary>
        /// Cached player entity ID, captured on the main thread during TryInitialize.
        /// Used for lazy OpenLipSync slot acquisition which can be triggered from the
        /// audio thread (where GetEntityId() cannot be called).
        /// </summary>
        private EntityId _cachedEntityId;

        /// <summary>
        /// Set by the audio thread when audio arrives while eligible for OpenLipSync
        /// but no context exists yet. Checked by Simulate() on the main thread to
        /// perform the actual context creation (which requires main thread access).
        /// </summary>
        private volatile bool _needsContext;

        /// <summary>
        /// Reference to the player's AudioSource. When disabled (player not speaking),
        /// the OpenLipSync context is released back to the pool for other speakers.
        /// Set by BasisAudioReceiver when the audio source is loaded.
        /// </summary>
        public AudioSource TrackedAudioSource;

        /// <summary>
        /// True when <see cref="TrackedAudioSource"/> is disabled (player not speaking).
        /// Maintained by BasisAudioReceiver so Simulate doesn't poll the native enabled flag.
        /// </summary>
        public bool AudioSourceInactive;

        /// <summary>
        /// Tracks whether initialization completed successfully.
        /// </summary>
        public bool WasSuccessful;

        /// <summary>
        /// Cached entity id of the face renderer used to safely bind/unbind events.
        /// </summary>
        public EntityId HashInstanceID = EntityId.None;

        /// <summary>
        /// Configures lip-sync for the given player and avatar. Records eligibility
        /// for OpenLipSync but defers context creation until the player is within
        /// viseme range (lazy allocation for 1000+ player scaling).
        /// </summary>
        public bool TryInitialize(IBasisPlayer BasisPlayer)
        {
            WasSuccessful = false;
            Avatar = BasisPlayer.BasisAvatar;
            Player = BasisPlayer;

            if (Avatar == null)
            {
                return false;
            }
            if (Avatar.FaceVisemeMesh == null)
            {
                return false;
            }
            if (Avatar.FaceVisemeMesh.sharedMesh == null)
            {
                return false;
            }
            if (Avatar.FaceVisemeMesh.sharedMesh.blendShapeCount == 0)
            {
                return false;
            }

            // Cache entity ID on the main thread — needed later for lazy slot
            // acquisition which can be triggered from the audio thread. The remote player
            // is a plain object, so key off its owned GameObject's entity id.
            BasisOpenLipSyncDriver.UnregisterSlotRevokedCallback(_cachedEntityId);
            _cachedEntityId = BasisPlayer.GameObject.GetEntityId();

            // Listen for slot evictions (e.g. MaxSlots lowered at runtime)
            BasisOpenLipSyncDriver.RegisterSlotRevokedCallback(_cachedEntityId, OnOpenLipSyncSlotRevoked);

            // Release any previous context.
            ReleaseOpenLipSyncContext();

            // Check eligibility but DON'T create context yet (lazy allocation).
            // Context will be created when the player enters viseme range.
            UseOpenLipSync = false;
            EligibleForOpenLipSync = false;
            EligibleForOpenLipSync = BasisOpenLipSyncDriver.IsInitialized;

            BlendShapeCount = Avatar.FaceVisemeMovement.Length;
            HasViseme = new bool[BlendShapeCount];
            for (int Index = 0; Index < BlendShapeCount; Index++)
            {
                HasViseme[Index] = Avatar.FaceVisemeMovement[Index] != -1;
            }

            // Wire visibility and lifetime callbacks (only once per renderer instance)
            if (Player != null && Player.FaceRenderer != null && HashInstanceID != Player.FaceRenderer.GetEntityId())
            {
                Player.FaceRenderer.Check += UpdateFaceVisibility;
                Player.FaceRenderer.DestroyCalled += TryShutdown;
            }

            UpdateFaceVisibility(Player.FaceIsVisible);
            WasSuccessful = true;
            return true;
        }

        /// <summary>
        /// Lazily acquires an OpenLipSync context for this player.
        /// Called when audio arrives and the player is in viseme range.
        /// </summary>
        private bool TryAcquireOpenLipSyncContext()
        {
            if (!EligibleForOpenLipSync || Avatar == null) return false;
            if (openLipSyncContext != null) return true; // already have one

            if (BasisOpenLipSyncDriver.TryAcquireSlot(_cachedEntityId, out uint ctxHandle))
            {
                openLipSyncContext = new BasisOpenLipSyncContext();
                openLipSyncContext.Initialize(Avatar, ctxHandle);
                UseOpenLipSync = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Releases the OpenLipSync context back to the pool.
        /// Called when the player leaves viseme range or on cleanup.
        /// </summary>
        private void ReleaseOpenLipSyncContext()
        {
            if (openLipSyncContext != null)
            {
                openLipSyncContext.ZeroVisemes();
                BasisOpenLipSyncDriver.ReleaseSlot(_cachedEntityId);
                openLipSyncContext.Dispose();
                openLipSyncContext = null;
                UseOpenLipSync = false;
            }
        }

        /// <summary>
        /// Called by BasisOpenLipSyncDriver when this player's slot is forcefully revoked.
        /// The backend context is already destroyed — just clean up the local state.
        /// </summary>
        private void OnOpenLipSyncSlotRevoked()
        {
            if (openLipSyncContext == null) return;

            openLipSyncContext.ZeroVisemes();
            openLipSyncContext.Dispose();
            openLipSyncContext = null;
            UseOpenLipSync = false;
        }

        public void OnDestroy()
        {
            BasisOpenLipSyncDriver.UnregisterSlotRevokedCallback(_cachedEntityId);
            ReleaseOpenLipSyncContext();
        }
        public void Simulate(float DeltaTime)
        {
            if (FaceVisible == false || !InVisemeRange)
            {
                // Player left viseme range — release the OpenLipSync context
                // so the slot can be used by a closer player.
                if (!InVisemeRange && openLipSyncContext != null)
                {
                    ReleaseOpenLipSyncContext();
                }
                return;
            }

            // Release context back to pool when audio source is inactive (player not speaking)
            if (UseOpenLipSync && openLipSyncContext != null && TrackedAudioSource != null && AudioSourceInactive)
            {
                ReleaseOpenLipSyncContext();
            }

            // Lazy context acquisition on the main thread.
            // The audio thread sets _needsContext when non-silent audio arrives
            // while in range but no OpenLipSync context exists yet.
            if (_needsContext)
            {
                _needsContext = false;
                TryAcquireOpenLipSyncContext();
            }

            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.Simulate(DeltaTime);
            }
        }
        private bool _overrideZeroed;

        public void Apply()
        {
            if (Basis.BasisUI.BasisSettingsDefaults.DisableLipSyncForFaceTracking.RawValue
                && Player is BasisRemotePlayer remote && remote.RemoteFaceDriver != null && remote.RemoteFaceDriver.OverrideViseme)
            {
                // Developer option (default off): a face-tracked remote drives the mouth via comms,
                // so suppress the audio-reconstructed visemes. Off = both run combined.
                if (!_overrideZeroed)
                {
                    openLipSyncContext?.ZeroVisemes();
                    _overrideZeroed = true;
                }
                return;
            }
            _overrideZeroed = false;

            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.Apply();
            }
        }
        /// <summary>
        /// Drops the mouth back to rest without releasing the OpenLipSync slot. Only the viseme
        /// blendshapes this driver owns are touched, so blink, face tracking and comms-driven
        /// expression shapes are left alone. Main thread only — SetBlendShapeWeight always is.
        /// </summary>
        public void ZeroVisemesNow()
        {
            openLipSyncContext?.ZeroVisemes();
        }

        /// <summary>
        /// Attempts to cleanly shut down the driver, disabling processing and unbinding callbacks.
        /// </summary>
        public void TryShutdown()
        {
            WasSuccessful = false;
            OnDeInitialize();
        }

        /// <summary>
        /// Current enabled state of viseme processing based on face renderer visibility.
        /// </summary>
        public bool FaceVisible = true;

        /// <summary>
        /// Set by BasisTransmissionResults: false when the player is too far away
        /// for lip-sync to be visually meaningful (beyond half the hearing range).
        /// Checked in Simulate and ProcessAudioSamples to skip expensive work.
        /// Mutate via <see cref="BasisRemoteAudioDriver.SetVisemeRange"/> so the
        /// active-driver list stays in sync.
        /// </summary>
        public volatile bool InVisemeRange = true;

        // Slot in BasisRemoteAudioDriver.Drivers; -1 when not registered.
        [System.NonSerialized] internal int RegisteredIndex = -1;

        // Slot in BasisRemoteAudioDriver.ActiveDrivers; -1 when out of range.
        [System.NonSerialized] internal int ActiveIndex = -1;

        /// <summary>
        /// Callback that updates whether viseme processing is active based on face visibility.
        /// </summary>
        private void UpdateFaceVisibility(bool State)
        {
            FaceVisible = State;
            openLipSyncContext?.SetFaceVisible(State);
        }

        /// <summary>
        /// Unbinds face renderer callbacks if the same renderer instance is still present.
        /// </summary>
        public void OnDeInitialize()
        {
            if (Player != null)
            {
                if (Player.FaceRenderer != null && HashInstanceID == Player.FaceRenderer.GetEntityId())
                {
                    Player.FaceRenderer.Check -= UpdateFaceVisibility;
                    Player.FaceRenderer.DestroyCalled -= TryShutdown;
                }
            }
        }

        /// <summary>
        /// Forwards raw audio samples to the OpenLipSync context when enabled and initialized.
        /// Lazily acquires an OpenLipSync context when the player first enters viseme range.
        /// </summary>
        public void ProcessAudioSamples(float[] data, int channels, int Length)
        {
            if (FaceVisible == false || !InVisemeRange)
            {
                return;
            }

            if (WasSuccessful == false)
            {
                return;
            }

            // Signal the main thread to acquire a pooled OpenLipSync context.
            // ProcessAudioSamples is only called when the AudioSource is enabled
            // (player is speaking), so any call here is a valid acquire signal.
            if (EligibleForOpenLipSync && !UseOpenLipSync)
            {
                _needsContext = true;
            }

            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.ProcessAudioSamples(data, channels, Length);
            }
        }

        /// <summary>
        /// External pause/resume hook for lip-sync playback.
        /// </summary>
        public void OnPausedEvent(bool IsPaused)
        {
            if (IsPaused && UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.ZeroVisemes();
            }
        }
    }
}
