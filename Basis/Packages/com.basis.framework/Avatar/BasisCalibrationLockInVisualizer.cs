using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
namespace Basis.Scripts.Avatar
{
    [AutoStaticsCleanup]
    public static partial class BasisCalibrationLockInVisualizer
    {
        public static bool Enabled = true;
        // One slot after the bone-driver gizmos (250) so the bone transforms have settled.
        private const int RenderPriority = 251;
        // Tuning as fractions of world eye height so the aid scales with avatar size.
        private const float CaptureFrac = 0.06f;      // tracker within this of the bone -> locked (min size, green)
        private const float FalloffFrac = 0.30f;      // beyond this the ball/line hide (no tracker near the bone)
        private const float MinDiameterFrac = 0.04f, MaxDiameterFrac = 0.08f, LineWidthFrac = 0.006f;
        private static readonly Color FarColor = new Color(1f, 0.45f, 0.12f, 1f);
        private static readonly Color LockedColor = new Color(0.15f, 1f, 0.35f, 1f);
        private static bool active, registered;
        private static readonly Dictionary<BasisBoneTrackedRole, int> balls = new Dictionary<BasisBoneTrackedRole, int>();
        private static readonly Dictionary<BasisBoneTrackedRole, int> lines = new Dictionary<BasisBoneTrackedRole, int>();
        private static readonly List<BasisInput> candidates = new List<BasisInput>(16);
        public static void Begin()
        {
            active = true;
            if (!registered)
            {
                BasisLocalPlayer.AfterSimulateOnRender.AddAction(RenderPriority, OnRender);
                registered = true;
            }
        }
        public static void End()
        {
            active = false;
            if (registered)
            {
                BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(RenderPriority, OnRender);
                registered = false;
            }
            DestroyAll();
        }
        private static void OnRender()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (!active || !Enabled || player == null || player.LocalBoneDriver == null || !player.LocalBoneDriver.HasControls)
            {
                DestroyAll();
                return;
            }

            var boneDriver = player.LocalBoneDriver;

            float playerEye = Mathf.Max(BasisHeightDriver.PlayerEyeHeight, 1.0f);
            float deviceScale = BasisHeightDriver.DeviceScale;
            if (deviceScale <= 0f) deviceScale = 1f;
            float eyeHeight = playerEye * deviceScale, capture = CaptureFrac * eyeHeight;
            float falloff = FalloffFrac * eyeHeight, minDiam = MinDiameterFrac * eyeHeight;
            float maxDiam = MaxDiameterFrac * eyeHeight, lineWidth = LineWidthFrac * eyeHeight;

            // Body-forward reference for the foot aim color: the hips bone's facing, flattened. Falls
            // back to world forward when there's no hips bone (e.g. trackerless setups).
            Vector3 bodyForward = Vector3.forward;
            if (boneDriver.FindBone(out var hipsControl, BasisBoneTrackedRole.Hips) && hipsControl != null)
            {
                Vector3 fwd = hipsControl.OutgoingWorldData.rotation * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-4f) bodyForward = fwd.normalized;
            }

            CollectCandidates();

