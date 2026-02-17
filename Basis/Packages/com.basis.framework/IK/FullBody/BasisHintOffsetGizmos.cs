using Basis.Scripts.Drivers; // BasisLocalBoneDriver
using Basis.Scripts.TransformBinders.BoneControl; // BasisLocalBoneControl
using UnityEngine;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;

namespace Basis.Scripts.Debugging
{
    /// <summary>
    /// Static gizmo drawer for visualizing hint trackers and their calibrated "push up/out" offsets.
    /// Draws:
    /// - A wire sphere at the raw hint tracker position
    /// - A line showing the offset vector (rawPos -> biasedPos)
    /// - A wire sphere at the biased hint position
    /// </summary>
    public static class BasisHintOffsetGizmos
    {
        // Global toggles
        public static bool Enabled = true;

        // Visual tuning
        public static float RawSphereRadius = 0.02f;
        public static float BiasedSphereRadius = 0.02f;
        public static float AxisLength = 0.06f;
        public static float LineThickness = 0f; // Gizmos has no thickness; kept for future Handles.

        // Colors
        public static Color RawColor = new Color(1f, 1f, 0f, 0.9f);      // yellow
        public static Color BiasedColor = new Color(0f, 1f, 1f, 0.9f);   // cyan
        public static Color OffsetLineColor = new Color(1f, 0.25f, 0.25f, 0.95f); // red-ish

        public static Color XAxisColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        public static Color YAxisColor = new Color(0.2f, 1f, 0.2f, 0.9f);
        public static Color ZAxisColor = new Color(0.2f, 0.4f, 1f, 0.9f);

        /// <summary>
        /// Draw all hint offsets using the current BasisLocalBoneDriver control world poses.
        /// Call from some MonoBehaviour's OnDrawGizmos() / OnDrawGizmosSelected().
        /// </summary>
        public static void DrawAll()
        {
            if (!Enabled) return;

            // Chest is used as head hint driver in your pipeline
            DrawForRole(BasisBoneTrackedRole.Chest, BasisLocalBoneDriver.ChestControl);

            // Arm hints
            DrawForRole(BasisBoneTrackedRole.LeftLowerArm, BasisLocalBoneDriver.LeftLowerArmControl);
            DrawForRole(BasisBoneTrackedRole.RightLowerArm, BasisLocalBoneDriver.RightLowerArmControl);

            // Leg hints
            DrawForRole(BasisBoneTrackedRole.LeftLowerLeg, BasisLocalBoneDriver.LeftLowerLegControl);
            DrawForRole(BasisBoneTrackedRole.RightLowerLeg, BasisLocalBoneDriver.RightLowerLegControl);
        }

        /// <summary>
        /// Draw a single hint role/control pair.
        /// </summary>
        public static void DrawForRole(BasisBoneTrackedRole role, BasisLocalBoneControl control)
        {
            if (!Enabled) return;
            if (control == null) return;

            // We only draw if there is a stored bias for this role.
            if (!BasisHintBiasStore.TryGet(role, out var localOffset))
                return;

            Vector3 rawPos = control.OutgoingWorldData.position;
            Quaternion rawRot = control.OutgoingWorldData.rotation;

            Vector3 biasedPos = rawPos + rawRot * localOffset;

            // Raw marker
            Gizmos.color = RawColor;
            Gizmos.DrawWireSphere(rawPos, RawSphereRadius);

            // Offset vector
            Gizmos.color = OffsetLineColor;
            Gizmos.DrawLine(rawPos, biasedPos);

            // Biased marker
            Gizmos.color = BiasedColor;
            Gizmos.DrawWireSphere(biasedPos, BiasedSphereRadius);

            // Local axes at raw pose (helps see how localOffset rotates)
            DrawAxes(rawPos, rawRot, AxisLength);

            // Label (Editor-only; safe no-op in player if UNITY_EDITOR not defined)
#if UNITY_EDITOR
            UnityEditor.Handles.color = Color.white;
            UnityEditor.Handles.Label(rawPos + Vector3.up * (RawSphereRadius * 2.5f), $"{role}\n|offset|={localOffset.magnitude:F3}m");
#endif
        }

        /// <summary>
        /// Draws an orientation triad at a pose (x=red,y=green,z=blue).
        /// </summary>
        public static void DrawAxes(Vector3 origin, Quaternion rot, float len)
        {
            Vector3 x = rot * Vector3.right;
            Vector3 y = rot * Vector3.up;
            Vector3 z = rot * Vector3.forward;

            Gizmos.color = XAxisColor;
            Gizmos.DrawLine(origin, origin + x * len);

            Gizmos.color = YAxisColor;
            Gizmos.DrawLine(origin, origin + y * len);

            Gizmos.color = ZAxisColor;
            Gizmos.DrawLine(origin, origin + z * len);
        }

        /// <summary>
        /// If you want to draw from the already-built IK submission data instead of controls,
        /// this helper draws raw->biased using the data pose + stored local offset.
        /// </summary>
        public static void DrawFromPose(BasisBoneTrackedRole role, Vector3 rawPos, Quaternion rawRot)
        {
            if (!Enabled) return;
            if (!BasisHintBiasStore.TryGet(role, out var localOffset))
                return;

            Vector3 biasedPos = rawPos + rawRot * localOffset;

            Gizmos.color = RawColor;
            Gizmos.DrawWireSphere(rawPos, RawSphereRadius);

            Gizmos.color = OffsetLineColor;
            Gizmos.DrawLine(rawPos, biasedPos);

            Gizmos.color = BiasedColor;
            Gizmos.DrawWireSphere(biasedPos, BiasedSphereRadius);

            DrawAxes(rawPos, rawRot, AxisLength);
        }
    }
}
