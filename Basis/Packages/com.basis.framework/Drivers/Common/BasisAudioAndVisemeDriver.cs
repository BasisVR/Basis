using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Connects a <c>uLipSync</c> pipeline to an avatar's facial rig by mapping phonemes to
    /// blendshapes and forwarding audio samples to the lip-sync engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initialization validates avatar resources, builds a phoneme→blendshape table from the avatar's
    /// <c>FaceVisemeMovement</c>, configures <see cref="uLipSyncBlendShape"/> with those mappings,
    /// and wires visibility callbacks so processing pauses when the face is hidden.
    /// </para>
    /// <para>
    /// Call <see cref="ProcessAudioSamples(float[], int, int)"/> each audio frame to drive lip sync.
    /// Call <see cref="TryInitialize(BasisPlayer)"/> when the player/avatar becomes available and
    /// <see cref="TryShutdown"/> on teardown.
    /// </para>
    /// </remarks>
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
        /// </summary>
        public BasisUlipSync uLipSync = new BasisUlipSync();

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
        /// Attempts to configure uLipSync for the given <paramref name="BasisPlayer"/> and avatar.
        /// </summary>
        /// <param name="BasisPlayer">The player whose avatar provides viseme resources.</param>
        /// <returns><c>true</c> if setup succeeded; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Validates the avatar and mesh, creates or reuses <see cref="uLipSync"/> and
        /// <see cref="uLipSyncBlendShape"/> components, assigns the shared lip-sync profile,
        /// constructs phoneme mappings from <c>Avatar.FaceVisemeMovement</c> indices, and wires face
        /// visibility callbacks to pause processing when not visible.
        /// </remarks>
        public bool TryInitialize(BasisPlayer BasisPlayer)
        {
            WasSuccessful = false;
            Avatar = BasisPlayer.BasisAvatar;
            Player = BasisPlayer;

            if (Avatar == null)
            {
                //   BasisDebug.Log("not setting up BasisVisemeDriver Avatar was null");
                return false;
            }
            if (Avatar.FaceVisemeMesh == null)
            {
                //   BasisDebug.Log("not setting up BasisVisemeDriver FaceVisemeMesh was null");
                return false;
            }
            if (Avatar.FaceVisemeMesh.sharedMesh.blendShapeCount == 0)
            {
                //  BasisDebug.Log("not setting up BasisVisemeDriver blendShapeCount was empty");
                return false;
            }

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
            if (Player != null && Player.FaceRenderer != null && HashInstanceID != Player.FaceRenderer.GetInstanceID())
            {
                // BasisDebug.Log("Wired up Renderer Check For Blinking");
                Player.FaceRenderer.Check += UpdateFaceVisibility;
                Player.FaceRenderer.DestroyCalled += TryShutdown;
            }
            //BasisDebug.Log($"uLipSync Initalized {Avatar.name}", BasisDebug.LogTag.Voice);
            uLipSync.Initalize();

            UpdateFaceVisibility(Player.FaceIsVisible);
            WasSuccessful = true;
            return true;
        }
        public void OnDestroy()
        {
            uLipSync.DisposeBuffers();
        }
        public void Simulate(float DeltaTime)
        {
            if (uLipSyncEnabledState == false)
            {
                return;
            }

            uLipSync.Simulate(DeltaTime);
        }
        public void Apply()
        {
            uLipSync.Apply();
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
        /// Current enabled state of uLipSync processing based on face visibility.
        /// </summary>
        public bool uLipSyncEnabledState = true;

        /// <summary>
        /// Callback that updates whether lip-sync is active based on face visibility.
        /// </summary>
        /// <param name="State">Whether the face is currently visible.</param>
        private void UpdateFaceVisibility(bool State)
        {
            uLipSyncEnabledState = State;
        }

        /// <summary>
        /// Unbinds face renderer callbacks if the same renderer instance is still present.
        /// </summary>
        public void OnDeInitalize()
        {
            if (Player != null)
            {
                if (Player.FaceRenderer != null && HashInstanceID == Player.FaceRenderer.GetInstanceID())
                {
                    Player.FaceRenderer.Check -= UpdateFaceVisibility;
                    Player.FaceRenderer.DestroyCalled -= TryShutdown;
                }
            }
        }

        /// <summary>
        /// Forwards raw audio samples to uLipSync when enabled and initialized.
        /// </summary>
        /// <param name="data">Interleaved PCM samples.</param>
        /// <param name="channels">Number of audio channels in <paramref name="data"/>.</param>
        /// <param name="Length">Number of samples provided.</param>
        /// <remarks>
        /// If the face is not visible or initialization failed, this method returns immediately.
        /// </remarks>
        public void ProcessAudioSamples(float[] data, int channels, int Length)
        {
            if (uLipSyncEnabledState == false)
            {
                return;
            }

            if (WasSuccessful == false)
            {
                return;
            }

            uLipSync.OnDataReceived(data, channels, Length);
        }

        /// <summary>
        /// External pause/resume hook for lip-sync playback.
        /// </summary>
        /// <param name="IsPaused"><c>true</c> to pause; <c>false</c> to resume.</param>
        /// <remarks>
        /// Reserved for future behavior changes (e.g., zeroing weights while paused).
        /// </remarks>
        public void OnPausedEvent(bool IsPaused)
        {
            if (IsPaused)
            {
                foreach (BasisPhonemeBlendShapeInfo blendshapeIndex in phonemeBlendShapeTable)
                {
                    Avatar.FaceVisemeMesh.SetBlendShapeWeight(blendshapeIndex.blendShape, 0);
                }
            }
        }
    }
}
