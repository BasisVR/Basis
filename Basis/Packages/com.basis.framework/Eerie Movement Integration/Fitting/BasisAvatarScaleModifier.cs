using Basis.Scripts.BasisSdk.Players;
using System;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class BasisAvatarScaleModifier
    {
        public Vector3 DuringCalibrationScale = Vector3.one;
        public float ApplyScale = 1f;
        public Vector3 FinalScale = Vector3.one;
        private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));
        private static Vector3 SanitizeCalibrationScale(Vector3 v)
        {
            if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z)) return Vector3.one;

            if (v.x == 0f) v.x = 1f;
            if (v.y == 0f) v.y = 1f;
            if (v.z == 0f) v.z = 1f;

            if (v.x < 0f) v.x = Mathf.Abs(v.x);
            if (v.y < 0f) v.y = Mathf.Abs(v.y);
            if (v.z < 0f) v.z = Mathf.Abs(v.z);

            return v;
        }
        public void ReInitialize(Animator animator)
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
        public void SetAvatarheightOverride(float scale)
        {
            if (!IsFinite(scale) || scale <= 0f) scale = 1f;

            ApplyScale = scale;
            FinalScale = DuringCalibrationScale * ApplyScale;

            var lp = BasisLocalPlayer.Instance;
            if (lp != null && lp.BasisAvatar != null)
            {
                lp.BasisAvatar.transform.localScale = FinalScale;
                // Scaling the avatar root moves every bone under it, so the whole pose cache is
                // stale -- not just this transform. NotifyWrite matches one Transform by reference
                // and would leave the Hips and Head slots holding pre-scale world positions.
                BasisLocalPose.InvalidateAll();
            }
        }
    }
}
