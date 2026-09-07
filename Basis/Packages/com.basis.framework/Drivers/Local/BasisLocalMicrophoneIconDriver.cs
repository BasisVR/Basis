#if !BASIS_DISABLE_MICROPHONE
using Basis.BasisUI;
using Basis.Scripts.Audio;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisLocalMicrophoneIconDriver
    {
        public enum MicrophoneDisplayMode
        {
            Off,
            AlwaysVisible,
            ActivityDetection,
        }

        // --- Config / References ---
        public MicrophoneDisplayMode DisplayMode = MicrophoneDisplayMode.AlwaysVisible;
        public SpriteRenderer SpriteRendererIcon;
        public Transform SpriteRendererIconTransform;
        public Sprite SpriteMicrophoneOn;
        public Sprite SpriteMicrophoneOff;

        // --- Level ring (shader-drawn circle outline, swapped in for the sprite) ---
        /// <summary>Replaces the microphone glyph with an outline-only circle that grows with voice level.</summary>
        public bool UseLevelRing;

        /// <summary>Ring quad edge length, as a multiple of the microphone sprite's longest side.</summary>
        public float LevelRingSizeMultiplier = 0.875f;

        /// <summary>Radius at silence, in quad half-extents. Under the stroke, so it closes to a dot.</summary>
        [Range(0f, 1f)]
        public float LevelRingQuietRadius = 0.05f;

        /// <summary>Ring radius at 0 dBFS, in quad half-extents. Plus the stroke it must stay under 1.</summary>
        [Range(0f, 1f)]
        public float LevelRingLoudRadius = 0.88f;

        /// <summary>Radius held while muted, in quad half-extents. Mute is a state, not a level.</summary>
        [Range(0f, 1f)]
        public float LevelRingMutedRadius = 0.45f;

        /// <summary>Stroke half-width, in quad half-extents.</summary>
        [Range(0.002f, 0.3f)]
        public float LevelRingThickness = 0.06f;

        /// <summary>
        /// Multiplier on the shared voice envelope's attack and release rate. The ring is read at a
        /// glance rather than watched like a meter, so it wants to be quicker off the mark than the
        /// settings-panel meter that shares those constants.
        /// </summary>
        [Range(0.25f, 8f)]
        public float LevelRingResponse = 2f;

        private static readonly int LevelRingLevelId = Shader.PropertyToID("_Level");
        private static readonly int LevelRingQuietId = Shader.PropertyToID("_RadiusQuiet");
        private static readonly int LevelRingLoudId = Shader.PropertyToID("_RadiusLoud");
        private static readonly int LevelRingMutedRadiusId = Shader.PropertyToID("_RadiusMuted");
        private static readonly int LevelRingThicknessId = Shader.PropertyToID("_Thickness");
        private static readonly int LevelRingMutedId = Shader.PropertyToID("_Muted");

        private Material levelRingMaterial;
        private Sprite levelRingSprite;
        private Material spriteIconMaterial;
        private float levelRingRms;

        public Vector2 VRdesiredNormXY = new Vector2(-0.42f, -0.52f);

        [Range(0f, 0.2f)]
        public float VRextraViewportPad = 0.022f;

        /// <summary>User-configurable offset applied to VRdesiredNormXY. Range -1..1 for both axes.</summary>
        public Vector2 IconPositionOffset = Vector2.zero;

        private Vector2 iconHalfRU;
        private readonly Vector3[] corners = new Vector3[4];
        private Rect FrustumRequest = new Rect(0, 0, 1, 1);

        // --- State ---
        public bool LocalIsTransmitting;
        public bool IsCurrentlyMuted { get; private set; }

        // Colors
        public Color UnMutedMutedIconColorActive = Color.white;
        public Color UnMutedMutedIconColorInactive = Color.grey;
        public Color MutedColor = Color.grey;
        public Color AnnounceColorActive = new Color(1f, 0.5490196f, 0f, 1f);
        public Color AnnounceColorInactive = new Color(0.6f, 0.3294118f, 0f, 1f);
        public Color ShoutColorActive = new Color(1f, 0.8117647f, 0.1607843f, 1f);
        public Color ShoutColorInactive = new Color(0.6f, 0.4870588f, 0.0964706f, 1f);
        public Color PrivateColorActive = new Color(0.6078432f, 0.1882353f, 1f, 1f);
        public Color PrivateColorInactive = new Color(0.3647059f, 0.1129412f, 0.6f, 1f);
        public Color DirectColorActive = new Color(0.12156863f, 0.7490196f, 0.3529412f, 1f);
        public Color DirectColorInactive = new Color(0.07294118f, 0.44941176f, 0.21176471f, 1f);
        public Color ThisPersonColorActive = new Color(1f, 0.3098039f, 0.627451f, 1f);
        public Color ThisPersonColorInactive = new Color(0.6f, 0.1858823f, 0.3764706f, 1f);
        public Color NoOneColorActive = new Color(0.3921569f, 0.5882353f, 0.7843137f, 1f);
        public Color NoOneColorInactive = new Color(0.2352941f, 0.3529412f, 0.4705882f, 1f);

        // Scale / FX
        public Vector3 StartingScale = Vector3.zero;
        public Vector3 largerScale;

        // Audio
        public AudioClip MuteSound;
        public AudioClip UnMuteSound;

        // Timing
        public float duration = 0.35f;
        public float halfDuration;

        private const float ClickMinInterval = 0.1f;
        private float _lastClickPlayTime = float.NegativeInfinity;

        // Owner
        public BasisLocalCameraDriver CameraDriver;

        // --- Scale animation state (Update/LateUpdate driven) ---
        private float scaleTime = 0f;
        private bool scalingUp = true;
        private bool isScaling = false;

        // --- Render "intent" (ONLY applied in Simulate) ---
        private bool requestedVisible = true;
        private Color targetColor = Color.white;
        private bool bounceRequested = false;

        public Color CurrentColor => targetColor;
        public static Color LastColor { get; private set; } = Color.white;
        public static event System.Action<Color> OnColorChanged;

        // ---------------- Initialization ----------------
        public void Initialize(BasisLocalCameraDriver CameraDriver)
        {
            this.CameraDriver = CameraDriver;

            halfDuration = duration / 2f;
            iconHalfRU = GetIconHalfSizeRUInCameraSpace(CameraDriver.Camera, CameraDriver.ParentOfUI);

            if (SpriteRendererIcon != null)
            {
                SpriteRendererIconTransform = SpriteRendererIcon.transform;
                StartingScale = SpriteRendererIconTransform.GetLocalScale();
                largerScale = StartingScale * 1.2f;
                if (spriteIconMaterial == null)
                {
                    // Once only: a re-Initialize while the ring is up would otherwise capture the
                    // ring material as the sprite's, and the sprite style could never come back.
                    spriteIconMaterial = SpriteRendererIcon.sharedMaterial;
                }
            }

            // Settings are broadcast per scene load, which can land either side of the microphone
            // driver reporting itself ready, so read the binding here rather than wait for a change.
            UseLevelRing = BasisSettingsDefaults.MicrophoneIconLevelRing.RawValue;
            ApplyIconStyle();

            UpdateMicrophoneVisuals(BasisLocalMicrophoneDriver.isPaused, false);

            // Seed intents (no renderer writes here)
            RecomputeVisibilityIntent();
            RecomputeColorIntent();
        }

        // This is different from the user's setting of requestedVisual, which enables or disables the component.
        // This is used in the initialization stage to force hide the visual until it is ready to be shown.
        public void HardEnableVisuals(bool enabled)
        {
            if (SpriteRendererIcon != null)
            {
                SpriteRendererIcon.gameObject.SetActive(enabled);
            }
        }

        // ---------------- Level Ring ----------------
        public void OnLevelRingChanged(bool enabled)
        {
            if (UseLevelRing == enabled)
            {
                return;
            }

            UseLevelRing = enabled;
            ApplyIconStyle();
        }

        /// <summary>
        /// Points the icon renderer at either the microphone sprite or the ring quad. Both styles
        /// share the one renderer, so placement, visibility, colour and sorting stay where they are.
        /// </summary>
        private void ApplyIconStyle()
        {
            if (SpriteRendererIcon == null)
            {
                return;
            }

            if (UseLevelRing && !TryBuildLevelRing())
            {
                // Nothing to draw the ring with: keep the sprite rather than blank the icon out.
                UseLevelRing = false;
            }

            if (UseLevelRing)
            {
                SpriteRendererIcon.sharedMaterial = levelRingMaterial;
                SpriteRendererIcon.sprite = levelRingSprite;
                levelRingRms = 0f;
                // The bounce scales the quad, which would thicken the stroke with it; the ring
                // already animates on its own, so it stays at rest.
                StopScaleBounce();
            }
            else
            {
                if (spriteIconMaterial != null)
                {
                    SpriteRendererIcon.sharedMaterial = spriteIconMaterial;
                }
                SpriteRendererIcon.sprite = IsCurrentlyMuted ? SpriteMicrophoneOff : SpriteMicrophoneOn;
            }
        }

        private bool TryBuildLevelRing()
        {
            if (levelRingMaterial == null)
            {
                Material source = BasisDeviceManagement.Instance != null
                    ? BasisDeviceManagement.Instance.MicrophoneLevelRingMaterial
                    : null;
                if (source == null)
                {
                    BasisDebug.LogError("Microphone level ring material is unassigned on BasisDeviceManagement; keeping the microphone sprite.");
                    return false;
                }

                // Instantiated, because _Level is written every frame and the asset is shared.
                levelRingMaterial = new Material(source) { name = "MicrophoneLevelRing", hideFlags = HideFlags.HideAndDontSave };
            }

            if (levelRingSprite == null)
            {
                // The microphone sprite's mesh is tight-fitted to the glyph, so a circle drawn over
                // it would be cut to the microphone silhouette. Build a full-rect quad instead,
                // sized off that sprite so the ring lands where the icon already sat.
                Sprite source = SpriteMicrophoneOn != null ? SpriteMicrophoneOn : SpriteMicrophoneOff;
                float side = source != null
                    ? Mathf.Max(source.rect.width, source.rect.height) / Mathf.Max(1e-4f, source.pixelsPerUnit)
                    : 1f;
                side *= Mathf.Max(0.01f, LevelRingSizeMultiplier);

                Texture2D white = Texture2D.whiteTexture;
                levelRingSprite = Sprite.Create(white, new Rect(0f, 0f, white.width, white.height),
                    new Vector2(0.5f, 0.5f), white.width / side, 0, SpriteMeshType.FullRect);
                levelRingSprite.name = "MicrophoneLevelRing";
                levelRingSprite.hideFlags = HideFlags.HideAndDontSave;
            }

            levelRingMaterial.SetFloat(LevelRingQuietId, LevelRingQuietRadius);
            levelRingMaterial.SetFloat(LevelRingLoudId, LevelRingLoudRadius);
            levelRingMaterial.SetFloat(LevelRingMutedRadiusId, LevelRingMutedRadius);
            levelRingMaterial.SetFloat(LevelRingThicknessId, LevelRingThickness);
            return true;
        }

        /// <summary>Releases the runtime ring material and quad. Safe to call when neither was built.</summary>
        public void Dispose()
        {
            if (UseLevelRing)
            {
                // Hands the renderer back its sprite and material before the ring's are destroyed.
                UseLevelRing = false;
                ApplyIconStyle();
            }

            if (levelRingMaterial != null)
            {
                UnityEngine.Object.Destroy(levelRingMaterial);
                levelRingMaterial = null;
            }

            if (levelRingSprite != null)
            {
                UnityEngine.Object.Destroy(levelRingSprite);
                levelRingSprite = null;
            }
        }

        // ---------------- Layout Helpers ----------------
        public Vector3 CalculateClampedLocal(Camera cam)
        {
            float depth = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            cam.CalculateFrustumCorners(FrustumRequest, depth, Camera.MonoOrStereoscopicEye.Left, corners);

            Vector3 BL = corners[0];
            Vector3 TL = corners[1];
            Vector3 TR = corners[2];
            Vector3 BR = corners[3];

            float halfW = (TR - TL).magnitude * 0.5f;
            float halfH = (TL - BL).magnitude * 0.5f;

            float marginU = Mathf.Clamp01(iconHalfRU.x / Mathf.Max(halfW, 1e-4f)) + VRextraViewportPad;
            float marginV = Mathf.Clamp01(iconHalfRU.y / Mathf.Max(halfH, 1e-4f)) + VRextraViewportPad;

            float u = Mathf.Clamp(VRdesiredNormXY.x + IconPositionOffset.x, -1f + marginU, 1f - marginU);
            float v = Mathf.Clamp(VRdesiredNormXY.y + IconPositionOffset.y, -1f + marginV, 1f - marginV);

            float s = (u + 1f) * 0.5f;
            float t = (v + 1f) * 0.5f;

            Vector3 bottom = Vector3.LerpUnclamped(BL, BR, s);
            Vector3 top = Vector3.LerpUnclamped(TL, TR, s);
            return Vector3.LerpUnclamped(bottom, top, t);
        }

        public Vector2 GetIconHalfSizeRUInCameraSpace(Camera cam, Transform uiRoot)
        {
            Vector3 ext = SpriteRendererIcon.bounds.extents;
            Vector3 right = cam.transform.right;
            Vector3 up = cam.transform.up;

            Vector3 ex = uiRoot.TransformVector(new Vector3(ext.x * 2f, 0, 0)) * 0.5f;
            Vector3 ey = uiRoot.TransformVector(new Vector3(0, ext.y * 2f, 0)) * 0.5f;
            Vector3 ez = uiRoot.TransformVector(new Vector3(0, 0, ext.z * 2f)) * 0.5f;

            float halfRight = ProjectHalfOnAxis(right, ex, ey, ez);
            float halfUp = ProjectHalfOnAxis(up, ex, ey, ez);

            return new Vector2(Mathf.Abs(halfRight), Mathf.Abs(halfUp));
        }

        public static float ProjectHalfOnAxis(Vector3 axis, params Vector3[] halfAxes)
        {
            axis = axis.normalized;
            float sum = 0f;
            for (int i = 0; i < halfAxes.Length; i++)
            {
                sum += Mathf.Abs(Vector3.Dot(axis, halfAxes[i]));
            }
            return sum;
        }

        // ---------------- Activity Hooks ----------------
        public void MicrophoneTransmitting()
        {
            LocalIsTransmitting = true;
            RecomputeVisibilityIntent();
            RecomputeColorIntent();
            // no renderer writes
        }

        public void MicrophoneNotTransmitting()
        {
            LocalIsTransmitting = false;
            RecomputeVisibilityIntent();
            RecomputeColorIntent();
            // no renderer writes
        }

        public void OnPausedEvent(bool IsMuted)
        {
            UpdateMicrophoneVisuals(IsMuted, true);
        }

        // ---------------- Visuals & Display Mode ----------------
        public void UpdateMicrophoneVisuals(bool IsMuted, bool PlaySound)
        {
            IsCurrentlyMuted = IsMuted;

            // sprite change can stay here (you only asked to centralize color/scale/active)
            // In ring mode the shader carries the mute state instead, applied in Simulate.
            if (SpriteRendererIcon != null && !UseLevelRing)
            {
                SpriteRendererIcon.sprite = IsMuted ? SpriteMicrophoneOff : SpriteMicrophoneOn;
            }

            // request bounce + recompute intents (no renderer writes)
            bounceRequested = !UseLevelRing;
            RecomputeVisibilityIntent();
            RecomputeColorIntent();

            if (PlaySound && CameraDriver != null)
            {
                BasisUISoundEvent micEvent = IsMuted ? BasisUISoundEvent.MicMute : BasisUISoundEvent.MicUnmute;
                AudioClip clip = BasisUISounds.IsEnabled(micEvent)
                    ? BasisUISounds.Resolve(micEvent, IsMuted ? MuteSound : UnMuteSound)
                    : null;
                float now = Time.realtimeSinceStartup;
                if (clip != null && now - _lastClickPlayTime >= ClickMinInterval)
                {
                    _lastClickPlayTime = now;
                    PlayMicClickOneShot(clip, BasisLocalCameraDriver.Position, SMModuleAudio.ActiveMenusVolume);
                }
            }
        }

        private static void PlayMicClickOneShot(AudioClip clip, Vector3 position, float volume)
        {
            GameObject go = new GameObject("MicClickOneShot");
            go.transform.SetPosition(position);
            AudioSource src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0f;
            src.volume = volume;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        public void OnDisplayModeChanged(MicrophoneDisplayMode newMode)
        {
            DisplayMode = newMode;

            // If we're going to hide the icon, kill bounce cleanly (no scale write)
            if (DisplayMode == MicrophoneDisplayMode.Off)
            {
                StopScaleBounce();
            }

            RecomputeVisibilityIntent();
        }

        private void RecomputeVisibilityIntent()
        {
            switch (DisplayMode)
            {
                case MicrophoneDisplayMode.Off:
                    requestedVisible = false;
                    break;

                case MicrophoneDisplayMode.AlwaysVisible:
                    requestedVisible = true;
                    break;

                case MicrophoneDisplayMode.ActivityDetection:
                    // Show when muted OR transmitting.
                    requestedVisible = IsCurrentlyMuted || LocalIsTransmitting;
                    break;

                default:
                    requestedVisible = true;
                    break;
            }
        }

        public void OnAnnounceModeChanged()
        {
            RecomputeColorIntent();
        }

        public void OnTalkModeChanged()
        {
            RecomputeColorIntent();
        }

        private void RecomputeColorIntent()
        {
            Color color = ComputeColorIntent();
            if (color == targetColor)
            {
                return;
            }
            targetColor = color;
            LastColor = color;
            OnColorChanged?.Invoke(color);
        }

        private Color ComputeColorIntent()
        {
            if (IsCurrentlyMuted)
            {
                return MutedColor;
            }

            // A local-only hold (the microphone test) blocks the send exactly like NoOne does,
            // so it has to read the same on the HUD, the menu mute button and the range circle.
            if (BasisTalkModeManager.LocalOnlyHeld)
            {
                return LocalIsTransmitting ? NoOneColorActive : NoOneColorInactive;
            }

            if (BasisAudioTransmission.IsInAnnounceMode)
            {
                return LocalIsTransmitting ? AnnounceColorActive : AnnounceColorInactive;
            }

            switch (BasisTalkModeManager.CurrentMode)
            {
                case BasisTalkMode.Private:
                    return LocalIsTransmitting ? PrivateColorActive : PrivateColorInactive;
                case BasisTalkMode.Direct:
                    return LocalIsTransmitting ? DirectColorActive : DirectColorInactive;
                case BasisTalkMode.ThisPerson:
                    return LocalIsTransmitting ? ThisPersonColorActive : ThisPersonColorInactive;
                case BasisTalkMode.Shout:
                    return LocalIsTransmitting ? ShoutColorActive : ShoutColorInactive;
                case BasisTalkMode.NoOne:
                    return LocalIsTransmitting ? NoOneColorActive : NoOneColorInactive;
                default:
                    return LocalIsTransmitting ? UnMutedMutedIconColorActive : UnMutedMutedIconColorInactive;
            }
        }

        // ---------------- Bounce (LateUpdate-style) ----------------
        private void StartScaleBounce()
        {
            if (SpriteRendererIconTransform == null)
                return;

            scaleTime = 0f;
            scalingUp = true;
            isScaling = true;
        }

        private void StopScaleBounce()
        {
            isScaling = false;
            scalingUp = true;
            scaleTime = 0f;
        }

        /// <summary>
        /// Call this once per LateUpdate from your driver, passing Time.deltaTime.
        /// This is the ONLY place that sets enabled/color/scale.
        /// </summary>
        public void Simulate(float DeltaTime)
        {
            // --- Apply active state ---
            SpriteRendererIcon.enabled = requestedVisible;

            // --- Apply color ---
            SpriteRendererIcon.color = targetColor;

            // --- Drive the ring radius from the outgoing voice level ---
            if (UseLevelRing && levelRingMaterial != null)
            {
                // Followed in RMS space with the shared attack/release, then through the shared dBFS
                // window, so the ring rides the same 0..1 loudness the volume meter and any avatar
                // parameter already read -- linear amplitude would leave speech in the bottom fifth.
                // Follow only ever uses deltaSeconds/tau, so scaling the delta divides both time
                // constants by the same factor: the ring speeds up without moving the shared ones.
                float target = IsCurrentlyMuted ? 0f : BasisVoiceLevel.LocalVoiceRms;
                levelRingRms = BasisVoiceLevel.Follow(levelRingRms, target, DeltaTime * LevelRingResponse);
                levelRingMaterial.SetFloat(LevelRingLevelId, BasisVoiceLevel.RmsToUnit(levelRingRms));
                levelRingMaterial.SetFloat(LevelRingMutedId, IsCurrentlyMuted ? 1f : 0f);
            }

            // --- Start bounce if requested ---
            if (bounceRequested)
            {
                bounceRequested = false;
                StartScaleBounce();
            }

            // --- Apply scale (bounce or settle) ---
            if (!requestedVisible)
            {
                // If hidden, you can choose what scale to keep.
                // Usually safest to reset to starting so next show is clean.
                SpriteRendererIconTransform.SetLocalScale(StartingScale);
                StopScaleBounce();
                return;
            }

            if (!isScaling)
            {
                // Ensure idle scale is consistent (especially after hide/show)
                SpriteRendererIconTransform.SetLocalScale(StartingScale);
                return;
            }

            scaleTime += DeltaTime;
            float t = (halfDuration <= 1e-6f) ? 1f : (scaleTime / halfDuration);

            if (scalingUp)
            {
                SpriteRendererIconTransform.SetLocalScale(Vector3.Lerp(StartingScale, largerScale, t));

                if (t >= 1f)
                {
                    SpriteRendererIconTransform.SetLocalScale(largerScale);
                    scalingUp = false;
                    scaleTime = 0f;
                }
            }
            else
            {
                SpriteRendererIconTransform.SetLocalScale(Vector3.Lerp(largerScale, StartingScale, t));

                if (t >= 1f)
                {
                    SpriteRendererIconTransform.SetLocalScale(StartingScale);
                    isScaling = false;
                }
            }
        }
    }
}

#endif
