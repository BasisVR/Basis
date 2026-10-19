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
        [NonSerialized] private Transform scaleTarget;
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
        public void ReInitialize(Animator animator, Transform avatarRoot = null)
        {
            // AvatarTransform is the scale/network root used by BasisAvatarFactory and the network
            // compressor. The Animator may sit below that root (for example under an import-scale
            // node), so measuring one transform and writing the other corrupts both local size and
            // remote scale replication. Prefer the explicit avatar root and retain the Animator-root
            // fallback for callers/tests that do not have a BasisAvatar wrapper.
            scaleTarget = avatarRoot != null ? avatarRoot : animator != null ? animator.transform : null;
            if (scaleTarget == null)
            {
                DuringCalibrationScale = Vector3.one;
            }
            else
            {
                DuringCalibrationScale = SanitizeCalibrationScale(scaleTarget.localScale);
            }

            ApplyScale = 1f;
            FinalScale = DuringCalibrationScale * ApplyScale;
        }
        public float NetworkScale(Transform animatorTransform)
        {
            float local = animatorTransform != null ? animatorTransform.localScale.y : 1f;
            float scale = scaleTarget != null && scaleTarget != animatorTransform ? local * ApplyScale : local;
            return IsFinite(scale) && scale > 0f ? scale : local;
        }
        public void SetAvatarheightOverride(float scale)
        {
            if (!IsFinite(scale) || scale <= 0f) scale = 1f;

            ApplyScale = scale;
            FinalScale = DuringCalibrationScale * ApplyScale;

            if (scaleTarget != null)
            {
                // Always write back to the exact transform sampled by ReInitialize.
                scaleTarget.localScale = FinalScale;
            }
        }
    }
}
