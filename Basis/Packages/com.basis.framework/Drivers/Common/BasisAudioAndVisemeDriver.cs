using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Connects a lip-sync pipeline to an avatar's facial rig by mapping phonemes/visemes to
    /// blendshapes and forwarding audio samples to the lip-sync engine.
    /// Supports dual backends: OpenLipSync (ONNX neural, 15 visemes) for the first 30 players,
    /// and uLipSync (MFCC, 6 phonemes) as fallback for the rest.
    /// </summary>
    [System.Serializable]
    public class BasisAudioAndVisemeDriver
    {
        /// <summary>
        /// Smoothing amount used by uLipSync (implementation-specific).
        /// </summary>
        public int smoothAmount = 70;

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
        public BasisPlayer Player;

        /// <summary>
        /// Avatar containing the viseme mesh and movement indices.
        /// </summary>
        public BasisAvatar Avatar;

        /// <summary>
        /// uLipSync core component that analyses incoming audio to phoneme weights.
        /// Always initialized as fallback.
        /// </summary>
        public BasisUlipSync uLipSync = new BasisUlipSync();

        /// <summary>
        /// OpenLipSync context for neural-network-based viseme processing (15 visemes).
        /// Null if this player does not have an OpenLipSync slot.
        /// </summary>
        public BasisOpenLipSyncContext openLipSyncContext;

        /// <summary>
        /// True if this player is using OpenLipSync instead of uLipSync.
        /// </summary>
        public bool UseOpenLipSync;

        /// <summary>
        /// Table mapping phoneme strings (e.g., "A", "E") to avatar blendshape indices.
        /// </summary>
        public List<BasisPhonemeBlendShapeInfo> phonemeBlendShapeTable = new List<BasisPhonemeBlendShapeInfo>();

        /// <summary>
        /// Tracks whether initialization completed successfully.
        /// </summary>
        public bool WasSuccessful;

        /// <summary>
        /// Cached instance ID of the face renderer used to safely bind/unbind events.
        /// </summary>
        public int HashInstanceID = -1;

        /// <summary>
        /// Attempts to configure lip-sync for the given player and avatar.
        /// First tries to acquire an OpenLipSync slot (30-player cap), then always
        /// initializes uLipSync as fallback.
        /// </summary>
        public bool TryInitialize(BasisPlayer BasisPlayer)
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
            if (Avatar.FaceVisemeMesh.sharedMesh.blendShapeCount == 0)
            {
                return false;
            }

            // --- OpenLipSync slot acquisition ---
            // Release any previous OpenLipSync slot
            if (openLipSyncContext != null)
            {
                BasisOpenLipSyncDriver.ReleaseSlot(BasisPlayer.GetEntityId());
                openLipSyncContext.Dispose();
                openLipSyncContext = null;
            }

            // Always try OpenLipSync first; only fall back to uLipSync on failure
            UseOpenLipSync = false;
            if (!BasisOpenLipSyncDriver.IsInitialized)
            {
                BasisOpenLipSyncDriver.Initialize();
            }
            if (BasisOpenLipSyncDriver.TryAcquireSlot(BasisPlayer.GetEntityId(), out uint ctxHandle))
            {
                openLipSyncContext = new BasisOpenLipSyncContext();
                openLipSyncContext.Initialize(Avatar, ctxHandle);
                UseOpenLipSync = true;
            }

            // --- uLipSync initialization (always, as fallback) ---
            phonemeBlendShapeTable.Clear();
            uLipSync.skinnedMeshRenderer = Avatar.FaceVisemeMesh;
            uLipSync.sharedMesh = Avatar.FaceVisemeMesh.sharedMesh;
            uLipSync.blendShapeCount = uLipSync.sharedMesh.blendShapeCount;
            // Build viseme availability and phoneme mapping table
            BlendShapeCount = Avatar.FaceVisemeMovement.Length;
            HasViseme = new bool[BlendShapeCount];

            for (int Index = 0; Index < BlendShapeCount; Index++)
            {
                if (Avatar.FaceVisemeMovement[Index] != -1)
                {
                    int FaceVisemeIndex = Avatar.FaceVisemeMovement[Index];
                    HasViseme[Index] = true;

                    // Map selected indices to uLipSync phoneme keys
                    switch (Index)
                    {
                        case 10:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "A", blendShape = FaceVisemeIndex });
                            break;
                        case 12:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "I", blendShape = FaceVisemeIndex });
                            break;
                        case 14:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "U", blendShape = FaceVisemeIndex });
                            break;
                        case 11:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "E", blendShape = FaceVisemeIndex });
                            break;
                        case 13:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "O", blendShape = FaceVisemeIndex });
                            break;
                        case 7:
                            phonemeBlendShapeTable.Add(new BasisPhonemeBlendShapeInfo { phoneme = "S", blendShape = FaceVisemeIndex });
                            break;
                    }
                }
                else
                {
                    HasViseme[Index] = false;
                }
            }

            // Push mappings into uLipSyncBlendShape
            uLipSync.CachedblendShapes.Clear();
            for (int i = 0; i < phonemeBlendShapeTable.Count; i++)
            {
                var info = phonemeBlendShapeTable[i];
                uLipSync.AddBlendShape(info.phoneme, info.blendShape);
            }
            uLipSync.BlendShapeInfos = uLipSync.CachedblendShapes.ToArray();

            // Wire visibility and lifetime callbacks (only once per renderer instance)
            if (Player != null && Player.FaceRenderer != null && HashInstanceID != Player.FaceRenderer.GetEntityId())
            {
                Player.FaceRenderer.Check += UpdateFaceVisibility;
                Player.FaceRenderer.DestroyCalled += TryShutdown;
            }
            uLipSync.Initalize();

            UpdateFaceVisibility(Player.FaceIsVisible);
            WasSuccessful = true;
            return true;
        }
        public void OnDestroy()
        {
            // Clean up OpenLipSync slot
            if (openLipSyncContext != null)
            {
                if (Player != null)
                {
                    BasisOpenLipSyncDriver.ReleaseSlot(Player.GetEntityId());
                }
                openLipSyncContext.Dispose();
                openLipSyncContext = null;
                UseOpenLipSync = false;
            }
            uLipSync.DisposeBuffers();
        }
        public void Simulate(float DeltaTime)
        {
            if (uLipSyncEnabledState == false || !InVisemeRange)
            {
                return;
            }

            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.Simulate(DeltaTime);
            }
            else
            {
                uLipSync.Simulate(DeltaTime);
            }
        }
        public void Apply()
        {
            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.Apply();
            }
            else
            {
                uLipSync.Apply();
            }
        }
        /// <summary>
        /// Attempts to cleanly shut down the driver, disabling processing and unbinding callbacks.
        /// </summary>
        public void TryShutdown()
        {
            WasSuccessful = false;
            OnDeInitalize();
        }

        /// <summary>
        /// Current enabled state of lip-sync processing based on face visibility.
        /// </summary>
        public bool uLipSyncEnabledState = true;

        /// <summary>
        /// Set by BasisTransmissionResults: false when the player is too far away
        /// for lip-sync to be visually meaningful (beyond half the hearing range).
        /// Checked in Simulate and ProcessAudioSamples to skip expensive work.
        /// </summary>
        public volatile bool InVisemeRange = true;

        /// <summary>
        /// Callback that updates whether lip-sync is active based on face visibility.
        /// </summary>
        private void UpdateFaceVisibility(bool State)
        {
            uLipSyncEnabledState = State;
            openLipSyncContext?.SetFaceVisible(State);
        }

        /// <summary>
        /// Unbinds face renderer callbacks if the same renderer instance is still present.
        /// </summary>
        public void OnDeInitalize()
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
        /// Forwards raw audio samples to the active lip-sync backend when enabled and initialized.
        /// </summary>
        public void ProcessAudioSamples(float[] data, int channels, int Length)
        {
            if (uLipSyncEnabledState == false || !InVisemeRange)
            {
                return;
            }

            if (WasSuccessful == false)
            {
                return;
            }

            if (UseOpenLipSync && openLipSyncContext != null)
            {
                openLipSyncContext.ProcessAudioSamples(data, channels, Length);
            }
            else
            {
                uLipSync.OnDataReceived(data, channels, Length);
            }
        }

        /// <summary>
        /// External pause/resume hook for lip-sync playback.
        /// </summary>
        public void OnPausedEvent(bool IsPaused)
        {
            if (IsPaused)
            {
                if (UseOpenLipSync && openLipSyncContext != null)
                {
                    openLipSyncContext.ZeroVisemes();
                }
                else
                {
                    foreach (BasisPhonemeBlendShapeInfo blendshapeIndex in phonemeBlendShapeTable)
                    {
                        Avatar.FaceVisemeMesh.SetBlendShapeWeight(blendshapeIndex.blendShape, 0);
                    }
                }
            }
        }
    }
}
