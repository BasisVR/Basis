// BasisAvatarScaleModifier.cs (fixed parts)

using Basis.Scripts.BasisSdk.Players;
using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class BasisAvatarScaleModifier
    {
        /// <summary>Captured during calibration (avatar root localScale at that moment)</summary>
        public Vector3 DuringCalibrationScale = Vector3.one;

        /// <summary>User/runtime override (1 = no override)</summary>
        public float ApplyScale = 1f;

        /// <summary>Final applied root scale = DuringCalibrationScale * ApplyScale</summary>
        public Vector3 FinalScale = Vector3.one;

        private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));

        private static Vector3 SanitizeCalibrationScale(Vector3 v)
        {
            // Prevent zeros/NaNs/Infs from poisoning the whole pipeline.
            if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z)) return Vector3.one;

            // If any axis is 0, treat it as 1 (uniform rigs will thank you).
            if (v.x == 0f) v.x = 1f;
            if (v.y == 0f) v.y = 1f;
            if (v.z == 0f) v.z = 1f;

            // Optional: avoid negative scale weirdness
            if (v.x < 0f) v.x = Mathf.Abs(v.x);
            if (v.y < 0f) v.y = Mathf.Abs(v.y);
            if (v.z < 0f) v.z = Mathf.Abs(v.z);

            return v;
        }

        /// <summary>
        /// Call during calibration: captures baseline avatar root scale and resets override.
        /// </summary>
        public void ReInitalize(Animator animator)
        {
            if (animator == null)
            {
                DuringCalibrationScale = Vector3.one;
            }
            else
            {
                DuringCalibrationScale = SanitizeCalibrationScale(animator.transform.localScale);
            }

            ApplyScale = 1f;
            FinalScale = DuringCalibrationScale * ApplyScale;
        }

        /// <summary>
        /// Sets the override factor, computes FinalScale, and applies it to the local avatar root.
        /// </summary>
        public void SetAvatarheightOverride(float scale)
        {
            // Sanitize override
            if (!IsFinite(scale) || scale <= 0f) scale = 1f;

            ApplyScale = scale;
            FinalScale = DuringCalibrationScale * ApplyScale;

            var lp = BasisLocalPlayer.Instance;
            if (lp != null && lp.BasisAvatar != null)
            {
                lp.BasisAvatar.transform.localScale = FinalScale;
            }
        }

        /// <summary>
        /// Returns the uniform "effective scale factor" that matches what the avatar root is doing.
        /// Uses Y as the canonical axis (consistent with your height code).
        /// </summary>
        public float GetEffectiveUniformScaleY()
        {
            // FinalScale is authoritative; use its Y for uniform scaling math.
            float y = FinalScale.y;
            if (!IsFinite(y) || y <= 0f) y = 1f;
            return y;
        }
    }
}
