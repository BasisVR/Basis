using Basis.Scripts.BasisSdk.Players;
using System.Collections;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;
using System;

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

        // Normalized [-1..1] desired position within the visible frustum where (0,0) is center.
        // (-1,-1)=bottom-left edge, (1,1)=top-right edge.
        // Think of it as "anchoring" inside the HMD view, independent of resolution.
        public Vector2 VRdesiredNormXY = new Vector2(-0.42f, -0.52f);

        // Optional additional normalized padding (percentage of frustum half-width/half-height)
        [Range(0f, 0.2f)]
        public float VRextraViewportPad = 0.022f;

        public Vector2 iconHalfRU;
        Vector3[] corners = new Vector3[4];
        Rect FrustumRequest = new Rect(0, 0, 1, 1);

        // --- State ---
        public bool LocalIsTransmitting;
        public bool IsCurrentlyMuted { get; private set; }

        // Colors
        public Color UnMutedMutedIconColorActive = Color.white;
        public Color UnMutedMutedIconColorInactive = Color.grey;
        public Color MutedColor = Color.grey;

        // Scale / FX
        public Vector3 StartingScale = Vector3.zero;
        public Vector3 largerScale;
        private Coroutine scaleCoroutine;

        // Audio
        public AudioClip MuteSound;
        public AudioClip UnMuteSound;
        public AudioSource AudioSource;

        // Timing
        public float duration = 0.35f;
        public float halfDuration;

        // Owner
        public BasisLocalCameraDriver CameraDriver;

        // ---------------- Initialization ----------------
        public void Initalize(BasisLocalCameraDriver CameraDriver)
        {
            this.CameraDriver = CameraDriver;

            halfDuration = duration / 2f; // Time to scale up and down

            SpriteRendererIconTransform = SpriteRendererIcon.transform;

            StartingScale = SpriteRendererIconTransform.localScale;
            largerScale = StartingScale * 1.2f;
            // Ensure initial visibility matches current mode/state
            ApplyDisplayModeVisibility();
        }

        // ---------------- Layout Helpers ----------------
        /// <summary>
        /// Places ParentOfUI in front of the camera at depth, using desired normalized coords in [-1..1],
        /// and clamps so the entire object (bounds) stays visible. Returns camera-local position.
        /// </summary>
        public Vector3 CalculateClampedLocal(Camera cam, Vector3 Position)
        {
            // 1) Frustum size at 'depth'
            // Use frustum corners to be robust to per-eye projections/FOVs.
            cam.CalculateFrustumCorners(FrustumRequest, 1, Camera.MonoOrStereoscopicEye.Left, corners);
            // corners: BL, TL, TR, BR in camera-local space
            // We want width/height at 'depth'
            Vector3 BL = corners[0];
            Vector3 TL = corners[1];
            Vector3 TR = corners[2];
            // In camera-local, right vector is along TR - TL, up is TL - BL; center is (BL+TR)/2
            float frustumWidth = (TR - TL).magnitude;
            float frustumHeight = (TL - BL).magnitude;
            float halfW = frustumWidth * 0.5f;
            float halfH = frustumHeight * 0.5f;

            // 3) Convert desired normXY [-1..1] to clamped norm so icon stays fully inside
            // Compute normalized “margins” required by the icon extents
            float marginU = Mathf.Clamp01(iconHalfRU.x / Mathf.Max(halfW, 1e-4f)) + VRextraViewportPad;
            float marginV = Mathf.Clamp01(iconHalfRU.y / Mathf.Max(halfH, 1e-4f)) + VRextraViewportPad;

            float u = Mathf.Clamp(VRdesiredNormXY.x, -1f + marginU, 1f - marginU);
            float v = Mathf.Clamp(VRdesiredNormXY.y, -1f + marginV, 1f - marginV);

            // 4) Build the camera-local position at depth using clamped normalized coords
            // Center point at depth on camera forward
            Vector3 centerAtDepth = cam.transform.InverseTransformPoint(Position + cam.transform.forward * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale);

            // Get camera-local right/up from corner vectors
            Vector3 rightLocal = (TR - TL).normalized;
            Vector3 upLocal = (TL - BL).normalized;

            Vector3 localPos = centerAtDepth + rightLocal * (u * halfW) + upLocal * (v * halfH);

            return localPos;
        }

        /// <summary>
        /// Computes the icon's half-size in meters projected onto the camera's right/up axes.
        /// Works for both 3D Renderers and world-space UI (RectTransform).
        /// </summary>
        public Vector2 GetIconHalfSizeRUInCameraSpace(Camera cam, Transform uiRoot)
        {
            // Bounds are in world space; project extents to camera right/up
            Vector3 ext = SpriteRendererIcon.bounds.extents;
            // We approximate by projecting the oriented extents to RU; this is conservative.
            Vector3 right = cam.transform.right;
            Vector3 up = cam.transform.up;

            // Build an oriented bounding "radius" along RU by sampling the 3 axes of the object
            // (handles rotated meshes). extents in local axes:
            Vector3 ex = uiRoot.TransformVector(new Vector3(ext.x * 2f, 0, 0)) * 0.5f;
            Vector3 ey = uiRoot.TransformVector(new Vector3(0, ext.y * 2f, 0)) * 0.5f;
            Vector3 ez = uiRoot.TransformVector(new Vector3(0, 0, ext.z * 2f)) * 0.5f;

            float halfRight = ProjectHalfOnAxis(right, ex, ey, ez);
            float halfUp = ProjectHalfOnAxis(up, ex, ey, ez);

            return new Vector2(Mathf.Abs(halfRight), Mathf.Abs(halfUp));
        }

        /// <summary>
        /// Projects the sum of half-axes onto a given axis; conservative half-size projection.
        /// </summary>
        public static float ProjectHalfOnAxis(Vector3 axis, params Vector3[] halfAxes)
        {
            axis = axis.normalized;
            float sum = 0f;
            for (int Index = 0; Index < halfAxes.Length; Index++)
                sum += Mathf.Abs(Vector3.Dot(axis, halfAxes[Index]));
            return sum;
        }

        // ---------------- Activity Hooks ----------------
        public void MicrophoneTransmitting()
        {
            LocalIsTransmitting = true;

            // Update visibility for ActivityDetection mode
            ApplyDisplayModeVisibility();

            SpriteRendererIcon.color = UnMutedMutedIconColorActive;
            SpriteRendererIconTransform.localScale = largerScale;
        }

        public void MicrophoneNotTransmitting()
        {
            LocalIsTransmitting = false;

            // Update visibility for ActivityDetection mode
            ApplyDisplayModeVisibility();

            SpriteRendererIcon.color = UnMutedMutedIconColorInactive;
            SpriteRendererIconTransform.localScale = StartingScale;
        }

        public void OnPausedEvent(bool IsMuted)
        {
            UpdateMicrophoneVisuals(IsMuted, true);
        }

        // ---------------- Visuals & Display Mode ----------------
        /// <summary>
        /// Update icon sprite/color/scale and apply display mode logic.
        /// </summary>
        public void UpdateMicrophoneVisuals(bool IsMuted, bool PlaySound)
        {
            IsCurrentlyMuted = IsMuted;

            // Cancel any running animation
            if (scaleCoroutine != null)
            {
                CameraDriver.StopCoroutine(scaleCoroutine);
                scaleCoroutine = null;
            }

            // Visibility per display mode
            ApplyDisplayModeVisibility();

            if (IsMuted)
            {
                SpriteRendererIcon.sprite = SpriteMicrophoneOff;
                AudioSource.PlayOneShot(MuteSound);

                SpriteRendererIcon.color = MutedColor;

                // Animate scale "bounce"
                scaleCoroutine = CameraDriver.StartCoroutine(ScaleIcons(SpriteRendererIcon.gameObject));
            }
            else
            {
                SpriteRendererIcon.sprite = SpriteMicrophoneOn;
                AudioSource.PlayOneShot(UnMuteSound);

                SpriteRendererIcon.color = LocalIsTransmitting ? UnMutedMutedIconColorActive : UnMutedMutedIconColorInactive;

                // Animate scale "bounce"
                scaleCoroutine = CameraDriver.StartCoroutine(ScaleIcons(SpriteRendererIconTransform.gameObject));
            }
        }

        /// <summary>
        /// Change the display mode at runtime and immediately apply its visibility rules.
        /// </summary>
        public void OnDisplayModeChanged(MicrophoneDisplayMode newMode)
        {
            if (DisplayMode == newMode) return;
            DisplayMode = newMode;

            // Stop any running animation if we might be hiding the icon
            if (scaleCoroutine != null)
            {
                CameraDriver.StopCoroutine(scaleCoroutine);
                scaleCoroutine = null;
            }

            ApplyDisplayModeVisibility();
        }

        /// <summary>
        /// Centralized visibility logic for all enum modes.
        /// - Off:            icon hidden always.
        /// - AlwaysVisible:  icon shown always.
        /// - ActivityDetection: icon shown when muted OR transmitting; hidden otherwise.
        /// </summary>
        private void ApplyDisplayModeVisibility()
        {
            bool shouldShow;
            switch (DisplayMode)
            {
                case MicrophoneDisplayMode.Off:
                    shouldShow = false;
                    break;

                case MicrophoneDisplayMode.AlwaysVisible:
                    shouldShow = true;
                    break;

                case MicrophoneDisplayMode.ActivityDetection:
                    // Show when the user is muted (state is important feedback) OR actively transmitting.
                    shouldShow = LocalIsTransmitting;
                    break;

                default:
                    shouldShow = true;
                    break;
            }

            SetIconVisible(shouldShow);
        }

        /// <summary>
        /// Enables/disables the icon renderer safely. Keeps the GameObject active
        /// so transforms/animations remain valid, but avoids rendering cost.
        /// </summary>
        private void SetIconVisible(bool visible)
        {
            if (SpriteRendererIcon != null)
            {
                SpriteRendererIcon.enabled = visible;
            }
        }

        // ---------------- Animation ----------------
        private IEnumerator ScaleIcons(GameObject iconToScale)
        {
            float time = 0f;

            // Phase 1: Scale up
            while (time < halfDuration)
            {
                time += Time.deltaTime;
                float t = time / halfDuration;

                // Scale the icon up
                iconToScale.transform.localScale = Vector3.Lerp(StartingScale, largerScale, t);
                yield return null; // Wait for the next frame
            }

            // Ensure the final scale at the end of phase 1 is set to largerScale
            iconToScale.transform.localScale = largerScale;

            // Reset time for the second phase
            time = 0f;

            // Phase 2: Scale down
            while (time < halfDuration)
            {
                time += Time.deltaTime;
                float t = time / halfDuration;

                // Scale the icon down back to the original scale
                iconToScale.transform.localScale = Vector3.Lerp(largerScale, StartingScale, t);
                yield return null; // Wait for the next frame
            }

            // Ensure the final scale at the end of phase 2 is set to originalScale
            iconToScale.transform.localScale = StartingScale;
        }
    }
}
