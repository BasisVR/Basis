using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Plain class (not MonoBehaviour) that detects direct finger touch on
    /// world-space UI canvases in VR.  Owned by <see cref="BasisPlayerInteract"/>
    /// which calls <see cref="Poll"/> each frame after IK.
    ///
    /// Touch flow per hand:
    ///   None ─▶ Hovering ─▶ Pressing ─▶ release ─▶ Hovering / None
    /// </summary>
    public class BasisDirectTouch
    {
        // ── Geometry ────────────────────────────────────────────────────
        // User-tunable via BasisSettingsDefaults (VR Finger Touch settings);
        // refreshed from the bindings once per Poll. Values here are the
        // compiled-in fallbacks used before the first refresh.
        [Tooltip("Fallback offset when avatar has no finger bones")]
        public static float FingerLength = 0.1f;
        [Tooltip("Extra offset past the distal bone to approximate the actual fingertip")]
        public static float DistalTipOffset = 0.015f;
        public static float FingerRadius = 0.00375f;

        // ── Thresholds ─────────────────────────────────────────────────
        public static float HoverDistance = 0.04f;
        public static float PressDepth = 0.01f;
        public static float ReleaseDistance = 0.025f;
        [Tooltip("Touch is disabled while the touch finger's curl is below this (extended ≈ 0.75, fist ≈ -1)")]
        public static float FistCurlThreshold = 0f;

        // ── Scroll ─────────────────────────────────────────────────────
        public static float ScrollSensitivity = 800f;

        // ── Haptics ────────────────────────────────────────────────────
        public static float HoverHapticDuration  = 0.02f;
        public static float HoverHapticAmplitude = 0.15f;
        public static float HoverHapticFrequency = 0.2f;

        public static float PressHapticDuration  = 0.08f;
        public static float PressHapticAmplitude = 0.7f;
        public static float PressHapticFrequency = 0.4f;

        public static float ClickHapticDuration  = 0.04f;
        public static float ClickHapticAmplitude = 1.0f;
        public static float ClickHapticFrequency = 0.6f;

        // ── Singleton ──────────────────────────────────────────────────
        public static BasisDirectTouch Instance;

        // ── Internals ──────────────────────────────────────────────────
        private const int k_MaxHovered = 32;
        private static readonly Collider[] _hitBuffer = new Collider[16];
        private static LayerMask _uiMask;

        // Fixed two slots: [0] = left, [1] = right
        private readonly FingerTouchState[] _hand = new FingerTouchState[2];
        private BasisInput _leftInput;
        private BasisInput _rightInput;
        private bool _leftHandBusy;
        private bool _rightHandBusy;

        private enum TouchPhase : byte { None, Hovering, Pressing }

        private class FingerTouchState
        {
            public TouchPhase Phase;
            public BasisPointerEventData EventData;
            public GameObject Target;
            public Canvas Canvas;
            public BasisUIToolkitPanel Panel;
            public BasisUIToolkitPointer ToolkitPointer;
            public Plane Plane;
            public Vector3 PrevSurface;
            public float SignedDist;
            public readonly GameObject[] Hovered = new GameObject[k_MaxHovered];
            public int HoveredCount;

            public void ClearHovered()
            {
                for (int i = 0; i < HoveredCount; i++) Hovered[i] = null;
                HoveredCount = 0;
            }

            public void Reset()
            {
                Phase = TouchPhase.None;
                Target = null;
                Canvas = null;
                Panel = null;
                ClearHovered();
            }
        }

        // ================================================================
        // Lifecycle  (called by BasisPlayerInteract)
        // ================================================================

        public BasisDirectTouch()
        {
            Instance = this;
            _uiMask = LayerMask.GetMask("UI", "OverlayUI");
            _hand[0] = new FingerTouchState();
            _hand[1] = new FingerTouchState();
        }

        public void Shutdown()
        {
            for (int i = 0; i < 2; i++)
                if (_hand[i].Phase != TouchPhase.None)
                    CleanupState(_hand[i]);

            _leftInput = null;
            _rightInput = null;

            if (Instance == this) Instance = null;
        }

        // ================================================================
        // Public query  (reference comparison, no strings)
        // ================================================================

        public bool IsDeviceTouching(BasisInput input)
        {
            if (input == _leftInput  && _hand[0].Phase != TouchPhase.None) return true;
            if (input == _rightInput && _hand[1].Phase != TouchPhase.None) return true;
            return false;
        }

        // ================================================================
        // Per-frame poll
        // ================================================================

        public void Poll(BasisInteractInput[] interactInputs)
        {
            if (BasisDeviceManagement.IsUserInDesktop()) { ClearAll(); TickGizmos(); return; }
            if (BasisSettingsDefaults.DisableVRFingerTouch.RawValue) { ClearAll(); TickGizmos(); return; }
            if (interactInputs == null) return;
            if (EventSystem.current == null) return;

            RefreshTuning();
            ResolveHands(interactInputs);

            if (_leftInput != null)
            {
                if (!_leftHandBusy && IsFingerExtended(true)) ProcessTouch(_hand[0], _leftInput);
                else if (_hand[0].Phase != TouchPhase.None) EndTouch(_hand[0], _leftInput);
            }
            if (_rightInput != null)
            {
                if (!_rightHandBusy && IsFingerExtended(false)) ProcessTouch(_hand[1], _rightInput);
                else if (_hand[1].Phase != TouchPhase.None) EndTouch(_hand[1], _rightInput);
            }

            TickGizmos();
        }

        /// <summary>
        /// Pulls the user-tunable values from settings once per frame.
        /// Threshold ordering is enforced here (press &lt; release &lt; hover)
        /// so bad slider combinations can't wedge the state machine into a
        /// press/release oscillation loop.
        /// </summary>
        private static void RefreshTuning()
        {
            float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            FingerLength = BasisSettingsDefaults.FingerTouchFingerLength.RawValue * scale;
            DistalTipOffset = BasisSettingsDefaults.FingerTouchTipOffset.RawValue * scale;
            FingerRadius = BasisSettingsDefaults.FingerTouchRadius.RawValue * scale;
            ScrollSensitivity = BasisSettingsDefaults.FingerTouchScrollSensitivity.RawValue;

            PressDepth = BasisSettingsDefaults.FingerTouchPressDepth.RawValue * scale;
            ReleaseDistance = Mathf.Max(BasisSettingsDefaults.FingerTouchReleaseDistance.RawValue * scale, PressDepth + 0.005f * scale);
            HoverDistance = Mathf.Max(BasisSettingsDefaults.FingerTouchHoverDistance.RawValue * scale, ReleaseDistance + 0.005f * scale);
        }

        private void ResolveHands(BasisInteractInput[] inputs)
        {
            BasisInput newL = null, newR = null;
            string hands = BasisSettingsDefaults.FingerTouchHands.RawValue;
            bool allowLeft = hands != BasisSettingsDefaults.FingerTouchHands_Right;
            bool allowRight = hands != BasisSettingsDefaults.FingerTouchHands_Left;
            int len = inputs.Length;

            bool busyL = false, busyR = false;
            for (int i = 0; i < len; i++)
            {
                BasisInput inp = inputs[i].input;
                if (inp == null || !inp.HasControl) continue;
                if (!inp.TryGetRole(out BasisBoneTrackedRole role)) continue;
                if (allowLeft && role == BasisBoneTrackedRole.LeftHand)
                {
                    newL = inp;
                    busyL = IsHandBusy(inputs[i], inp);
                }
                else if (allowRight && role == BasisBoneTrackedRole.RightHand)
                {
                    newR = inp;
                    busyR = IsHandBusy(inputs[i], inp);
                }
            }
            _leftHandBusy = busyL;
            _rightHandBusy = busyR;

            if (newL != _leftInput)
            {
                if (_hand[0].Phase != TouchPhase.None) CleanupState(_hand[0]);
                _leftInput = newL;
            }
            if (newR != _rightInput)
            {
                if (_hand[1].Phase != TouchPhase.None) CleanupState(_hand[1]);
                _rightInput = newR;
            }
        }

        /// <summary>
        /// True while the hand is holding an interactable, or its fingertip
        /// is within touch range of the interactable it is hovering — either
        /// means grab intent, so direct touch is suppressed for that hand.
        /// </summary>
        private static bool IsHandBusy(BasisInteractInput interactInput, BasisInput input)
        {
            BasisInteractableObject target = interactInput.lastTarget;
            if (target == null) return false;
            if (target.IsInteractingWith(input)) return true;
            if (!target.IsHoveredBy(input)) return false;
            Vector3 tip = GetFingertip(input);
            return Vector3.Distance(target.GetClosestPoint(tip), tip) <= FingerRadius + HoverDistance;
        }

        private void ClearAll()
        {
            for (int i = 0; i < 2; i++)
                if (_hand[i].Phase != TouchPhase.None)
                    CleanupState(_hand[i]);
            _leftInput = null;
            _rightInput = null;
        }

        // ================================================================
        // Fingertip position
        // ================================================================

        /// <summary>
        /// Returns the world-space fingertip position for a hand input.
        /// Prefers the distal bone of the finger chosen in settings (+ small
        /// tip offset) when available; falls back to hand position + forward
        /// * FingerLength.
        /// </summary>
        private static Vector3 GetFingertip(BasisInput input)
        {
            BasisTransformMapping map = BasisLocalAvatarDriver.Mapping;
            if (map != null && input.TryGetRole(out BasisBoneTrackedRole role)
                && (role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand))
            {
                bool isLeft = role == BasisBoneTrackedRole.LeftHand;
                if (TryGetTouchFingerBones(map, isLeft, out Transform distal, out Transform intermediate))
                {
                    // The distal bone is the last joint; the actual tip
                    // extends a little further along the finger.
                    Vector3 tip = distal.position;
                    Vector3 along = intermediate != null ? tip - intermediate.position : distal.forward;
                    if (along.sqrMagnitude > 1e-10f)
                        tip += along.normalized * DistalTipOffset;
                    return tip;
                }
            }

            // Fallback: offset from hand along the pointing direction
            return input.RaycastCoord.position
                 + input.RaycastCoord.rotation * (Vector3.forward * FingerLength);
        }

        /// <summary>
        /// Resolves the distal and intermediate bones of the finger selected
        /// in settings, degrading to the index finger when the avatar is
        /// missing the chosen finger's bones.
        /// </summary>
        private static bool TryGetTouchFingerBones(BasisTransformMapping map, bool isLeft, out Transform distal, out Transform intermediate)
        {
            bool has;
            switch (BasisSettingsDefaults.FingerTouchFinger.RawValue)
            {
                case BasisSettingsDefaults.FingerTouchFinger_Thumb:
                    has = isLeft ? map.HasLeftThumb[2] : map.HasRightThumb[2];
                    distal = isLeft ? map.LeftThumb[2] : map.RightThumb[2];
                    intermediate = isLeft ? map.LeftThumb[1] : map.RightThumb[1];
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Middle:
                    has = isLeft ? map.HasLeftMiddle[2] : map.HasRightMiddle[2];
                    distal = isLeft ? map.LeftMiddle[2] : map.RightMiddle[2];
                    intermediate = isLeft ? map.LeftMiddle[1] : map.RightMiddle[1];
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Ring:
                    has = isLeft ? map.HasLeftRing[2] : map.HasRightRing[2];
                    distal = isLeft ? map.LeftRing[2] : map.RightRing[2];
                    intermediate = isLeft ? map.LeftRing[1] : map.RightRing[1];
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Little:
                    has = isLeft ? map.HasLeftLittle[2] : map.HasRightLittle[2];
                    distal = isLeft ? map.LeftLittle[2] : map.RightLittle[2];
                    intermediate = isLeft ? map.LeftLittle[1] : map.RightLittle[1];
                    break;
                default:
                    has = false;
                    distal = null;
                    intermediate = null;
                    break;
            }

            if (!has || distal == null)
            {
                has = isLeft ? map.HasLeftIndex[2] : map.HasRightIndex[2];
                distal = isLeft ? map.LeftIndex[2] : map.RightIndex[2];
                intermediate = isLeft ? map.LeftIndex[1] : map.RightIndex[1];
            }
            return has && distal != null;
        }

        /// <summary>
        /// True when the touch finger is extended enough to signal press
        /// intent; curled toward a fist disables direct touch entirely.
        /// </summary>
        private static bool IsFingerExtended(bool isLeft)
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (player == null || player.LocalHandDriver == null) return true;
            BasisFingerPose pose = isLeft ? player.LocalHandDriver.LeftHand : player.LocalHandDriver.RightHand;
            if (pose == null) return true;

            float curl;
            switch (BasisSettingsDefaults.FingerTouchFinger.RawValue)
            {
                case BasisSettingsDefaults.FingerTouchFinger_Thumb:
                    curl = pose.ThumbPercentage.x;
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Middle:
                    curl = pose.MiddlePercentage.x;
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Ring:
                    curl = pose.RingPercentage.x;
                    break;
                case BasisSettingsDefaults.FingerTouchFinger_Little:
                    curl = pose.LittlePercentage.x;
                    break;
                default:
                    curl = pose.IndexPercentage.x;
                    break;
            }
            return curl >= FistCurlThreshold;
        }

        // ================================================================
        // Core touch logic
        // ================================================================

        private void ProcessTouch(FingerTouchState st, BasisInput input)
        {
            Vector3 tip = GetFingertip(input);

            int hits = Physics.OverlapSphereNonAlloc(
                tip, FingerRadius + HoverDistance, _hitBuffer, _uiMask);

            if (hits == 0) { if (st.Phase != TouchPhase.None) EndTouch(st, input); return; }

            // Closest canvas, and closest UI Toolkit panel. Panels are checked first because a
            // panel parented under a canvas would otherwise resolve as that canvas.
            Canvas best = null;
            float bestD = float.MaxValue;
            BasisUIToolkitPanel bestPanel = null;
            float bestPanelD = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                Collider col = _hitBuffer[i];
                if (col == null) continue;
                float d = Vector3.Distance(tip, col.ClosestPoint(tip));

                BasisUIToolkitPanel p = col.GetComponentInParent<BasisUIToolkitPanel>();
                if (p != null && p.isActiveAndEnabled)
                {
                    if (d < bestPanelD) { bestPanelD = d; bestPanel = p; }
                    continue;
                }

                Canvas c = col.GetComponent<Canvas>();
                if (c == null) c = col.GetComponentInParent<Canvas>();
                if (c == null) continue;
                if (d < bestD) { bestD = d; best = c; }
            }

            if (bestPanel != null && (best == null || bestPanelD <= bestD))
            {
                ProcessTouchPanel(st, input, bestPanel, tip);
                return;
            }

            if (best == null) { if (st.Phase != TouchPhase.None) EndTouch(st, input); return; }

            // Leaving a panel for a canvas: close out the panel touch so the phase machine below
            // restarts from None instead of inheriting panel state.
            if (st.Panel != null) EndTouch(st, input);

            RectTransform rt = best.GetComponent<RectTransform>();
            Vector3 fwd = CanvasFrontNormal(best, rt);
            Plane plane = new Plane(fwd, rt.position);
            float sd = plane.GetDistanceToPoint(tip);
            st.SignedDist = sd;
            st.Plane = plane;

            // The finger only counts as pressing from the canvas' front when
            // the hand itself is on the front (+forward) side — a fingertip
            // that pierced in from the back must not hover or press.
            Vector3 handPos = input.HasControl && input.Control != null
                ? input.Control.OutgoingWorldData.position
                : input.RaycastCoord.position;
            if (plane.GetDistanceToPoint(handPos) <= 0f)
            {
                if (st.Phase != TouchPhase.None) EndTouch(st, input);
                return;
            }

            Vector3 proj = tip - fwd * sd;

            Camera cam = best.worldCamera;
            if (cam == null && BasisLocalCameraDriver.HasInstance)
                cam = BasisLocalCameraDriver.Instance.Camera;
            if (cam == null) return;

            GameObject graphic = FindGraphic(best, proj, cam);

            switch (st.Phase)
            {
                case TouchPhase.None:
                    if (sd > 0 && sd < HoverDistance && graphic != null)
                        BeginHover(st, graphic, best, input, proj, cam);
                    break;

                case TouchPhase.Hovering:
                    if (graphic == null || sd > HoverDistance || sd < -HoverDistance)
                        EndTouch(st, input);
                    else if (sd <= PressDepth)
                        BeginPress(st, graphic, input, proj, cam);
                    else
                        UpdateHover(st, graphic, proj, cam);
                    break;

                case TouchPhase.Pressing:
                    if (sd > ReleaseDistance || sd < -HoverDistance)
                    {
                        EndPress(st, input);
                        if (sd > 0 && sd < HoverDistance && graphic != null)
                            st.Phase = TouchPhase.Hovering;
                        else
                            EndTouch(st, input);
                    }
                    else
                        UpdatePress(st, proj, cam);
                    break;
            }
        }

        /// <summary>
        /// Fingertip poke against a UI Toolkit panel. Mirrors the canvas phase machine above, but
        /// drives a <see cref="BasisUIToolkitPointer"/> instead of uGUI event data. The panel's
        /// front is fixed by its component, so no majority vote is needed.
        /// </summary>
        private void ProcessTouchPanel(FingerTouchState st, BasisInput input, BasisUIToolkitPanel panel, Vector3 tip)
        {
            // Leaving a canvas for a panel: close out the canvas touch first.
            if (st.Panel == null && st.Phase != TouchPhase.None) EndTouch(st, input);

            Vector3 fwd = panel.FrontNormal;
            Plane plane = new Plane(fwd, panel.transform.position);
            float sd = plane.GetDistanceToPoint(tip);
            st.SignedDist = sd;
            st.Plane = plane;

            Vector3 handPos = input.HasControl && input.Control != null
                ? input.Control.OutgoingWorldData.position
                : input.RaycastCoord.position;
            if (plane.GetDistanceToPoint(handPos) <= 0f)
            {
                if (st.Phase != TouchPhase.None) EndTouch(st, input);
                return;
            }

            Vector3 proj = tip - fwd * sd;
            if (!panel.TryGetPanelPositionFromPoint(proj, true, out Vector2 panelPoint))
            {
                if (st.Phase != TouchPhase.None) EndTouch(st, input);
                return;
            }

            EnsureToolkitPointer(st);
            st.Panel = panel;
            st.PrevSurface = proj;

            switch (st.Phase)
            {
                case TouchPhase.None:
                    if (sd > 0 && sd < HoverDistance)
                    {
                        st.Phase = TouchPhase.Hovering;
                        DriveToolkitPointer(st, panel, panelPoint, false);
                        PlayTouchHaptic(input, HoverHapticDuration, HoverHapticAmplitude, HoverHapticFrequency);
                    }
                    break;

                case TouchPhase.Hovering:
                    if (sd > HoverDistance || sd < -HoverDistance)
                    {
                        EndTouch(st, input);
                    }
                    else if (sd <= PressDepth)
                    {
                        st.Phase = TouchPhase.Pressing;
                        DriveToolkitPointer(st, panel, panelPoint, true);
                        PlayTouchHaptic(input, PressHapticDuration, PressHapticAmplitude, PressHapticFrequency);
                    }
                    else
                    {
                        DriveToolkitPointer(st, panel, panelPoint, false);
                    }
                    break;

                case TouchPhase.Pressing:
                    if (sd > ReleaseDistance || sd < -HoverDistance)
                    {
                        DriveToolkitPointer(st, panel, panelPoint, false);
                        PlayTouchHaptic(input, ClickHapticDuration, ClickHapticAmplitude, ClickHapticFrequency);
                        if (sd > 0 && sd < HoverDistance) st.Phase = TouchPhase.Hovering;
                        else EndTouch(st, input);
                    }
                    else
                    {
                        DriveToolkitPointer(st, panel, panelPoint, true);
                    }
                    break;
            }
        }

        private static void EnsureToolkitPointer(FingerTouchState st)
        {
            if (st.ToolkitPointer == null)
                st.ToolkitPointer = new BasisUIToolkitPointer();
        }

        private static void DriveToolkitPointer(FingerTouchState st, BasisUIToolkitPanel panel, Vector2 panelPoint, bool pressed)
        {
            st.ToolkitPointer.BeginFrame(pressed);
            st.ToolkitPointer.Process(panel, panelPoint, pressed, Vector2.zero);
        }

        /// <summary>
        /// World-space normal of the canvas' readable/pressable face,
        /// pointing toward where a viewer would stand. UI meshes read
        /// correctly from the side their graphics' forward points away
        /// from (Unity's ignoreReversedGraphics rule), and canvases in the
        /// wild are authored facing either way relative to their root. A
        /// majority vote over the first few graphics decides, snapped to
        /// ±root forward so a tilted decoration can't skew the plane.
        /// </summary>
        private static Vector3 CanvasFrontNormal(Canvas canvas, RectTransform rt)
        {
            var graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
            int count = Mathf.Min(graphics.Count, 8);
            if (count == 0) return rt.forward;

            Vector3 rootFwd = rt.forward;
            int aligned = 0;
            for (int i = 0; i < count; i++)
                if (Vector3.Dot(graphics[i].transform.forward, rootFwd) >= 0f) aligned++;

            return aligned * 2 >= count ? -rootFwd : rootFwd;
        }

        // ================================================================
        // Graphic hit-test
        // ================================================================

        private static GameObject FindGraphic(Canvas canvas, Vector3 worldPt, Camera cam)
        {
            var graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
            int count = graphics.Count;
            GameObject hit = null;
            int deepest = -1;
            Vector2 sp = (Vector2)cam.WorldToScreenPoint(worldPt);

            for (int i = 0; i < count; i++)
            {
                Graphic g = graphics[i];
                if (g.depth == -1 || !g.raycastTarget || g.canvasRenderer.cull) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, sp, cam)
                    && g.Raycast(sp, cam)
                    && g.depth > deepest)
                {
                    deepest = g.depth;
                    hit = g.gameObject;
                }
            }
            return hit;
        }

        // ================================================================
        // Phase transitions
        // ================================================================

        private void BeginHover(FingerTouchState st, GameObject target, Canvas canvas,
                                BasisInput input, Vector3 surf, Camera cam)
        {
            st.Phase = TouchPhase.Hovering;
            st.Target = target;
            st.Canvas = canvas;
            st.PrevSurface = surf;
            EnsureEventData(st);
            WriteEventData(st, surf, cam);
            EmitEnter(st, target);
            PlayTouchHaptic(input, HoverHapticDuration, HoverHapticAmplitude, HoverHapticFrequency);
        }

        private void UpdateHover(FingerTouchState st, GameObject target,
                                 Vector3 surf, Camera cam)
        {
            WriteEventData(st, surf, cam);
            if (target != st.Target)
            {
                EmitExit(st);
                st.Target = target;
                if (target != null) EmitEnter(st, target);
            }
            st.PrevSurface = surf;
        }

        private void BeginPress(FingerTouchState st, GameObject target,
                                BasisInput input, Vector3 surf, Camera cam)
        {
            st.Phase = TouchPhase.Pressing;
            if (target != st.Target)
            {
                EmitExit(st);
                st.Target = target;
                EmitEnter(st, target);
            }
            WriteEventData(st, surf, cam);

            var ed = st.EventData;
            ed.pressPosition = ed.position;
            ed.pointerPressRaycast = ed.pointerCurrentRaycast;
            ed.eligibleForClick = true;
            ed.dragging = false;
            ed.useDragThreshold = true;
            ed.button = PointerEventData.InputButton.Left;

            GameObject sel = ExecuteEvents.GetEventHandler<ISelectHandler>(target);
            if (sel != EventSystem.current.currentSelectedGameObject)
                EventSystem.current.SetSelectedGameObject(sel, ed);

            GameObject pressed = ExecuteEvents.ExecuteHierarchy(target, ed, ExecuteEvents.pointerDownHandler);
            if (pressed == null)
                pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            ed.pointerPress = pressed;
            ed.rawPointerPress = target;

            GameObject drag = ExecuteEvents.GetEventHandler<IDragHandler>(target);
            ed.pointerDrag = drag;
            if (drag != null)
                ExecuteEvents.Execute(drag, ed, ExecuteEvents.initializePotentialDrag);

            st.PrevSurface = surf;
            PlayTouchHaptic(input, PressHapticDuration, PressHapticAmplitude, PressHapticFrequency);
        }

        private void UpdatePress(FingerTouchState st, Vector3 surf, Camera cam)
        {
            Vector3 prev = st.PrevSurface;
            WriteEventData(st, surf, cam);
            var ed = st.EventData;

            if (ed.pointerDrag != null)
            {
                if (!ed.dragging)
                {
                    float thr = EventSystem.current.pixelDragThreshold * 3f;
                    if ((ed.pressPosition - ed.position).sqrMagnitude >= thr * thr)
                    {
                        if (ed.pointerPress != null && ed.pointerPress != ed.pointerDrag)
                        {
                            ExecuteEvents.Execute(ed.pointerPress, ed, ExecuteEvents.pointerUpHandler);
                            ed.eligibleForClick = false;
                            ed.pointerPress = null;
                            ed.rawPointerPress = null;
                        }
                        ExecuteEvents.Execute(ed.pointerDrag, ed, ExecuteEvents.beginDragHandler);
                        ed.dragging = true;
                    }
                }
                if (ed.dragging)
                    ExecuteEvents.Execute(ed.pointerDrag, ed, ExecuteEvents.dragHandler);
            }
            else if (st.Canvas != null)
            {
                Vector3 delta = surf - prev;
                if (delta.sqrMagnitude > 1e-8f)
                {
                    Vector3 local = st.Canvas.transform.InverseTransformVector(delta);
                    ed.scrollDelta = new Vector2(-local.x, -local.y) * ScrollSensitivity;
                    if (st.Target != null)
                    {
                        GameObject h = ExecuteEvents.GetEventHandler<IScrollHandler>(st.Target);
                        if (h != null)
                            ExecuteEvents.ExecuteHierarchy(h, ed, ExecuteEvents.scrollHandler);
                    }
                }
            }
            st.PrevSurface = surf;
        }

        private void EndPress(FingerTouchState st, BasisInput input)
        {
            var ed = st.EventData;
            if (ed.pointerPress != null)
                ExecuteEvents.Execute(ed.pointerPress, ed, ExecuteEvents.pointerUpHandler);

            if (ed.eligibleForClick && ed.pointerPress != null && st.Target != null)
            {
                GameObject ch = ExecuteEvents.GetEventHandler<IPointerClickHandler>(st.Target);
                if (ch == ed.pointerPress)
                {
                    ExecuteEvents.Execute(ed.pointerPress, ed, ExecuteEvents.pointerClickHandler);
                    PlayTouchHaptic(input, ClickHapticDuration, ClickHapticAmplitude, ClickHapticFrequency);
                }
            }

            if (ed.dragging && ed.pointerDrag != null)
                ExecuteEvents.Execute(ed.pointerDrag, ed, ExecuteEvents.endDragHandler);

            ed.eligibleForClick = false;
            ed.pointerPress = null;
            ed.rawPointerPress = null;
            ed.dragging = false;
            ed.pointerDrag = null;
        }

        private static void PlayTouchHaptic(BasisInput input, float duration, float amplitude, float frequency)
        {
            if (!BasisSettingsDefaults.FingerTouchHaptics.RawValue) return;
            input.PlayHaptic(duration, amplitude, frequency);
        }

        private void EndTouch(FingerTouchState st, BasisInput input)
        {
            if (st.Panel != null)
            {
                // Release dispatches the PointerUp that fires the click, and clearing the frame
                // edge lets a re-approach press again instead of reading as an already-held poke.
                if (st.Phase == TouchPhase.Pressing)
                    PlayTouchHaptic(input, ClickHapticDuration, ClickHapticAmplitude, ClickHapticFrequency);

                ReleaseToolkitPointer(st);
                st.Reset();
                return;
            }

            if (st.Phase == TouchPhase.Pressing) EndPress(st, input);
            EmitExit(st);
            st.Reset();
        }

        private static void ReleaseToolkitPointer(FingerTouchState st)
        {
            if (st.ToolkitPointer == null) return;
            st.ToolkitPointer.BeginFrame(false);
            st.ToolkitPointer.Release();
        }

        private static void CleanupState(FingerTouchState st)
        {
            ReleaseToolkitPointer(st);
            if (st.EventData != null)
                for (int i = 0; i < st.HoveredCount; i++)
                    if (st.Hovered[i] != null)
                        ExecuteEvents.Execute(st.Hovered[i], st.EventData, ExecuteEvents.pointerExitHandler);
            st.Reset();
        }

        // ================================================================
        // Event-data helpers
        // ================================================================

        private static void EnsureEventData(FingerTouchState st)
        {
            if (st.EventData == null)
                st.EventData = new BasisPointerEventData(EventSystem.current);
        }

        private static void WriteEventData(FingerTouchState st, Vector3 surfPt, Camera cam)
        {
            Vector2 sp = (Vector2)cam.WorldToScreenPoint(surfPt);
            var ed = st.EventData;
            ed.delta = sp - ed.position;
            ed.position = sp;
            if (st.Target != null)
            {
                ed.pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = st.Target,
                    distance = Mathf.Abs(st.SignedDist),
                    worldPosition = surfPt,
                    worldNormal = st.Plane.normal,
                    screenPosition = sp
                };
            }
        }

        private static void EmitEnter(FingerTouchState st, GameObject target)
        {
            if (target == null) return;
            Transform t = target.transform;
            while (t != null && st.HoveredCount < k_MaxHovered)
            {
                ExecuteEvents.Execute(t.gameObject, st.EventData, ExecuteEvents.pointerEnterHandler);
                st.Hovered[st.HoveredCount++] = t.gameObject;
                t = t.parent;
            }
            st.EventData.pointerEnter = target;
        }

        private static void EmitExit(FingerTouchState st)
        {
            for (int i = 0; i < st.HoveredCount; i++)
                if (st.Hovered[i] != null)
                    ExecuteEvents.Execute(st.Hovered[i], st.EventData, ExecuteEvents.pointerExitHandler);
            st.ClearHovered();
            if (st.EventData != null) st.EventData.pointerEnter = null;
        }

        // ================================================================
        // Gizmos  (ticked by SMModuleDebugOptions)
        // ================================================================

        // Fingertip touch-sphere visualization for tuning the finger-touch
        // settings: one sphere per hand at the live touch point, sized to the
        // configured touch radius and tinted by phase.
        private static readonly Color GizmoIdleColor = Color.gray;
        private static readonly Color GizmoHoverColor = Color.yellow;
        private static readonly Color GizmoPressColor = Color.green;
        private static readonly Color GizmoDisabledColor = Color.red;

        private struct FingerGizmo
        {
            public int Sphere;
            public TouchPhase Phase;
            public bool Suppressed;
        }

        private static readonly FingerGizmo[] _fingerGizmos = new FingerGizmo[2];
        private static bool _gizmoHooked;

        private static bool _gizmosEnabled;

        public static void UpdateGizmos(bool show)
        {
            EnsureGizmoHook();
            _gizmosEnabled = show;
            if (!show || Instance == null)
            {
                GizmoShutdown();
            }
        }

        // Positions are written from Poll (after IK) so the spheres don't
        // trail the hands by a frame while the player moves.
        private void TickGizmos()
        {
            if (!_gizmosEnabled) return;
            UpdateHandGizmo(0, _leftInput, _hand[0].Phase, _leftHandBusy);
            UpdateHandGizmo(1, _rightInput, _hand[1].Phase, _rightHandBusy);
        }

        private static void UpdateHandGizmo(int slot, BasisInput input, TouchPhase phase, bool busy)
        {
            FingerGizmo g = _fingerGizmos[slot];

            if (input == null)
            {
                if (g.Sphere > 0)
                {
                    BasisGizmoManager.DestroyGizmo(g.Sphere);
                    _fingerGizmos[slot] = default;
                }
                return;
            }

            bool suppressed = busy || !IsFingerExtended(slot == 0);

            // Sphere color is fixed at creation — recreate when the phase or suppression changes.
            if (g.Sphere > 0 && (g.Phase != phase || g.Suppressed != suppressed))
            {
                BasisGizmoManager.DestroyGizmo(g.Sphere);
                g.Sphere = 0;
            }

            Vector3 tip = GetFingertip(input);
            // Unit-sphere mesh: scale equals diameter.
            float diameter = FingerRadius * 2f;

            if (g.Sphere <= 0)
            {
                Color color = suppressed ? GizmoDisabledColor
                            : phase == TouchPhase.Pressing ? GizmoPressColor
                            : phase == TouchPhase.Hovering ? GizmoHoverColor
                            : GizmoIdleColor;
                if (!BasisGizmoManager.CreateSphereGizmo(slot == 0 ? "FingerTouchLeft" : "FingerTouchRight", out g.Sphere, tip, diameter, color))
                {
                    return;
                }
                g.Phase = phase;
                g.Suppressed = suppressed;
            }
            else
            {
                BasisGizmoManager.UpdateSphereGizmo(g.Sphere, tip, Vector3.one * diameter);
            }

            _fingerGizmos[slot] = g;
        }

        public static void GizmoShutdown()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_fingerGizmos[i].Sphere > 0)
                {
                    BasisGizmoManager.DestroyGizmo(_fingerGizmos[i].Sphere);
                }
                _fingerGizmos[i] = default;
            }
        }

        private static void EnsureGizmoHook()
        {
            if (_gizmoHooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnGizmoMasterToggleChanged;
            _gizmoHooked = true;
        }

        // Master toggle going off destroys BasisGizmoManager's dictionaries, so our
        // cached IDs are stale — forget them so the next tick re-creates cleanly.
        private static void OnGizmoMasterToggleChanged(bool state)
        {
            if (!state)
            {
                for (int i = 0; i < 2; i++)
                {
                    _fingerGizmos[i] = default;
                }
            }
        }
    }
}
