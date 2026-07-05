#if !BASIS_DISABLE_MICROPHONE
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
        public Color ShoutColorActive = new Color(1f, 0.5490196f, 0f, 1f);
        public Color ShoutColorInactive = new Color(0.6f, 0.3294118f, 0f, 1f);
        public Color PrivateColorActive = new Color(0.6078432f, 0.1882353f, 1f, 1f);
        public Color PrivateColorInactive = new Color(0.3647059f, 0.1129412f, 0.6f, 1f);
        public Color DirectColorActive = new Color(0.12156863f, 0.7490196f, 0.3529412f, 1f);
        public Color DirectColorInactive = new Color(0.07294118f, 0.44941176f, 0.21176471f, 1f);
        public Color ThisPersonColorActive = new Color(1f, 0.3098039f, 0.627451f, 1f);
        public Color ThisPersonColorInactive = new Color(0.6f, 0.1858823f, 0.3764706f, 1f);

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
                StartingScale = SpriteRendererIconTransform.localScale;
                largerScale = StartingScale * 1.2f;
            }

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
            if (SpriteRendererIcon != null)
            {
                SpriteRendererIcon.sprite = IsMuted ? SpriteMicrophoneOff : SpriteMicrophoneOn;
            }

            // request bounce + recompute intents (no renderer writes)
            bounceRequested = true;
            RecomputeVisibilityIntent();
            RecomputeColorIntent();

            if (PlaySound && CameraDriver != null)
            {
                AudioClip clip = IsMuted ? MuteSound : UnMuteSound;
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
            go.transform.position = position;
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

        public void OnShoutModeChanged()
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

            if (BasisAudioTransmission.IsInShoutMode)
            {
                return LocalIsTransmitting ? ShoutColorActive : ShoutColorInactive;
            }

            switch (BasisTalkModeManager.CurrentMode)
            {
                case BasisTalkMode.Private:
                    return LocalIsTransmitting ? PrivateColorActive : PrivateColorInactive;
                case BasisTalkMode.Direct:
                    return LocalIsTransmitting ? DirectColorActive : DirectColorInactive;
                case BasisTalkMode.ThisPerson:
                    return LocalIsTransmitting ? ThisPersonColorActive : ThisPersonColorInactive;
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
                SpriteRendererIconTransform.localScale = StartingScale;
                StopScaleBounce();
                return;
            }

            if (!isScaling)
            {
                // Ensure idle scale is consistent (especially after hide/show)
                SpriteRendererIconTransform.localScale = StartingScale;
                return;
            }

            scaleTime += DeltaTime;
            float t = (halfDuration <= 1e-6f) ? 1f : (scaleTime / halfDuration);

            if (scalingUp)
            {
                SpriteRendererIconTransform.localScale = Vector3.Lerp(StartingScale, largerScale, t);

                if (t >= 1f)
                {
                    SpriteRendererIconTransform.localScale = largerScale;
                    scalingUp = false;
                    scaleTime = 0f;
                }
            }
            else
            {
                SpriteRendererIconTransform.localScale = Vector3.Lerp(largerScale, StartingScale, t);

                if (t >= 1f)
                {
                    SpriteRendererIconTransform.localScale = StartingScale;
                    isScaling = false;
                }
            }
        }
    }
}

#endif
