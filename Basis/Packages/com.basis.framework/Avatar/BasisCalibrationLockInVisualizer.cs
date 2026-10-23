using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
namespace Basis.Scripts.Avatar
{
    public struct BasisCalibrationStageFrame
    {
        public Vector3 Origin, Forward;
        public float Drift;
        public bool Valid, FromHands;
        public bool Track(Vector3 head, Vector3 facing, bool fromHands, float deltaTime)
        {
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f)
            {
                if (Valid)
                {
                    return false;
                }
                facing = Vector3.forward;
            }
            facing.Normalize();
            Vector3 offset = head - Origin;
            offset.y = 0f;
            float angle = Valid ? Vector3.Angle(Forward, facing) : 180f, distance = offset.magnitude;
            bool raised = fromHands && !FromHands && (angle > BasisCalibrationLockInCore.StageSettleAngleDeg || distance > BasisCalibrationLockInCore.StageSettleDistance);
            FromHands = fromHands;
            Drift = angle > BasisCalibrationLockInCore.StageRecenterAngleDeg || distance > BasisCalibrationLockInCore.StageRecenterDistance ? Drift + deltaTime : 0f;
            if (Valid && !raised && Drift < BasisCalibrationLockInCore.StageRecenterSeconds)
            {
                return false;
            }
            Origin = head;
            Forward = facing;
            Drift = 0f;
            Valid = true;
            return true;
        }
    }
    [AutoStaticsCleanup]
    public static partial class BasisCalibrationLockInVisualizer
    {
        public static bool Enabled = true;
        // One slot after the bone-driver gizmos (250) so the bone transforms have settled.
        private const int RenderPriority = 251;
        // Tuning as fractions of world eye height so the aid scales with avatar size.
        private const float CaptureFrac = 0.06f;      // tracker within this of the bone -> locked (min size, green)
        private const float FalloffFrac = 0.30f;      // beyond this the ring/line hide (no tracker near the bone)
        private const float MinDiameterFrac = 0.04f, MaxDiameterFrac = 0.08f, LineWidthFrac = 0.006f;
        private const float HandMinDiameterFrac = 0.05f, HandMaxDiameterFrac = 0.12f, HeadTargetDistance = 0.6f, HeadTargetRadiusFrac = 0.025f, GazeClampRadii = 4f, LabelScale = 0.012f;
        private static readonly Color FarColor = new Color(1f, 0.45f, 0.12f, 1f);
        private static readonly Color LockedColor = new Color(0.15f, 1f, 0.35f, 1f);
        private static bool active, registered;
        private static int headRing = -1, gazeRing = -1, promptLabel = -1;
        private static string promptText;
        private static BasisCalibrationStageFrame stage;
        private static readonly Dictionary<BasisBoneTrackedRole, int> rings = new Dictionary<BasisBoneTrackedRole, int>();
        private static readonly Dictionary<BasisBoneTrackedRole, int> lines = new Dictionary<BasisBoneTrackedRole, int>();
        private static readonly List<BasisInput> candidates = new List<BasisInput>(16);
        private static readonly Vector3[] ringPoints = new Vector3[32];
        public static void Begin()
        {
            active = true;
            stage = default;
            promptText = BasisLocalization.Get("calibration.tposePrompt") + "\n" + BasisLocalization.Get(BasisDeviceManagement.IsUserInDesktop() ? "calibration.clickToConfirm" : "calibration.pullTriggers");
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
        public static float WorldEyeHeight()
        {
            float deviceScale = BasisHeightDriver.DeviceScale;
            if (!(deviceScale > 0f) || float.IsInfinity(deviceScale))
            {
                deviceScale = 1f;
            }
            return Mathf.Max(BasisHeightDriver.PlayerEyeHeight, 1.0f) * deviceScale;
        }
        public static float PlayerSizeScale() => Mathf.Clamp(WorldEyeHeight() / BasisHeightDriver.FallbackHeightInMeters, 0.25f, 4f);
        public static bool TryGetViewer(out Vector3 eye, out Quaternion rotation, out Vector3 facing, out bool fromHands)
        {
            fromHands = false;
            BasisLocalBoneControl eyeControl = BasisLocalBoneDriver.EyeControl;
            if (eyeControl == null)
            {
                eye = Vector3.zero;
                rotation = Quaternion.identity;
                facing = Vector3.forward;
                return false;
            }
            var world = eyeControl.OutgoingWorldData;
            eye = world.position;
            rotation = world.rotation;
            facing = rotation * Vector3.forward;
            BasisLocalBoneControl left = BasisLocalBoneDriver.LeftHandControl, right = BasisLocalBoneDriver.RightHandControl;
            if (left != null && right != null && left.HasTracked == BasisHasTracked.HasTracker && right.HasTracked == BasisHasTracked.HasTracker && BasisCalibrationLockInCore.TryBodyForwardFromHands(left.OutgoingWorldData.position, right.OutgoingWorldData.position, facing, BasisCalibrationLockInCore.SpreadHandsFraction * WorldEyeHeight(), out Vector3 body))
            {
                facing = body;
                fromHands = true;
            }
            return true;
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
            float eyeHeight = WorldEyeHeight(), capture = CaptureFrac * eyeHeight, falloff = FalloffFrac * eyeHeight, minDiam = MinDiameterFrac * eyeHeight;
            float maxDiam = MaxDiameterFrac * eyeHeight, lineWidth = LineWidthFrac * eyeHeight;
            bool hasViewer = TryGetViewer(out Vector3 eye, out Quaternion eyeRotation, out Vector3 facing, out bool fromHands);
            if (hasViewer)
            {
                stage.Track(eye, facing, fromHands, Time.deltaTime);
            }
            bool framed = stage.Valid;
            Vector3 bodyForward = framed ? stage.Forward : Vector3.forward, viewer = eye;
            if (BasisCalibrationMirrorService.Available && BasisCalibrationMirrorService.Provider.TryGetSurface(out Vector3 mirrorPosition, out Vector3 mirrorNormal))
            {
                viewer = BasisCalibrationLockInCore.ReflectAcrossPlane(eye, mirrorPosition, mirrorNormal);
                Vector3 mirrorFacing = new Vector3(-mirrorNormal.x, 0f, -mirrorNormal.z);
                if (mirrorFacing.sqrMagnitude > 1e-6f)
                {
                    bodyForward = mirrorFacing.normalized;
                    framed = true;
                }
            }

            CollectCandidates();

            int count = boneDriver.ControlsLength;
            for (int i = 0; i < count; i++)
            {
                BasisBoneTrackedRole role = boneDriver.trackedRoles[i];
                if (role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand)
                {
                    continue;
                }

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

                EnsureRing(role, bonePos, viewer, diameter * 0.5f, lineWidth, color);
                EnsureLine(role, trackerPos, bonePos, lineWidth, color);
            }
            Vector3 avatarForward = player.AvatarTransform != null ? player.AvatarTransform.forward : bodyForward;
            avatarForward.y = 0f;
            Quaternion armYaw = framed && avatarForward.sqrMagnitude > 1e-6f ? Quaternion.AngleAxis(Vector3.SignedAngle(avatarForward, bodyForward, Vector3.up), Vector3.up) : Quaternion.identity;
            DrawWrist(player.LocalAvatarDriver, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftUpperArm, BasisLocalBoneDriver.LeftHandControl, armYaw, viewer, eyeHeight, lineWidth);
            DrawWrist(player.LocalAvatarDriver, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightUpperArm, BasisLocalBoneDriver.RightHandControl, armYaw, viewer, eyeHeight, lineWidth);
            DrawHeadTarget(hasViewer, eye, eyeRotation, bodyForward, eyeHeight, lineWidth);
        }
        private static void DrawWrist(BasisLocalAvatarDriver avatar, BasisBoneTrackedRole handRole, BasisBoneTrackedRole upperArmRole, BasisLocalBoneControl handControl, Quaternion armYaw, Vector3 viewer, float eyeHeight, float lineWidth)
        {
            if (avatar == null || avatar.StoredRolesTransforms == null || handControl == null || handControl.HasTracked != BasisHasTracked.HasTracker
                || !avatar.StoredRolesTransforms.TryGetValue(handRole, out Transform wrist) || wrist == null
                || !avatar.StoredRolesTransforms.TryGetValue(upperArmRole, out Transform shoulder) || shoulder == null
                || !avatar.StoredRolesTransforms.TryGetValue(BasisBoneTrackedRole.Head, out Transform head) || head == null)
            {
                HideRole(handRole);
                return;
            }
            Vector3 hand = handControl.OutgoingWorldData.position, pivot = head.position;
            float error = BasisCalibrationLockInCore.ArmLineErrorDegrees(pivot + armYaw * (shoulder.position - pivot), pivot + armYaw * (wrist.position - pivot), hand, out Vector3 onLine);
            float weight = BasisCalibrationLockInCore.AngleScore(error, BasisCalibrationLockInCore.WristToleranceDeg, BasisCalibrationLockInCore.WristFalloffDeg);
            float diameter = BasisCalibrationLockInCore.ProximityRadius(weight, HandMinDiameterFrac * eyeHeight, HandMaxDiameterFrac * eyeHeight);
            Color color = Color.Lerp(FarColor, LockedColor, weight);
            EnsureRing(handRole, onLine, viewer, diameter * 0.5f, lineWidth, color);
            EnsureLine(handRole, hand, onLine, lineWidth, color);
        }
        private static void DrawHeadTarget(bool hasViewer, Vector3 eye, Quaternion eyeRotation, Vector3 forward, float eyeHeight, float lineWidth)
        {
            if (!hasViewer)
            {
                DestroyHeadTarget();
                return;
            }
            float radius = HeadTargetRadiusFrac * eyeHeight, size = PlayerSizeScale();
            Vector3 center = eye + forward * (HeadTargetDistance * size);
            BasisCalibrationLockInCore.HeadErrors(eyeRotation, forward, out float pitch, out float roll, out float yaw);
            Color color = Color.Lerp(FarColor, LockedColor, BasisCalibrationLockInCore.HeadLevelScore(pitch, roll, yaw));
            EnsureRing(ref headRing, "LockInHead", center, eye, radius, lineWidth, color);
            if (BasisCalibrationLockInCore.TryGazeOnPlane(eye, eyeRotation * Vector3.forward, center, -forward, out Vector3 hit))
            {
                EnsureRing(ref gazeRing, "LockInGaze", center + Vector3.ClampMagnitude(hit - center, radius * GazeClampRadii), eye, radius * 0.25f, lineWidth, color);
            }
            else
            {
                SafeDestroy(ref gazeRing);
            }
            Vector3 labelPosition = center - Vector3.up * (radius * 2.5f);
            string text = promptText ?? string.Empty;
            if ((promptLabel < 0 || !BasisGizmoManager.Exists(promptLabel)) && !BasisGizmoManager.CreateTextGizmo("LockInPrompt", out promptLabel, labelPosition, text, Color.white))
            {
                promptLabel = -1;
                return;
            }
            BasisGizmoManager.UpdateTextGizmo(promptLabel, labelPosition, BasisGizmoManager.BillboardRotation(labelPosition, eye), LabelScale * size, text, Color.white);
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
        private static void EnsureRing(BasisBoneTrackedRole role, Vector3 center, Vector3 viewer, float radius, float width, Color color)
        {
            BasisCalibrationLockInCore.RingPoints(center, viewer - center, radius, ringPoints);
            if (rings.TryGetValue(role, out int id) && BasisGizmoManager.Exists(id))
            {
                BasisGizmoManager.UpdateLineGizmo(id, ringPoints);
                BasisGizmoManager.UpdateGizmoColor(id, color);
                return;
            }
            if (BasisGizmoManager.CreateLineGizmo($"LockIn_{role}", out id, ringPoints, width, color, true))
            {
                rings[role] = id;
            }
        }
        private static void EnsureRing(ref int id, string name, Vector3 center, Vector3 viewer, float radius, float width, Color color)
        {
            BasisCalibrationLockInCore.RingPoints(center, viewer - center, radius, ringPoints);
            if (id >= 0 && BasisGizmoManager.Exists(id))
            {
                BasisGizmoManager.UpdateLineGizmo(id, ringPoints);
                BasisGizmoManager.UpdateGizmoColor(id, color);
                return;
            }
            if (!BasisGizmoManager.CreateLineGizmo(name, out id, ringPoints, width, color, true))
            {
                id = -1;
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
            if (rings.TryGetValue(role, out int ringId))
            {
                SafeDestroy(ringId);
                rings.Remove(role);
            }
            if (lines.TryGetValue(role, out int lineId))
            {
                SafeDestroy(lineId);
                lines.Remove(role);
            }
        }
        private static void DestroyHeadTarget()
        {
            SafeDestroy(ref headRing);
            SafeDestroy(ref gazeRing);
            SafeDestroy(ref promptLabel);
        }
        private static void DestroyAll()
        {
            if (rings.Count > 0)
            {
                foreach (KeyValuePair<BasisBoneTrackedRole, int> kvp in rings)
                {
                    SafeDestroy(kvp.Value);
                }
                rings.Clear();
            }
            if (lines.Count > 0)
            {
                foreach (KeyValuePair<BasisBoneTrackedRole, int> kvp in lines)
                {
                    SafeDestroy(kvp.Value);
                }
                lines.Clear();
            }
            DestroyHeadTarget();
        }
        private static void SafeDestroy(ref int id)
        {
            if (id >= 0)
            {
                SafeDestroy(id);
                id = -1;
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