            int count = boneDriver.ControlsLength;
            for (int i = 0; i < count; i++)
            {
                BasisBoneTrackedRole role = boneDriver.trackedRoles[i];

                // Only the bones the user can drive with a full-body tracker (skips hands / head /
                // upper limbs, which keep their roles and are never lock-in targets).
                if (!BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                {
                    HideRole(role);
                    continue;
                }

                var control = boneDriver.Controls[i];
                if (control == null)
                {
                    HideRole(role);
                    continue;
                }

                Vector3 bonePos = control.OutgoingWorldData.position;

                // A guide only appears once SOME candidate tracker is near this bone, so bones the
                // player has no tracker for stay out of the view instead of hanging far/red.
                if (!TryNearestCandidate(bonePos, out Vector3 trackerPos, out Quaternion trackerRot, out float dist) || dist > falloff)
                {
                    HideRole(role);
                    continue;
                }

                float weight = BasisCalibrationLockInCore.ProximityWeight(dist, capture, falloff);

                // Feet fold rotation into the color: positionally close but mis-aimed should not green.
                float colorWeight = weight;
                if (role == BasisBoneTrackedRole.LeftFoot || role == BasisBoneTrackedRole.RightFoot)
                {
                    Vector3 footForward = trackerRot * Vector3.forward;
                    float yaw = BasisCalibrationLockInCore.FootYawDegrees(bodyForward, footForward, Vector3.up);
                    float tilt = BasisCalibrationLockInCore.FootTiltDegrees(trackerRot * Vector3.up, Vector3.up);
                    float align = BasisCalibrationLockInCore.FootAlignmentScore(yaw, tilt, BasisCalibrationLockInCore.DefaultMaxYawDeg, BasisCalibrationLockInCore.DefaultMaxTiltDeg);
                    colorWeight = Mathf.Min(weight, align);
                }

                float diameter = BasisCalibrationLockInCore.ProximityRadius(weight, minDiam, maxDiam);
                Color color = Color.Lerp(FarColor, LockedColor, colorWeight);

                EnsureBall(role, bonePos, diameter, color);
                EnsureLine(role, trackerPos, bonePos, lineWidth, color);
            }
        }
        private static void CollectCandidates()
        {
            candidates.Clear();
            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                return;
            }
            BasisObservableList<BasisInput> devices = manager.AllInputDevices;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (input == null)
                {
                    continue;
                }
                // A candidate puck is either unassigned (no role captured yet) or already on a full-body
                // role. Hands / HMD / neck keep their roles and are excluded.
                if (input.TryGetRole(out BasisBoneTrackedRole role) && !BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                {
                    continue;
                }
                candidates.Add(input);
            }
        }
        private static bool TryNearestCandidate(Vector3 target, out Vector3 pos, out Quaternion rot, out float dist)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            dist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                BasisInput input = candidates[i];
                Transform t = input != null ? input.transform : null;
                if (t == null)
                {
                    continue;
                }
                float d = Vector3.Distance(t.position, target);
                if (d < dist)
                {
                    dist = d;
                    pos = t.position;
                    rot = t.rotation;
                    found = true;
                }
            }
            return found;
        }
        // Create-or-update, robust to the debug gizmo pool being wiped (ShowGizmos toggled off).
        private static void EnsureBall(BasisBoneTrackedRole role, Vector3 pos, float diameter, Color color)
        {
            if (balls.TryGetValue(role, out int id) && BasisGizmoManager.Exists(id))
            {
                BasisGizmoManager.UpdateSphereGizmo(id, pos, Vector3.one * diameter);
                BasisGizmoManager.UpdateGizmoColor(id, color);
                return;
            }
            if (BasisGizmoManager.CreateSphereGizmo($"LockIn_{role}", out id, pos, diameter, color))
            {
                balls[role] = id;
            }
        }
        private static void EnsureLine(BasisBoneTrackedRole role, Vector3 trackerPos, Vector3 bonePos, float width, Color color)
        {
            if (lines.TryGetValue(role, out int id) && BasisGizmoManager.Exists(id))
            {
                BasisGizmoManager.UpdateLineGizmo(id, trackerPos, bonePos);
                BasisGizmoManager.UpdateGizmoColor(id, color);
                return;
            }
            if (BasisGizmoManager.CreateLineGizmo($"LockInLink_{role}", out id, trackerPos, bonePos, width, color))
            {
                lines[role] = id;
            }
        }
        private static void HideRole(BasisBoneTrackedRole role)
        {
            if (balls.TryGetValue(role, out int ballId))
            {
                SafeDestroy(ballId);
                balls.Remove(role);
            }
            if (lines.TryGetValue(role, out int lineId))
            {
                SafeDestroy(lineId);
                lines.Remove(role);
            }
        }
        private static void DestroyAll()
        {
            if (balls.Count > 0)
            {
                foreach (KeyValuePair<BasisBoneTrackedRole, int> kvp in balls)
                {
                    SafeDestroy(kvp.Value);
                }
                balls.Clear();
            }
            if (lines.Count > 0)
            {
                foreach (KeyValuePair<BasisBoneTrackedRole, int> kvp in lines)
                {
                    SafeDestroy(kvp.Value);
                }
                lines.Clear();
            }
        }
        // The debug ShowGizmos teardown wipes the manager's pools out from under our cached IDs;
        // only call DestroyGizmo when the ID is still live so we don't log spurious warnings.
        private static void SafeDestroy(int id)
        {
            if (BasisGizmoManager.Exists(id))
            {
                BasisGizmoManager.DestroyGizmo(id);
            }
        }
    }
}
