using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Steadies a tracked hand's pointer. The ray keeps its origin on the hand but its direction
    /// trails the controller with a speed-adaptive low-pass: small tremor is filtered heavily, a
    /// deliberate sweep releases the filter so the pointer keeps up. The filtered direction is
    /// what <see cref="ray"/> carries, so every physics, canvas and panel hit (and the press that
    /// follows) lands where the drawn line and reticle are, never on the raw controller aim.
    /// Filtering runs in player space, so locomotion, snap turns and seats move the pointer
    /// rigidly with the body instead of leaving it swinging behind.
    /// </summary>
    public partial class BasisPointRaycaster
    {
        private const float SmoothingMaxRestTime = 0.2f;
        private const float SmoothingSpeedEstimateTime = 0.04f;

        private bool _smoothingHasState;
        private int _smoothingFrame = -1;
        private Vector3 _smoothedLocalDirection;
        private Vector3 _lastTargetLocalDirection;
        private float _smoothedAngularSpeed;

        /// <summary>Controller aim before smoothing, world space.</summary>
        public Vector3 RawDirection { get; private set; } = Vector3.forward;

        /// <summary>True when the current <see cref="ray"/> direction came out of the smoothing filter.</summary>
        public bool IsSmoothing { get; private set; }

        /// <summary>
        /// The direction other pointing queries should use so they agree with the drawn pointer:
        /// the smoothed ray while it is being kept up to date, otherwise the raw controller aim.
        /// </summary>
        public Vector3 PointerDirection
        {
            get
            {
                if (IsSmoothing && _smoothingFrame >= Time.frameCount - 1)
                {
                    return ray.direction;
                }
                return BasisInput != null ? BasisInput.RaycastCoord.rotation * Vector3.forward : ray.direction;
            }
        }

        public static bool SmoothingEnabled => BasisSettingsDefaults.PointerSmoothing.RawValue;

        private bool CanSmooth()
        {
            return UseWorldPosition && BasisInput != null && SmoothingEnabled && !BasisPlayerInteract.IsDesktopCenterEye(BasisInput);
        }

        /// <summary>Drops the filter state so the next update starts exactly on the controller aim.</summary>
        public void ResetSmoothing()
        {
            _smoothingHasState = false;
            IsSmoothing = false;
        }

        private Vector3 SmoothDirection(Vector3 worldDirection)
        {
            RawDirection = worldDirection;
            if (!CanSmooth())
            {
                ResetSmoothing();
                return worldDirection;
            }

            Quaternion playerRotation = BasisLocalPlayer.localToWorldMatrix.rotation;
            Vector3 target = Quaternion.Inverse(playerRotation) * worldDirection;
            int frame = Time.frameCount;

            // A pointer that was hidden, blocked or skipped a frame restarts on the controller aim,
            // otherwise it would swoop in from wherever it was last seen.
            if (!_smoothingHasState || frame - _smoothingFrame > 1)
            {
                _smoothedLocalDirection = target;
                _lastTargetLocalDirection = target;
                _smoothedAngularSpeed = 0f;
                _smoothingHasState = true;
                _smoothingFrame = frame;
                IsSmoothing = true;
                return worldDirection;
            }

            // Several ray refreshes in one frame reuse the frame's result instead of filtering twice.
            if (frame == _smoothingFrame)
            {
                return playerRotation * _smoothedLocalDirection;
            }
            _smoothingFrame = frame;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 1e-5f)
            {
                return playerRotation * _smoothedLocalDirection;
            }

            float strength = Mathf.Clamp01(BasisSettingsDefaults.PointerSmoothingStrength.RawValue);
            float releaseSpeed = Mathf.Max(1f, BasisSettingsDefaults.PointerSmoothingReleaseSpeed.RawValue);
            float maxLag = Mathf.Max(0.1f, BasisSettingsDefaults.PointerSmoothingMaxLag.RawValue);

            // The speed estimate is itself filtered, so a single noisy sample cannot release the filter.
            float angularSpeed = Vector3.Angle(_lastTargetLocalDirection, target) / dt;
            _lastTargetLocalDirection = target;
            _smoothedAngularSpeed += (angularSpeed - _smoothedAngularSpeed) * (1f - Mathf.Exp(-dt / SmoothingSpeedEstimateTime));

            float hold = 1f - Mathf.Clamp01(_smoothedAngularSpeed / releaseSpeed);
            float timeConstant = SmoothingMaxRestTime * strength * hold * hold;
            float blend = timeConstant <= 1e-5f ? 1f : 1f - Mathf.Exp(-dt / timeConstant);

            Vector3 smoothed = Vector3.Slerp(_smoothedLocalDirection, target, blend);

            // Never trail the controller by more than the configured angle: past it the line stops
            // reading as attached to the hand.
            if (Vector3.Angle(smoothed, target) > maxLag)
            {
                smoothed = Vector3.RotateTowards(target, smoothed, maxLag * Mathf.Deg2Rad, 0f);
            }

            smoothed.Normalize();
            _smoothedLocalDirection = smoothed;
            IsSmoothing = true;
            return playerRotation * smoothed;
        }
    }
}
