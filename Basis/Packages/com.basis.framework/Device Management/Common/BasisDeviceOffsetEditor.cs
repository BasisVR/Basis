using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    [AutoStaticsCleanup]
    public static partial class BasisDeviceOffsetEditor
    {
        public const int TickPriority = 200;
        public const float GrabRadius = 0.15f;
        private const float HandleSize = 0.045f;
        private const float OriginalSize = 0.03f;
        private const float AxisLength = 0.08f;
        private const float RingRadius = 0.06f;
        private const int RingSegments = 32;
        private const float LineWidth = 0.004f;
        private const float ThinLineWidth = 0.002f;
        private const float LabelScale = 0.012f;
        private static readonly Color IdleColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        private static readonly Color HoverColor = new Color(1f, 0.85f, 0.35f, 1f);
        private static readonly Color HeldColor = new Color(0.4f, 1f, 0.55f, 1f);
        private static readonly Color OriginalColor = new Color(0.2f, 0.45f, 1f, 1f);
        private static readonly Color OriginalLineColor = new Color(0.2f, 0.45f, 1f, 0.6f);
        [NoAutoStaticsCleanup] private static readonly Color[] AxisColors = { new Color(1f, 0.35f, 0.35f, 1f), new Color(0.45f, 1f, 0.45f, 1f), new Color(0.45f, 0.6f, 1f, 1f) };
        [NoAutoStaticsCleanup] private static readonly Vector3[] AxisVectors = { Vector3.right, Vector3.up, Vector3.forward };
        [NoAutoStaticsCleanup] private static readonly BasisDeviceOffsetAxes[] PositionAxisFlags = { BasisDeviceOffsetAxes.PositionX, BasisDeviceOffsetAxes.PositionY, BasisDeviceOffsetAxes.PositionZ };
        private static readonly BasisGizmoSet gizmos = new BasisGizmoSet("DeviceOffsetHandles");
        private static readonly BasisGizmoSet rings = new BasisGizmoSet("DeviceOffsetRings");
        [NoAutoStaticsCleanup] private static readonly Vector3[] ringPoints = new Vector3[RingSegments];
        private static readonly List<BasisInput> targets = new List<BasisInput>();
        private static readonly List<BasisInput> hands = new List<BasisInput>();
        private static readonly List<BasisInput> staleHands = new List<BasisInput>();
        private static readonly Dictionary<BasisInput, bool> gripLatch = new Dictionary<BasisInput, bool>();
        private static readonly Dictionary<BasisInput, BasisInput> hoverByHand = new Dictionary<BasisInput, BasisInput>();
        [NoAutoStaticsCleanup] private static readonly BasisInput[] grabbers = new BasisInput[2];
        private static string[] roleLabels;
        private static int grabberCount;
        private static int anchorMode;
        private static BasisInput anchorLead;
        private static Vector3 anchorFramePosition;
        private static Quaternion anchorFrameRotation = Quaternion.identity;
        private static Vector3 anchorPosition;
        private static Quaternion anchorRotation = Quaternion.identity;
        private static Vector3 basePosition;
        private static Vector3 baseEuler;
        private static Vector3 eulerReference;
        private static Vector3 frameUp = Vector3.up;
        private static bool registered;
        private static bool hooked;

        public static event Action OnStateChanged;
        public static BasisDeviceOffsetAxes EditAxes { get; private set; }
        public static bool IsEditing => EditAxes != BasisDeviceOffsetAxes.None;
        public static string SelectedKey { get; private set; }
        public static string TargetKey { get; private set; }
        public static BasisInput Target { get; private set; }
        public static bool IsGrabbing => grabberCount > 0;

        public static Color AxisColor(int axis)
        {
            return AxisColors[Mathf.Clamp(axis, 0, AxisColors.Length - 1)];
        }

        public static bool IsAxisEnabled(BasisDeviceOffsetAxes axis)
        {
            return (EditAxes & axis) != 0;
        }

        public static void SetAxisEnabled(BasisDeviceOffsetAxes axis, bool enabled)
        {
            SetEditAxes(enabled ? EditAxes | axis : EditAxes & ~axis);
        }

        public static void SetEditAxes(BasisDeviceOffsetAxes axes)
        {
            if (EditAxes == axes)
            {
                return;
            }
            bool wasEditing = IsEditing;
            EditAxes = axes;
            if (IsEditing && !wasEditing)
            {
                if (!registered)
                {
                    BasisLocalPlayer.AfterSimulateOnLate.AddAction(TickPriority, Tick);
                    registered = true;
                }
                EnsureMasterHook();
            }
            else if (!IsEditing)
            {
                EndGrab();
                if (registered)
                {
                    BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(TickPriority, Tick);
                    registered = false;
                }
                gizmos.Clear();
                rings.Clear();
                gripLatch.Clear();
                hoverByHand.Clear();
            }
            else
            {
                anchorMode = 0;
            }
            OnStateChanged?.Invoke();
        }

        public static void Select(string key)
        {
            if (SelectedKey == key)
            {
                return;
            }
            SelectedKey = key;
            OnStateChanged?.Invoke();
        }

        public static bool IsCapturing(BasisInput input)
        {
            if (grabberCount == 0 || input == null)
            {
                return false;
            }
            for (int index = 0; index < grabberCount; index++)
            {
                if (ReferenceEquals(grabbers[index], input))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasGrabbingHand()
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return false;
            }
            var devices = management.AllInputDevices;
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                if (IsHand(devices[index]))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsTargetable(BasisInput input)
        {
            return input != null && input.HasEvents && !string.IsNullOrEmpty(input.DeviceOffsetKey) && input.TryGetRole(out _) && !(input is BasisInputController && input.TrackingHardware == BasisTrackingHardware.Optical);
        }

        public static string RoleLabel(BasisBoneTrackedRole role)
        {
            if (roleLabels == null)
            {
                BasisBoneTrackedRole[] roles = (BasisBoneTrackedRole[])Enum.GetValues(typeof(BasisBoneTrackedRole));
                int highest = 0;
                for (int index = 0; index < roles.Length; index++)
                {
                    highest = Mathf.Max(highest, (int)roles[index]);
                }
                roleLabels = new string[highest + 1];
                for (int index = 0; index < roles.Length; index++)
                {
                    roleLabels[(int)roles[index]] = SplitWords(roles[index].ToString());
                }
            }
            int slot = (int)role;
            return slot >= 0 && slot < roleLabels.Length && roleLabels[slot] != null ? roleLabels[slot] : role.ToString();
        }

        private static bool IsHand(BasisInput input)
        {
            return input != null && input.HasEvents && input.TryGetRole(out BasisBoneTrackedRole role) && (role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand);
        }

        private static void Tick()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (!IsEditing || player == null || management == null || !BasisLocalPlayer.PlayerReady)
            {
                return;
            }
            Collect(management);
            ReleaseGrabbers();
            HandlePresses();
            if (grabberCount > 0)
            {
                Drive();
            }
            Draw(player);
        }

        private static void Collect(BasisDeviceManagement management)
        {
            targets.Clear();
            hands.Clear();
            var devices = management.AllInputDevices;
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput input = devices[index];
                if (IsHand(input))
                {
                    hands.Add(input);
                }
                if (IsTargetable(input))
                {
                    targets.Add(input);
                }
            }
        }

        private static void ReleaseGrabbers()
        {
            if (grabberCount == 0)
            {
                return;
            }
            if (Target == null || !targets.Contains(Target) || Target.DeviceOffsetKey != TargetKey)
            {
                EndGrab();
                return;
            }
            int kept = 0;
            for (int index = 0; index < grabberCount; index++)
            {
                BasisInput grabber = grabbers[index];
                if (grabber != null && hands.Contains(grabber) && grabber.CurrentInputState.GripButton)
                {
                    grabbers[kept++] = grabber;
                }
                else if (grabber != null)
                {
                    grabber.PlayHaptic(0.03f, 0.3f);
                }
            }
            for (int index = kept; index < grabbers.Length; index++)
            {
                grabbers[index] = null;
            }
            grabberCount = kept;
            if (kept == 0)
            {
                EndGrab();
            }
        }

        private static void HandlePresses()
        {
            int count = hands.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput hand = hands[index];
                bool down = hand.CurrentInputState.GripButton;
                bool pressed = gripLatch.TryGetValue(hand, out bool wasDown) && down && !wasDown;
                gripLatch[hand] = down;
                if (IsCapturing(hand))
                {
                    UpdateHover(hand, null);
                    continue;
                }
                BasisInput hovered = FindNearestTarget(hand);
                UpdateHover(hand, hovered);
                if (!pressed || hovered == null || BasisLocalPlayspaceMover.IsHandHoldingObject(hand))
                {
                    continue;
                }
                if (grabberCount == 0)
                {
                    BeginGrab(hovered, hand);
                }
                else if (grabberCount == 1 && ReferenceEquals(hovered, Target))
                {
                    grabbers[grabberCount++] = hand;
                    hand.PlayHaptic(0.05f, 0.5f);
                }
            }
            PruneLatches();
        }

        private static BasisInput FindNearestTarget(BasisInput hand)
        {
            hand.GetPhysicalUnscaledPose(out Vector3 handPosition, out _);
            BasisInput nearest = null;
            float best = GrabRadius * GrabRadius;
            int count = targets.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput target = targets[index];
                if (ReferenceEquals(target, hand))
                {
                    continue;
                }
                target.GetPhysicalUnscaledPose(out Vector3 physicalPosition, out Quaternion physicalRotation);
                float distance = (physicalPosition + (physicalRotation * target.DeviceOffsetPosition) - handPosition).sqrMagnitude;
                if (target is BasisInputController controller)
                {
                    distance = Mathf.Min(distance, (controller.UnscaledHandTarget - handPosition).sqrMagnitude);
                }
                if (distance < best)
                {
                    best = distance;
                    nearest = target;
                }
            }
            return nearest;
        }

        private static void UpdateHover(BasisInput hand, BasisInput hovered)
        {
            hoverByHand.TryGetValue(hand, out BasisInput previous);
            if (ReferenceEquals(previous, hovered))
            {
                return;
            }
            hoverByHand[hand] = hovered;
            if (hovered != null)
            {
                hand.PlayHaptic(0.02f, 0.15f);
            }
        }

        private static bool IsHovered(BasisInput target)
        {
            foreach (KeyValuePair<BasisInput, BasisInput> pair in hoverByHand)
            {
                if (ReferenceEquals(pair.Value, target))
                {
                    return true;
                }
            }
            return false;
        }

        private static void BeginGrab(BasisInput target, BasisInput hand)
        {
            Target = target;
            TargetKey = target.DeviceOffsetKey;
            grabbers[0] = hand;
            grabberCount = 1;
            anchorMode = 0;
            anchorLead = null;
            SelectedKey = TargetKey;
            hand.PlayHaptic(0.06f, 0.6f);
            OnStateChanged?.Invoke();
        }

        private static void EndGrab()
        {
            if (TargetKey == null)
            {
                return;
            }
            string key = TargetKey;
            grabbers[0] = null;
            grabbers[1] = null;
            grabberCount = 0;
            anchorMode = 0;
            anchorLead = null;
            Target = null;
            TargetKey = null;
            BasisDeviceOffsets.Persist(key);
            OnStateChanged?.Invoke();
        }

        private static void Drive()
        {
            GetManipulationFrame(out Vector3 framePosition, out Quaternion frameRotation, out int mode);
            Target.GetPhysicalUnscaledPose(out Vector3 physicalPosition, out Quaternion physicalRotation);
            if (mode != anchorMode || !ReferenceEquals(anchorLead, grabbers[0]))
            {
                BasisDeviceOffsetMath.Compose(physicalPosition, physicalRotation, Target.DeviceOffsetPosition, Target.DeviceOffsetRotation, out anchorPosition, out anchorRotation);
                anchorFramePosition = framePosition;
                anchorFrameRotation = frameRotation;
                basePosition = Target.DeviceOffsetPosition;
                baseEuler = BasisDeviceOffsetMath.ToEuler(Target.DeviceOffsetRotation);
                eulerReference = baseEuler;
                anchorMode = mode;
                anchorLead = grabbers[0];
                return;
            }
            BasisDeviceOffsetMath.FollowFrame(anchorFramePosition, anchorFrameRotation, anchorPosition, anchorRotation, framePosition, frameRotation, out Vector3 heldPosition, out Quaternion heldRotation);
            BasisDeviceOffsetMath.SolveOffset(physicalPosition, physicalRotation, heldPosition, heldRotation, out Vector3 freePosition, out Quaternion freeRotation);
            BasisDeviceOffsetMath.ConstrainOffset(EditAxes, basePosition, baseEuler, freePosition, freeRotation, ref eulerReference, out Vector3 offsetPosition, out Quaternion offsetRotation);
            BasisDeviceOffsets.Set(TargetKey, offsetPosition, offsetRotation, false, null);
        }

        private static void GetManipulationFrame(out Vector3 position, out Quaternion rotation, out int mode)
        {
            grabbers[0].GetPhysicalUnscaledPose(out position, out rotation);
            mode = 1;
            if (grabberCount < 2)
            {
                return;
            }
            grabbers[1].GetPhysicalUnscaledPose(out Vector3 secondPosition, out Quaternion secondRotation);
            if (BasisDeviceOffsetMath.TryBuildTwoHandFrame(position, rotation, secondPosition, secondRotation, frameUp, out Vector3 framePosition, out Quaternion frameRotation))
            {
                position = framePosition;
                rotation = frameRotation;
                frameUp = frameRotation * Vector3.up;
                mode = 2;
            }
        }

        private static void Draw(BasisLocalPlayer player)
        {
            Transform root = player.transform;
            Matrix4x4 rootMatrix = root.localToWorldMatrix;
            Quaternion rootRotation = root.rotation;
            float deviceScale = Sanitize(BasisHeightDriver.DeviceScale);
            float nodeScale = Sanitize(BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
            float handleSize = HandleSize * nodeScale;
            Vector3 viewer = BasisLocalCameraDriver.Position;
            gizmos.Begin();
            rings.Begin();
            int count = targets.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput target = targets[index];
                target.GetPhysicalUnscaledPose(out Vector3 physicalPosition, out Quaternion physicalRotation);
                BasisDeviceOffsetMath.Compose(physicalPosition, physicalRotation, target.DeviceOffsetPosition, target.DeviceOffsetRotation, out Vector3 virtualPosition, out Quaternion virtualRotation);
                ToWorld(rootMatrix, rootRotation, deviceScale, physicalPosition, physicalRotation, out Vector3 original, out Quaternion originalRotation);
                ToWorld(rootMatrix, rootRotation, deviceScale, virtualPosition, virtualRotation, out Vector3 handle, out Quaternion handleRotation);
                bool held = ReferenceEquals(target, Target);
                bool hovered = !held && IsHovered(target);
                Color color = held ? HeldColor : hovered ? HoverColor : IdleColor;
                gizmos.Sphere(original, OriginalSize * nodeScale, OriginalColor);
                if (target.HasDeviceOffset)
                {
                    gizmos.Line(original, handle, OriginalLineColor, ThinLineWidth);
                }
                gizmos.Sphere(handle, handleSize, color);
                if (held || hovered)
                {
                    DrawConstraints(handle, originalRotation, handleRotation, target.DeviceOffsetRotation, nodeScale);
                }
                if (target.TryGetRole(out BasisBoneTrackedRole role))
                {
                    gizmos.Label(handle + (Vector3.up * (handleSize * 1.5f)), RoleLabel(role), color, viewer, LabelScale * nodeScale);
                }
            }
            gizmos.End();
            rings.End();
        }

        private static void DrawConstraints(Vector3 handle, Quaternion originalRotation, Quaternion handleRotation, Quaternion offsetRotation, float nodeScale)
        {
            float axisLength = AxisLength * nodeScale;
            for (int axis = 0; axis < 3; axis++)
            {
                if (IsAxisEnabled(PositionAxisFlags[axis]))
                {
                    Vector3 direction = originalRotation * (AxisVectors[axis] * axisLength);
                    gizmos.Line(handle - direction, handle + direction, AxisColors[axis], LineWidth);
                }
            }
            float radius = RingRadius * nodeScale;
            if (IsAxisEnabled(BasisDeviceOffsetAxes.RotationX))
            {
                Quaternion yawFrame = originalRotation * BasisDeviceOffsetMath.FromEuler(new Vector3(0f, BasisDeviceOffsetMath.ToEuler(offsetRotation).y, 0f));
                DrawRing(handle, yawFrame, Vector3.up, Vector3.forward, radius, AxisColors[0]);
            }
            if (IsAxisEnabled(BasisDeviceOffsetAxes.RotationY))
            {
                DrawRing(handle, originalRotation, Vector3.right, Vector3.forward, radius, AxisColors[1]);
            }
            if (IsAxisEnabled(BasisDeviceOffsetAxes.RotationZ))
            {
                DrawRing(handle, handleRotation, Vector3.right, Vector3.up, radius, AxisColors[2]);
            }
        }

        private static void DrawRing(Vector3 center, Quaternion frame, Vector3 first, Vector3 second, float radius, Color color)
        {
            float step = Mathf.PI * 2f / RingSegments;
            for (int index = 0; index < RingSegments; index++)
            {
                float angle = index * step;
                ringPoints[index] = center + (frame * (((first * Mathf.Cos(angle)) + (second * Mathf.Sin(angle))) * radius));
            }
            rings.Poly(ringPoints, color, true, LineWidth);
        }

        private static void ToWorld(Matrix4x4 rootMatrix, Quaternion rootRotation, float deviceScale, Vector3 unscaledPosition, Quaternion unscaledRotation, out Vector3 position, out Quaternion rotation)
        {
            Vector3 local = BasisInput.OffsetCoords.position + (BasisInput.OffsetCoords.rotation * (unscaledPosition * deviceScale));
            Quaternion localRotation = BasisInput.OffsetCoords.rotation * unscaledRotation;
            BasisLocalPlayspaceMover.ApplyFlipToLocalPose(ref local, ref localRotation);
            position = rootMatrix.MultiplyPoint3x4(local);
            rotation = rootRotation * localRotation;
        }

        private static float Sanitize(float scale)
        {
            return scale > 1e-4f && !float.IsNaN(scale) && !float.IsInfinity(scale) ? scale : 1f;
        }

        private static void PruneLatches()
        {
            if (gripLatch.Count <= hands.Count && hoverByHand.Count <= hands.Count)
            {
                return;
            }
            staleHands.Clear();
            foreach (BasisInput hand in gripLatch.Keys)
            {
                if (!hands.Contains(hand))
                {
                    staleHands.Add(hand);
                }
            }
            foreach (BasisInput hand in hoverByHand.Keys)
            {
                if (!hands.Contains(hand) && !staleHands.Contains(hand))
                {
                    staleHands.Add(hand);
                }
            }
            for (int index = 0; index < staleHands.Count; index++)
            {
                gripLatch.Remove(staleHands[index]);
                hoverByHand.Remove(staleHands[index]);
            }
            staleHands.Clear();
        }

        private static string SplitWords(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length + 4);
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(character);
            }
            return builder.ToString();
        }

        private static void EnsureMasterHook()
        {
            if (hooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnMasterGizmoToggle;
            hooked = true;
        }

        private static void OnMasterGizmoToggle(bool state)
        {
            if (!state)
            {
                gizmos.Forget();
                rings.Forget();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            if (registered)
            {
                BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(TickPriority, Tick);
                registered = false;
            }
            EditAxes = BasisDeviceOffsetAxes.None;
            SelectedKey = null;
            Target = null;
            TargetKey = null;
            grabbers[0] = null;
            grabbers[1] = null;
            grabberCount = 0;
            anchorMode = 0;
            anchorLead = null;
            gripLatch.Clear();
            hoverByHand.Clear();
            gizmos.Forget();
            rings.Forget();
            OnStateChanged = null;
        }
    }
}
