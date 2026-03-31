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
        [Tooltip("Fallback offset when avatar has no index finger bones")]
        public static float FingerLength = 0.1f;
        [Tooltip("Extra offset past the distal bone to approximate the actual fingertip")]
        public static float DistalTipOffset = 0.015f;
        public static float FingerRadius = 0.015f;

        // ── Thresholds ─────────────────────────────────────────────────
        public static float HoverDistance = 0.04f;
        public static float PressDepth = 0.01f;
        public static float ReleaseDistance = 0.025f;

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

        private enum TouchPhase : byte { None, Hovering, Pressing }

        private class FingerTouchState
        {
            public TouchPhase Phase;
            public BasisPointerEventData EventData;
            public GameObject Target;
            public Canvas Canvas;
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
            if (BasisDeviceManagement.IsUserInDesktop()) { ClearAll(); return; }
            if (interactInputs == null) return;
            if (EventSystem.current == null) return;

            ResolveHands(interactInputs);

            if (_leftInput  != null) ProcessTouch(_hand[0], _leftInput);
            if (_rightInput != null) ProcessTouch(_hand[1], _rightInput);
        }

        private void ResolveHands(BasisInteractInput[] inputs)
        {
            BasisInput newL = null, newR = null;
            int len = inputs.Length;

            for (int i = 0; i < len; i++)
            {
                BasisInput inp = inputs[i].input;
                if (inp == null || !inp.HasControl) continue;
                if (!inp.TryGetRole(out BasisBoneTrackedRole role)) continue;
                if (role == BasisBoneTrackedRole.LeftHand)       newL = inp;
                else if (role == BasisBoneTrackedRole.RightHand) newR = inp;
            }

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
        /// Prefers the avatar's index-finger distal bone (+ small tip offset)
        /// when available; falls back to hand position + forward * FingerLength.
        /// </summary>
        private static Vector3 GetFingertip(BasisInput input)
        {
            BasisTransformMapping map = BasisLocalAvatarDriver.Mapping;
            if (map != null && input.TryGetRole(out BasisBoneTrackedRole role))
            {
                Transform distal = null;
                bool has = false;

                if (role == BasisBoneTrackedRole.LeftHand)
                {
                    has = map.HasLeftIndex[2];
                    distal = map.LeftIndex[2];
                }
                else if (role == BasisBoneTrackedRole.RightHand)
                {
                    has = map.HasRightIndex[2];
                    distal = map.RightIndex[2];
                }

                if (has && distal != null)
                {
                    // The distal bone is the last joint; the actual tip
                    // extends a little further along the bone's forward.
                    return distal.position + distal.forward * DistalTipOffset;
                }
            }

            // Fallback: offset from hand along the pointing direction
            return input.RaycastCoord.position
                 + input.RaycastCoord.rotation * (Vector3.forward * FingerLength);
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

            // Closest canvas
            Canvas best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                Collider col = _hitBuffer[i];
                if (col == null) continue;
                Canvas c = col.GetComponent<Canvas>();
                if (c == null) c = col.GetComponentInParent<Canvas>();
                if (c == null) continue;
                float d = Vector3.Distance(tip, col.ClosestPoint(tip));
                if (d < bestD) { bestD = d; best = c; }
            }

            if (best == null) { if (st.Phase != TouchPhase.None) EndTouch(st, input); return; }

            RectTransform rt = best.GetComponent<RectTransform>();
            Vector3 fwd = rt.forward;
            Plane plane = new Plane(fwd, rt.position);
            float sd = plane.GetDistanceToPoint(tip);
            st.SignedDist = sd;
            st.Plane = plane;

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
            input.PlayHaptic(HoverHapticDuration, HoverHapticAmplitude, HoverHapticFrequency);
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
            input.PlayHaptic(PressHapticDuration, PressHapticAmplitude, PressHapticFrequency);
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
                    input.PlayHaptic(ClickHapticDuration, ClickHapticAmplitude, ClickHapticFrequency);
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

        private void EndTouch(FingerTouchState st, BasisInput input)
        {
            if (st.Phase == TouchPhase.Pressing) EndPress(st, input);
            EmitExit(st);
            st.Reset();
        }

        private static void CleanupState(FingerTouchState st)
        {
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
    }
}
