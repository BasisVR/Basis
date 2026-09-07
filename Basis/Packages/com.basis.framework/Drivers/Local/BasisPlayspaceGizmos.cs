using System;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.Scripts.Drivers
{

    /// <summary>
    /// World-space picture of the local player's play space: the runtime's own play-area outline, the
    /// tracking origin that outline and every device pose are measured from, the offsets the play-space
    /// mover stacks on top (vertical space drag, horizontal drag, flip), and the hands driving them.
    /// <para>
    /// Everything is drawn where a tracked device at that point would actually land — tracking point,
    /// through the vertical lift, through the device scale, through the rig matrix — so the outline sits
    /// around the player at the size their real movement covers, and stays honest while the play space
    /// is dragged, scaled or tipped.
    /// </para>
    /// <para>
    /// Drawn through <see cref="BasisGizmoManager"/> (batched, no GameObjects) and ticked from the event
    /// driver immediately before the gizmo submission, so it carries the frame that is about to render.
    /// Independent of the developer gizmo master toggle, which only wipes handles when it goes off —
    /// hence the master hook.
    /// </para>
    /// </summary>
    public static class BasisPlayspaceGizmos
    {
        private const float LineWidth = 0.006f;
        private const float ThinLineWidth = 0.003f;
        private const float NodeSize = 0.04f;
        private const float SmallNodeSize = 0.025f;
        private const float LabelBaseScale = 0.014f;
        private const int RingSegments = 32;
        private const int MaxBoundaryPosts = 24;
        private const float BoundaryRefreshInterval = 2f;
        private const float FallbackRailHeight = 1.7f;
        private const float OriginRingRadius = 0.35f;
        private const float AxisLength = 0.5f;
        private const float DeadZone = 0.001f;

        private static readonly Color BoundaryColor = new Color(0.35f, 0.9f, 1f, 1f);
        private static readonly Color BoundaryRailColor = new Color(0.2f, 0.55f, 0.7f, 1f);
        private static readonly Color OriginColor = new Color(1f, 0.85f, 0.4f, 1f);
        private static readonly Color AxisXColor = new Color(1f, 0.35f, 0.35f, 1f);
        private static readonly Color AxisYColor = new Color(0.45f, 1f, 0.45f, 1f);
        private static readonly Color AxisZColor = new Color(0.45f, 0.6f, 1f, 1f);
        private static readonly Color RootColor = new Color(0.5f, 1f, 0.6f, 1f);
        private static readonly Color CapsuleColor = new Color(0.3f, 0.7f, 0.45f, 1f);
        private static readonly Color VerticalColor = new Color(0.6f, 0.75f, 1f, 1f);
        private static readonly Color DragColor = new Color(1f, 0.6f, 0.35f, 1f);
        private static readonly Color FlipColor = new Color(0.9f, 0.5f, 1f, 1f);
        private static readonly Color GhostColor = new Color(1f, 1f, 1f, 0.25f);
        private static readonly Color HandIdleColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color HandHeldColor = new Color(0.4f, 1f, 0.55f, 1f);
        private static readonly Color HandRotateColor = new Color(1f, 0.85f, 0.4f, 1f);
        private static readonly Color ReadoutColor = new Color(1f, 1f, 1f, 1f);

        private static readonly BasisGizmoSet _boundary = new BasisGizmoSet("PlayspaceBoundary");
        private static readonly BasisGizmoSet _origin = new BasisGizmoSet("PlayspaceOrigin");
        private static readonly BasisGizmoSet _offset = new BasisGizmoSet("PlayspaceOffset");
        private static readonly BasisGizmoSet _hands = new BasisGizmoSet("PlayspaceHands");
        private static readonly BasisGizmoSet _readouts = new BasisGizmoSet("PlayspaceReadouts");

        private static readonly Vector3[] _ring = new Vector3[RingSegments];
        private static readonly Vector3[] _ringB = new Vector3[RingSegments];
        private static Vector3[] _outline = Array.Empty<Vector3>();
        private static Vector3[] _outlineRail = Array.Empty<Vector3>();

        private static readonly StringBuilder _builder = new StringBuilder(220);
        private static string _readoutText = string.Empty;
        private static int _readoutKey;

        private static BasisPlayspaceGizmoLayers _drawn = BasisPlayspaceGizmoLayers.None;
        private static bool _hooked;

        /// <summary>
        /// Reads the layer toggles and draws this frame's play space. Cheap to call unconditionally:
        /// with every toggle off it drops any handles it still holds and returns.
        /// </summary>
        public static void Tick()
        {
            BasisPlayspaceGizmoLayers layers = ResolveLayers();
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (layers == BasisPlayspaceGizmoLayers.None || player == null || BasisLocalPlayer.PlayerReady == false)
            {
                ClearAll();
                return;
            }

            EnsureMasterHook();
            ClearDisabled(layers);
            _drawn = layers;

            Frame frame = BuildFrame();
            BasisPlayspaceBoundary.Poll(BoundaryRefreshInterval);

            if ((layers & BasisPlayspaceGizmoLayers.Boundary) != 0)
            {
                DrawBoundary(ref frame);
            }
            if ((layers & BasisPlayspaceGizmoLayers.Origin) != 0)
            {
                DrawOrigin(ref frame, player);
            }
            if ((layers & BasisPlayspaceGizmoLayers.Offset) != 0)
            {
                DrawOffsets(ref frame, player);
            }
            if ((layers & BasisPlayspaceGizmoLayers.Hands) != 0)
            {
                DrawHands(ref frame);
            }
            if ((layers & BasisPlayspaceGizmoLayers.Readouts) != 0)
            {
                DrawReadouts(ref frame);
            }
        }

        private static BasisPlayspaceGizmoLayers ResolveLayers()
        {
            if (BasisSettingsDefaults.PlayspaceGizmos.RawValue == false)
            {
                return BasisPlayspaceGizmoLayers.None;
            }

            BasisPlayspaceGizmoLayers layers = BasisPlayspaceGizmoLayers.None;
            if (BasisSettingsDefaults.PlayspaceGizmoBoundary.RawValue)
            {
                layers |= BasisPlayspaceGizmoLayers.Boundary;
            }
            if (BasisSettingsDefaults.PlayspaceGizmoOrigin.RawValue)
            {
                layers |= BasisPlayspaceGizmoLayers.Origin;
            }
            if (BasisSettingsDefaults.PlayspaceGizmoOffset.RawValue)
            {
                layers |= BasisPlayspaceGizmoLayers.Offset;
            }
            if (BasisSettingsDefaults.PlayspaceGizmoHands.RawValue)
            {
                layers |= BasisPlayspaceGizmoLayers.Hands;
            }
            if (BasisSettingsDefaults.PlayspaceGizmoReadouts.RawValue)
            {
                layers |= BasisPlayspaceGizmoLayers.Readouts;
            }
            return layers;
        }

        private static Frame BuildFrame()
        {
            float deviceScale = BasisHeightDriver.DeviceScale;
            if (deviceScale <= 1e-4f || float.IsNaN(deviceScale) || float.IsInfinity(deviceScale))
            {
                deviceScale = 1f;
            }

            float nodeScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            if (nodeScale <= 0f || float.IsNaN(nodeScale) || float.IsInfinity(nodeScale))
            {
                nodeScale = 1f;
            }

            float rail = BasisHeightDriver.SelectedUnScaledPlayerHeight;
            if (rail <= 0.2f || float.IsNaN(rail) || float.IsInfinity(rail))
            {
                rail = FallbackRailHeight;
            }

            bool isVR = BasisDeviceManagement.IsCurrentModeVR();
            return new Frame
            {
                Rig = BasisLocalPlayer.localToWorldMatrix,
                Lift = BasisPlayspaceGizmoCore.TrackingLift(isVR, BasisLocalPlayspaceMover.VerticalOffset,
                    SMModuleSitStand.IsSteatedMode, SMModuleSitStand.MissingHeightDelta, BasisHeightDriver.HeightModeGroundingOffset),
                VerticalOffset = isVR ? BasisLocalPlayspaceMover.VerticalOffset : 0f,
                DeviceScale = deviceScale,
                OffsetPosition = BasisInput.OffsetCoords.position,
                OffsetRotation = BasisInput.OffsetCoords.rotation,
                Viewer = BasisLocalCameraDriver.Position,
                NodeScale = nodeScale,
                RailHeight = rail,
            };
        }

        private static void DrawBoundary(ref Frame frame)
        {
            _boundary.Begin();

            var points = BasisPlayspaceBoundary.Points;
            if (BasisPlayspaceBoundary.Metrics.Valid)
            {
                int count = points.Count;
                if (_outline.Length != count)
                {
                    _outline = new Vector3[count];
                    _outlineRail = new Vector3[count];
                }

                for (int Index = 0; Index < count; Index++)
                {
                    Vector3 corner = points[Index];
                    _outline[Index] = frame.World(new Vector3(corner.x, 0f, corner.z));
                    _outlineRail[Index] = frame.World(new Vector3(corner.x, frame.RailHeight, corner.z));
                }

                _boundary.Poly(_outline, BoundaryColor, true, LineWidth);
                _boundary.Poly(_outlineRail, BoundaryRailColor, true, ThinLineWidth);

                int stride = Mathf.Max(1, Mathf.CeilToInt(count / (float)MaxBoundaryPosts));
                for (int Index = 0; Index < count; Index += stride)
                {
                    _boundary.Line(_outline[Index], _outlineRail[Index], BoundaryRailColor, ThinLineWidth);
                    _boundary.Sphere(_outline[Index], SmallNodeSize * frame.NodeScale, BoundaryColor);
                }
            }

            _boundary.End();
        }

        private static void DrawOrigin(ref Frame frame, BasisLocalPlayer player)
        {
            _origin.Begin();

            Vector3 origin = frame.World(Vector3.zero);
            BuildTrackingRing(ref frame, OriginRingRadius, frame.Lift, _ring);
            _origin.Poly(_ring, OriginColor, true, ThinLineWidth);
            _origin.Sphere(origin, NodeSize * frame.NodeScale, OriginColor);
            _origin.Line(origin, frame.World(new Vector3(AxisLength, 0f, 0f)), AxisXColor, LineWidth);
            _origin.Line(origin, frame.World(new Vector3(0f, 0f, AxisLength)), AxisZColor, LineWidth);
            _origin.Line(origin, frame.World(new Vector3(0f, AxisLength, 0f)), AxisYColor, ThinLineWidth);

            BasisLocalPose.GetPose(BasisPoseSlot.PlayerRoot, player.transform, out Vector3 rootPosition, out Quaternion rootRotation);
            _origin.Sphere(rootPosition, SmallNodeSize * frame.NodeScale, RootColor);
            _origin.Line(rootPosition, rootPosition + (rootRotation * Vector3.forward * (AxisLength * frame.NodeScale)), RootColor, LineWidth);
            _origin.Line(origin, rootPosition, GhostColor, ThinLineWidth);

            DrawCapsule(ref frame, player);

            _origin.End();
        }

        private static void DrawCapsule(ref Frame frame, BasisLocalPlayer player)
        {
            BasisLocalCharacterDriver driver = player.LocalCharacterDriver;
            CharacterController controller = driver != null ? driver.characterController : null;
            if (controller == null || controller.enabled == false)
            {
                return;
            }

            Transform owner = controller.transform;
            Vector3 lossy = owner.lossyScale;
            float radius = controller.radius * Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.z));
            float half = Mathf.Max((controller.height * Mathf.Abs(lossy.y) * 0.5f) - radius, 0f);
            Vector3 center = owner.TransformPoint(controller.center);
            Vector3 bottom = center - (Vector3.up * half);
            Vector3 top = center + (Vector3.up * half);

            BuildWorldRing(bottom, radius, _ring);
            BuildWorldRing(top, radius, _ringB);
            _origin.Poly(_ring, CapsuleColor, true, ThinLineWidth);
            _origin.Poly(_ringB, CapsuleColor, true, ThinLineWidth);

            int quarter = RingSegments / 4;
            for (int Index = 0; Index < 4; Index++)
            {
                int point = Index * quarter;
                _origin.Line(_ring[point], _ringB[point], CapsuleColor, ThinLineWidth);
            }
        }

        private static void DrawOffsets(ref Frame frame, BasisLocalPlayer player)
        {
            _offset.Begin();

            if (Mathf.Abs(frame.VerticalOffset) > DeadZone)
            {
                float restingLift = frame.Lift - frame.VerticalOffset;
                Vector3 lifted = frame.World(Vector3.zero);
                Vector3 resting = frame.WorldAt(Vector3.zero, restingLift);
                _offset.Line(resting, lifted, VerticalColor, LineWidth);
                _offset.Sphere(resting, SmallNodeSize * frame.NodeScale, GhostColor);
                BuildTrackingRing(ref frame, OriginRingRadius, restingLift, _ring);
                _offset.Poly(_ring, GhostColor, true, ThinLineWidth);
            }

            Vector3 drag = BasisLocalPlayspaceMover.CurrentOffset;
            if (drag.sqrMagnitude > DeadZone * DeadZone)
            {
                BasisLocalPose.GetPose(BasisPoseSlot.PlayerRoot, player.transform, out Vector3 rootPosition, out _);
                Vector3 start = rootPosition - drag;
                _offset.Line(start, rootPosition, DragColor, LineWidth);
                _offset.Sphere(start, SmallNodeSize * frame.NodeScale, DragColor);
                BuildWorldRing(start, OriginRingRadius * frame.NodeScale, _ring);
                _offset.Poly(_ring, DragColor, true, ThinLineWidth);
            }

            if (BasisLocalPlayspaceMover.HasFlip)
            {
                Vector3 pivot = frame.Rig.MultiplyPoint3x4(new Vector3(0f, BasisLocalPlayspaceMover.FlipPivotY, 0f));
                Vector3 tipped = frame.Rig.MultiplyVector(Vector3.up).normalized * (AxisLength * frame.NodeScale);
                _offset.Sphere(pivot, NodeSize * frame.NodeScale, FlipColor);
                _offset.Line(pivot, pivot + tipped, FlipColor, LineWidth);
                _offset.Line(pivot, pivot + (Vector3.up * (AxisLength * frame.NodeScale)), GhostColor, ThinLineWidth);
            }

            _offset.End();
        }

        private static void DrawHands(ref Frame frame)
        {
            _hands.Begin();

            BasisPlayspaceMoverSample sample = BasisLocalPlayspaceMover.GizmoSample;
            Vector3 left = frame.Rig.MultiplyPoint3x4(sample.LeftLocal);
            Vector3 right = frame.Rig.MultiplyPoint3x4(sample.RightLocal);

            if (sample.LeftPresent)
            {
                _hands.Sphere(left, NodeSize * frame.NodeScale, HandColor(sample.LeftHeld, sample.LeftRotate));
            }
            if (sample.RightPresent)
            {
                _hands.Sphere(right, NodeSize * frame.NodeScale, HandColor(sample.RightHeld, sample.RightRotate));
            }

            bool leftEngaged = sample.LeftPresent && (sample.LeftHeld || sample.LeftRotate);
            bool rightEngaged = sample.RightPresent && (sample.RightHeld || sample.RightRotate);
            if (leftEngaged && rightEngaged)
            {
                Vector3 middle = (left + right) * 0.5f;
                _hands.Line(left, right, HandRotateColor, ThinLineWidth);
                _hands.Sphere(middle, SmallNodeSize * frame.NodeScale, HandRotateColor);

                if (sample.SpanBase > DeadZone && sample.SpanCurrent > DeadZone)
                {
                    Vector3 axis = (right - left).normalized * (sample.SpanBase * frame.DeviceScale * 0.5f);
                    _hands.Line(middle - axis, middle + axis, GhostColor, LineWidth);
                }
            }

            _hands.End();
        }

        private static Color HandColor(bool held, bool rotate)
        {
            if (held)
            {
                return HandHeldColor;
            }
            return rotate ? HandRotateColor : HandIdleColor;
        }

        private static void DrawReadouts(ref Frame frame)
        {
            _readouts.Begin();

            BasisPlayspaceMoverSample sample = BasisLocalPlayspaceMover.GizmoSample;
            Vector3 drag = BasisLocalPlayspaceMover.CurrentOffset;
            float dragDistance = new Vector2(drag.x, drag.z).magnitude;
            float flipAngle = BasisLocalPlayspaceMover.HasFlip ? BasisSettingsDefaults.PlayspaceMoverFlipAngle.RawValue : 0f;
            string flipAxis = FlipAxisName();

            int key = ReadoutKey(sample.State, frame.VerticalOffset, dragDistance, frame.DeviceScale, flipAngle, flipAxis);
            if (key != _readoutKey || _readoutText.Length == 0)
            {
                _readoutKey = key;
                _readoutText = BuildReadoutText(sample.State, in frame, dragDistance, flipAngle, flipAxis);
            }

            Vector3 anchor = frame.World(new Vector3(0f, frame.RailHeight + 0.35f, 0f));
            _readouts.Label(anchor, _readoutText, ReadoutColor, frame.Viewer, LabelBaseScale * frame.NodeScale);

            _readouts.End();
        }

        /// <summary>Configured flip axis, falling back to the default when the setting is empty.</summary>
        private static string FlipAxisName()
        {
            string axis = BasisSettingsDefaults.PlayspaceMoverFlipAxis.RawValue;
            return string.IsNullOrEmpty(axis) ? BasisLocalPlayspaceMover.AxisRoll : axis;
        }

        private static int ReadoutKey(BasisPlayspaceMoverState state, float verticalOffset, float dragDistance, float deviceScale, float flipAngle, string flipAxis)
        {
            unchecked
            {
                int key = 17;
                key = (key * 31) + (int)state;
                key = (key * 31) + Mathf.RoundToInt(verticalOffset * 100f);
                key = (key * 31) + Mathf.RoundToInt(dragDistance * 100f);
                key = (key * 31) + Mathf.RoundToInt(deviceScale * 1000f);
                key = (key * 31) + Mathf.RoundToInt(flipAngle);
                key = (key * 31) + flipAxis.GetHashCode();
                key = (key * 31) + Mathf.RoundToInt(BasisPlayspaceBoundary.Metrics.SizeX * 100f);
                key = (key * 31) + Mathf.RoundToInt(BasisPlayspaceBoundary.Metrics.SizeZ * 100f);
                return key;
            }
        }

        private static string BuildReadoutText(BasisPlayspaceMoverState state, in Frame frame, float dragDistance, float flipAngle, string flipAxis)
        {
            _builder.Clear();
            _builder.Append("playspace  ").Append(BasisPlayspaceGizmoCore.StateLabel(state)).Append('\n');

            _builder.Append("vertical  ");
            if (frame.VerticalOffset >= 0f)
            {
                _builder.Append('+');
            }
            _builder.Append(frame.VerticalOffset.ToString("0.00")).Append(" m\n");

            _builder.Append("drag  ").Append(dragDistance.ToString("0.00")).Append(" m\n");
            _builder.Append("device scale  ×").Append(frame.DeviceScale.ToString("0.000"))
                .Append("   eye  ").Append(frame.RailHeight.ToString("0.00")).Append(" m\n");

            if (flipAngle > 0f)
            {
                _builder.Append("flip  ").Append(flipAngle.ToString("0")).Append("° ")
                    .Append(flipAxis.ToLowerInvariant()).Append('\n');
            }

            BasisPlayspaceBoundsMetrics bounds = BasisPlayspaceBoundary.Metrics;
            if (bounds.Valid)
            {
                _builder.Append("play area  ").Append(bounds.SizeX.ToString("0.00")).Append(" × ")
                    .Append(bounds.SizeZ.ToString("0.00")).Append(" m   ")
                    .Append(bounds.Area.ToString("0.0")).Append(" m²");
            }
            else
            {
                _builder.Append("play area  not reported (origin ")
                    .Append(BasisPlayspaceBoundary.OriginMode.ToString().ToLowerInvariant()).Append(')');
            }

            return _builder.ToString();
        }

        private static void BuildTrackingRing(ref Frame frame, float radius, float lift, Vector3[] into)
        {
            for (int Index = 0; Index < into.Length; Index++)
            {
                float angle = Index / (float)into.Length * Mathf.PI * 2f;
                Vector3 point = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                into[Index] = frame.WorldAt(point, lift);
            }
        }

        private static void BuildWorldRing(Vector3 center, float radius, Vector3[] into)
        {
            for (int Index = 0; Index < into.Length; Index++)
            {
                float angle = Index / (float)into.Length * Mathf.PI * 2f;
                into[Index] = new Vector3(center.x + (Mathf.Cos(angle) * radius), center.y, center.z + (Mathf.Sin(angle) * radius));
            }
        }

        private static void ClearDisabled(BasisPlayspaceGizmoLayers layers)
        {
            BasisPlayspaceGizmoLayers dropped = _drawn & ~layers;
            if (dropped == BasisPlayspaceGizmoLayers.None)
            {
                return;
            }
            if ((dropped & BasisPlayspaceGizmoLayers.Boundary) != 0)
            {
                _boundary.Clear();
            }
            if ((dropped & BasisPlayspaceGizmoLayers.Origin) != 0)
            {
                _origin.Clear();
            }
            if ((dropped & BasisPlayspaceGizmoLayers.Offset) != 0)
            {
                _offset.Clear();
            }
            if ((dropped & BasisPlayspaceGizmoLayers.Hands) != 0)
            {
                _hands.Clear();
            }
            if ((dropped & BasisPlayspaceGizmoLayers.Readouts) != 0)
            {
                _readouts.Clear();
                _readoutText = string.Empty;
            }
        }

        /// <summary>Destroys every handle the visualiser holds. Safe to call when it holds none.</summary>
        public static void ClearAll()
        {
            if (_drawn == BasisPlayspaceGizmoLayers.None)
            {
                return;
            }
            _drawn = BasisPlayspaceGizmoLayers.None;
            _boundary.Clear();
            _origin.Clear();
            _offset.Clear();
            _hands.Clear();
            _readouts.Clear();
            _readoutText = string.Empty;
        }

        private static void EnsureMasterHook()
        {
            if (_hooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnMasterGizmoToggle;
            _hooked = true;
        }

        private static void OnMasterGizmoToggle(bool state)
        {
            if (state)
            {
                return;
            }
            _drawn = BasisPlayspaceGizmoLayers.None;
            _boundary.Forget();
            _origin.Forget();
            _offset.Forget();
            _hands.Forget();
            _readouts.Forget();
            _readoutText = string.Empty;
        }

        /// <summary>
        /// Everything a layer needs to place a tracking-space point in the world, resolved once per
        /// frame so every layer draws against the same pose.
        /// </summary>
        private struct Frame
        {
            public Matrix4x4 Rig;
            public float Lift;
            public float VerticalOffset;
            public float DeviceScale;
            public Vector3 OffsetPosition;
            public Quaternion OffsetRotation;
            public Vector3 Viewer;
            public float NodeScale;
            public float RailHeight;

            public Vector3 World(Vector3 trackingPoint)
            {
                return WorldAt(trackingPoint, Lift);
            }

            public Vector3 WorldAt(Vector3 trackingPoint, float lift)
            {
                Vector3 local = BasisPlayspaceGizmoCore.TrackingToPlayerLocal(trackingPoint, lift, DeviceScale, OffsetPosition, OffsetRotation);
                return Rig.MultiplyPoint3x4(local);
            }
        }
    }
}
