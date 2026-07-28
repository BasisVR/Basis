using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.Scripts.UI
{
    public class BasisUIRaycastProcess
    {
        public float ClickSpeed = 0.3f;
        public static bool HasTarget;
        public BasisDeviceManagement BasisDeviceManagement;
        public List<BasisInput> Inputs;
        public bool HasEvent = false;

        private Vector2 _vrScrollStick;

        private static bool IsTriggerDown(BasisInput input, bool wasDown)
        {
            float trigger = input.CurrentInputState.Trigger;
            float press = BasisSettingsDefaults.UIClickPressThreshold.RawValue;
            float release = Mathf.Min(BasisSettingsDefaults.UIClickReleaseThreshold.RawValue, press);
            return wasDown ? trigger >= release : trigger >= press;
        }

        public void Initialize()
        {
            BasisDeviceManagement = BasisDeviceManagement.Instance;
            if (!HasEvent)
            {
                BasisDeviceManagement.AllInputDevices.OnListChanged += AllInputDevices;
                HasEvent = true;
            }
            AllInputDevices();
        }

        public void OnDeInitialize()
        {
            if (HasEvent && BasisDeviceManagement != null)
            {
                BasisDeviceManagement.AllInputDevices.OnListChanged -= AllInputDevices;
                HasEvent = false;
            }
        }

        public void AllInputDevices()
        {
            Inputs = BasisDeviceManagement.AllInputDevices.ToList();
        }

        public void Simulate()
        {
            if (Inputs == null)
            {
                return;
            }

            // Snapshot the field before iterating: a UI event raised inside this
            // loop (e.g. a pointerUp that fires a dropdown OnValueChanged that
            // mutates BasisDeviceManagement.AllInputDevices) replaces the Inputs
            // field via AllInputDevices() callback. Indexing the new shorter
            // list with the old cached count throws ArgumentOutOfRange.
            List<BasisInput> snapshot = Inputs;
            int DevicesCount = snapshot.Count;
            _vrScrollStick = ReadRightHandScrollStick(snapshot, DevicesCount);
            HasTarget = false;
            var EffectiveMouseAction = false;

            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = snapshot[Index];
                if (input == null)
                {
                    continue;
                }

                if (input.HasRaycaster)
                {
                    // Skip ray-based UI events when direct finger touch is active for this device.
                    // The ray's panel pointer is released first, or its last hover would stay lit
                    // on the panel for as long as the finger is poking.
                    if (BasisDirectTouch.Instance != null && BasisDirectTouch.Instance.IsDeviceTouching(input))
                    {
                        input.BasisUIRaycast.ToolkitPointer.BeginFrame(false);
                        input.BasisUIRaycast.ToolkitPointer.Release();
                        continue;
                    }

                    var eventData = input.BasisUIRaycast.CurrentEventData;
                    if (eventData == null)
                    {
                        continue;
                    }

                    bool hasActiveUITarget = input.BasisUIRaycast.WasCorrectLayer && input.BasisUIRaycast.HadRaycastUITarget;
                    bool hasToolkitTarget = input.BasisUIRaycast.WasCorrectLayer && input.BasisUIRaycast.HadToolkitPanelTarget;
                    BasisUIToolkitPointer toolkitPointer = input.BasisUIRaycast.ToolkitPointer;

                    bool isDownThisFrame = IsTriggerDown(input, eventData.WasLastDown);

                    // Track down-transition for deselection later
                    if (input.BasisUIRaycast.WasCorrectLayer)
                    {
                        EffectiveMouseAction |= !eventData.WasLastDown && isDownThisFrame;
                    }

                    // Handle button UP transition, even if there is no UI hit this frame
                    if (eventData.WasLastDown && !isDownThisFrame)
                    {
                        EffectiveMouseUp(eventData, input);
                        eventData.WasLastDown = false;
                    }

                    toolkitPointer.BeginFrame(isDownThisFrame);

                    if (toolkitPointer.WantsCapture && TryGetToolkitCapturePosition(input, toolkitPointer, out Vector2 capturedPanelPosition))
                    {
                        // A pressed panel owns the pointer until release. UI Toolkit captures on
                        // press, so this keeps driving the captured element even when the ray
                        // leaves the panel or crosses a different one — the native equivalent of
                        // the uGUI drag-plane capture below (#869).
                        toolkitPointer.Process(toolkitPointer.CapturedPanel, capturedPanelPosition, isDownThisFrame, ComputeScrollDelta(input));
                        HasTarget = true;
                    }
                    else if (hasActiveUITarget)
                    {
                        toolkitPointer.Release();
                        List<BasisRaycastUIHitData> hitData = input.BasisUIRaycast.SortedGraphics;
                        List<RaycastResult> RaycastResults = input.BasisUIRaycast.SortedRays;

                        if (hitData != null && RaycastResults != null &&
                            hitData.Count != 0 && RaycastResults.Count != 0)
                        {
                            RaycastResult hit = RaycastResults[0];
                            if (hitData[0].graphic != null && hitData[0].graphic.gameObject != null)
                            {
                                hit.gameObject = hitData[0].graphic.gameObject;
                                SimulateOnCanvas(hit, hitData[0], eventData, input);
                                HasTarget = true;
                            }
                        }
                        else
                        {
                            BasisDebug.LogWarning(nameof(BasisUIRaycastProcess) + "Skipping raycast simulate — hit data or ray results missing.");
                        }
                    }
                    else if (hasToolkitTarget)
                    {
                        toolkitPointer.Process(input.BasisUIRaycast.HitToolkitPanel, input.BasisUIRaycast.ToolkitPanelPosition, isDownThisFrame, ComputeScrollDelta(input));
                        HasTarget = true;
                    }
                    else if (eventData.WasLastDown && eventData.pointerDrag != null &&
                        TryGetDragCapturePosition(input, eventData, out Vector2 capturePosition))
                    {
                        toolkitPointer.Release();
                        // A held drag (slider/scrollbar/scroll view) whose ray slipped off every
                        // raycastable graphic: keep driving it from the ray projected onto the
                        // plane it was pressed on, instead of freezing until release (#869).
                        eventData.delta = capturePosition - eventData.position;
                        eventData.position = capturePosition;
                        eventData.pointerCurrentRaycast = new RaycastResult();
                        ProcessPointerButtonDrag(eventData, pixelDragThresholdMultiplier: 3.0f);
                        HasTarget = true;
                    }
                    else
                    {
                        toolkitPointer.Release();

                        // Lost UI hit (or moved to non-UI layer): send pointerExit events and clear selection.
                        if (eventData.pointerEnter != null || eventData.hovered.Count > 0)
                        {
                            eventData.pointerCurrentRaycast = new RaycastResult();
                            ProcessPointerMovement(eventData);
                            eventData.HapticHoverTarget = null;

                            // Clear EventSystem selection so UI elements (e.g. toggles/buttons)
                            // don't stay stuck in the “Selected” highlight visual state.
                            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                            {
                                EventSystem.current.SetSelectedGameObject(null, eventData);
                            }
                        }
                    }
                }
            }

            if (!HasTarget && EffectiveMouseAction)
            {
                EventSystem.current.SetSelectedGameObject(null, null);
            }

            // Re-snapshot in case the first loop's events mutated the device list.
            snapshot = Inputs;
            DevicesCount = snapshot.Count;
            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = snapshot[Index];
                if (input == null)
                    continue;

                if (input.HasRaycaster && input.BasisUIRaycast.WasCorrectLayer)
                {
                    var eventData = input.BasisUIRaycast.CurrentEventData;
                    if (eventData != null)
                    {
                        // Needed if you want to use the keyboard
                        SendUpdateEventToSelectedObject(eventData);
                    }
                }
            }
        }

        private static Vector2 ReadRightHandScrollStick(List<BasisInput> snapshot, int DevicesCount)
        {
            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = snapshot[Index];
                if (input != null && input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.RightHand)
                {
                    return input.CurrentInputState.Primary2DAxisDeadZoned;
                }
            }
            return Vector2.zero;
        }

        private const string CursorPos = "_CursorPos";

        public void SimulateOnCanvas(RaycastResult raycastResult, BasisRaycastUIHitData hit, BasisPointerEventData currentEventData, BasisInput BaseInput)
        {
            if (hit.graphic == null || currentEventData == null)
            {
                return;
            }

            HasTarget = true;

            // ---- POINTER POSITION / DELTA UPDATE (NO HARD RESET) ----
            Vector2 previousPosition = currentEventData.position;
            currentEventData.delta = hit.screenPosition - previousPosition;
            currentEventData.position = hit.screenPosition;
            currentEventData.scrollDelta = ComputeScrollDelta(BaseInput);

            // Always keep latest raycast, so movement / scroll / hover use up-to-date info
            currentEventData.pointerCurrentRaycast = raycastResult;

            bool IsDownThisFrame = IsTriggerDown(BaseInput, currentEventData.WasLastDown);

            Shader.SetGlobalVector(CursorPos, hit.worldHitPosition);

            // Update hover before processing a press. Touch pointers move to their target and
            // press in the same frame, so requiring the previous frame's pointerEnter makes
            // the first Android tap establish hover without activating the control.
            ProcessPointerMovement(currentEventData);

            // ---- BUTTON DOWN ----
            if (IsDownThisFrame)
            {
                if (!currentEventData.WasLastDown)
                {
                    GameObject pressTarget = hit.graphic.gameObject;
                    if (currentEventData.pointerEnter == pressTarget)
                    {
                        currentEventData.pressPosition = hit.screenPosition;
                        currentEventData.pointerPressRaycast = raycastResult;

                        CheckOrApplySelectedGameobject(hit, currentEventData);
                        currentEventData.WasLastDown = true;
                        EffectiveMouseDown(hit, currentEventData);
                    }
                    else
                    {
                        currentEventData.WasLastDown = true;
                        currentEventData.eligibleForClick = false;
                        currentEventData.pointerPress = null;
                    }
                }
            }
            // Button UP is handled in Simulate() based on trigger state

            // ---- OTHER POINTER EVENTS ----
            UpdateHoverHaptic(currentEventData, BaseInput);
            ProcessScrollWheel(currentEventData);

            // VR-friendly: larger drag threshold so tiny jitter isn't a drag.
            ProcessPointerButtonDrag(currentEventData, pixelDragThresholdMultiplier: 3.0f);
        }

        public void CheckOrApplySelectedGameobject(BasisRaycastUIHitData hit, BasisPointerEventData CurrentEventData)
        {
            if (hit.graphic != null)
            {
                if (EventSystem.current.currentSelectedGameObject != hit.graphic.gameObject)
                {
                    EventSystem.current.SetSelectedGameObject(hit.graphic.gameObject, CurrentEventData);
                }
            }
            else
            {
                EventSystem.current.SetSelectedGameObject(null, CurrentEventData);
            }
        }

        public void EffectiveMouseDown(BasisRaycastUIHitData hit, BasisPointerEventData CurrentEventData)
        {
            CurrentEventData.eligibleForClick = true;
            CurrentEventData.delta = Vector2.zero;
            CurrentEventData.dragging = false;
            CurrentEventData.pressPosition = CurrentEventData.position;
            CurrentEventData.pointerPressRaycast = CurrentEventData.pointerCurrentRaycast;
            CurrentEventData.useDragThreshold = true;
            CurrentEventData.selectedObject = hit.graphic.gameObject;
            CurrentEventData.button = PointerEventData.InputButton.Left;

            GameObject selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(hit.graphic.gameObject);
            if (selectHandler != EventSystem.current.currentSelectedGameObject)
            {
                EventSystem.current.SetSelectedGameObject(selectHandler, CurrentEventData);
            }

            GameObject newPressed = ExecuteEvents.ExecuteHierarchy(hit.graphic.gameObject, CurrentEventData, ExecuteEvents.pointerDownHandler);
            if (newPressed == null)
            {
                newPressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.graphic.gameObject);
            }

            float time = Time.unscaledTime;
            if (newPressed == CurrentEventData.lastPress && ((time - CurrentEventData.clickTime) < ClickSpeed))
            {
                ++CurrentEventData.clickCount;
            }
            else
            {
                CurrentEventData.clickCount = 1;
            }

            CurrentEventData.clickTime = time;
            CurrentEventData.pointerPress = newPressed;
            CurrentEventData.rawPointerPress = hit.graphic.gameObject;

            // Save the drag handler for drag events during this mouse down.
            var dragObject = ExecuteEvents.GetEventHandler<IDragHandler>(hit.graphic.gameObject);
            CurrentEventData.pointerDrag = dragObject;

            if (dragObject != null)
            {
                Canvas canvas = hit.graphic.canvas;
                CurrentEventData.DragPlaneTransform = canvas != null ? canvas.transform : hit.graphic.rectTransform;
                CurrentEventData.DragEventCamera = canvas != null ? canvas.worldCamera : null;
                ExecuteEvents.Execute(dragObject, CurrentEventData, ExecuteEvents.initializePotentialDrag);
            }
        }

        // NOTE: No BasisRaycastUIHitData here anymore – everything comes from eventData
        public void EffectiveMouseUp(BasisPointerEventData CurrentEventData, BasisInput BaseInput)
        {
            var target = CurrentEventData.pointerPress;

            if (target != null)
            {
                ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerUpHandler);
            }

            // Where did we "release"? Still needed for the drop handler below.
            GameObject releaseGameObject = CurrentEventData.pointerCurrentRaycast.gameObject;

            var pointerDrag = CurrentEventData.pointerDrag;

            // Accept the click on release as long as the press began on a clickable
            // element and is still click-eligible — even if the pointer has since
            // drifted off it. The release no longer has to land back on the pressed
            // element, which helps users whose hands move slightly between pressing
            // and releasing the trigger (issue #826). A press that turned into a drag
            // off its target clears eligibleForClick (see ProcessPointerButtonDrag),
            // so dragging a scrollbar/slider still won't fire a stray click.
            GameObject pressClickHandler = target != null
                ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)
                : null;

            if (pressClickHandler != null && CurrentEventData.eligibleForClick)
            {
                if (BasisSettingsDefaults.UIHaptics.RawValue)
                {
                    BaseInput.PlayHaptic(0.1f, 0.1f, 0.5f);
                }
                // BaseInput.PlaySoundEffect("press", SMModuleAudio.ActiveMenusVolume / 80);
                ExecuteEvents.Execute(pressClickHandler, CurrentEventData, ExecuteEvents.pointerClickHandler);
            }
            else if (CurrentEventData.dragging && pointerDrag != null && releaseGameObject != null)
            {
                ExecuteEvents.ExecuteHierarchy(releaseGameObject, CurrentEventData, ExecuteEvents.dropHandler);
            }

            CurrentEventData.eligibleForClick = false;
            CurrentEventData.pointerPress = null;
            CurrentEventData.rawPointerPress = null;

            if (CurrentEventData.dragging && pointerDrag != null)
            {
                ExecuteEvents.Execute(pointerDrag, CurrentEventData, ExecuteEvents.endDragHandler);
            }

            CurrentEventData.dragging = false;
            CurrentEventData.pointerDrag = null;
            CurrentEventData.DragPlaneTransform = null;
            CurrentEventData.DragEventCamera = null;
        }

        private static bool TryGetToolkitCapturePosition(BasisInput input, BasisUIToolkitPointer pointer, out Vector2 panelPosition)
        {
            panelPosition = default;
            BasisUIToolkitPanel panel = pointer.CapturedPanel;
            if (panel == null)
            {
                return false;
            }

            Ray ray = input.BasisUIRaycast.BasisPointRaycaster.ray;
            return panel.TryGetPanelPosition(ray, false, out panelPosition, out _, out _);
        }

        private static bool TryGetDragCapturePosition(BasisInput input, BasisPointerEventData eventData, out Vector2 screenPosition)
        {
            screenPosition = default;
            Transform planeTransform = eventData.DragPlaneTransform;
            Camera eventCamera = eventData.DragEventCamera;
            if (planeTransform == null || eventCamera == null)
            {
                return false;
            }

            Ray ray = input.BasisUIRaycast.BasisPointRaycaster.ray;
            Plane plane = new Plane(planeTransform.forward, planeTransform.position);
            if (!plane.Raycast(ray, out float enter))
            {
                return false;
            }

            Vector3 projected = eventCamera.WorldToScreenPoint(ray.GetPoint(enter));
            if (projected.z <= 0f)
            {
                return false;
            }

            screenPosition = projected;
            return true;
        }

        private static void UpdateHoverHaptic(BasisPointerEventData eventData, BasisInput input)
        {
            GameObject interactiveRoot = null;
            if (eventData.pointerEnter != null)
            {
                interactiveRoot = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventData.pointerEnter);
                if (interactiveRoot == null)
                {
                    interactiveRoot = ExecuteEvents.GetEventHandler<IDragHandler>(eventData.pointerEnter);
                }
            }

            if (interactiveRoot == eventData.HapticHoverTarget)
            {
                return;
            }

            eventData.HapticHoverTarget = interactiveRoot;
            if (interactiveRoot != null && !eventData.WasLastDown && BasisSettingsDefaults.UIHaptics.RawValue)
            {
                input.PlayHaptic(0.02f, 0.25f, 0.5f);
            }
        }

        public bool SendUpdateEventToSelectedObject(BasisPointerEventData CurrentEventData)
        {
            if (EventSystem.current.currentSelectedGameObject == null)
            {
                return false;
            }
            ExecuteEvents.Execute(EventSystem.current.currentSelectedGameObject, CurrentEventData, ExecuteEvents.updateSelectedHandler);
            return CurrentEventData.used;
        }

        // Continuous VR stick → per-second rate × unscaledDeltaTime (frame-rate independent).
        // Desktop mouse wheel (CenterEye) is already a discrete tick, so pass it through unscaled.
        private Vector2 ComputeScrollDelta(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye)
            {
                return input.CurrentInputState.Secondary2DAxisDeadZoned;
            }

            Vector2 axis = _vrScrollStick + input.CurrentInputState.Secondary2DAxisDeadZoned;
            return axis * (SMModuleControllerSettings.ScrollSpeed * Time.unscaledDeltaTime);
        }

        public void ProcessScrollWheel(BasisPointerEventData eventData)
        {
            var scrollDelta = eventData.scrollDelta;
            if (!Mathf.Approximately(scrollDelta.sqrMagnitude, 0f))
            {
                GameObject scrollTarget = eventData.pointerEnter;
                if (scrollTarget == null)
                {
                    scrollTarget = eventData.pointerCurrentRaycast.gameObject;
                }

                if (scrollTarget != null)
                {
                    var scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(scrollTarget);
                    if (scrollHandler != null)
                    {
                        ExecuteEvents.ExecuteHierarchy(scrollHandler, eventData, ExecuteEvents.scrollHandler);
                    }
                }
            }
        }

        public void ProcessPointerMovement(BasisPointerEventData eventData)
        {
            var currentPointerTarget = eventData.pointerCurrentRaycast.gameObject;
            var wasMoved = eventData.IsPointerMoving();

            // If the pointer moved, send move events to everything currently hovered.
            if (wasMoved)
            {
                for (var i = 0; i < eventData.hovered.Count; ++i)
                {
                    ExecuteEvents.Execute(eventData.hovered[i], eventData, ExecuteEvents.pointerMoveHandler);
                }
            }

            // If we have no target or pointerEnter has been deleted,
            // we just send exit events to anything we are tracking and then exit.
            if (currentPointerTarget == null || eventData.pointerEnter == null)
            {
                foreach (var hovered in eventData.hovered)
                {
                    ExecuteEvents.Execute(hovered, eventData, ExecuteEvents.pointerExitHandler);
                }

                eventData.hovered.Clear();

                if (currentPointerTarget == null)
                {
                    eventData.pointerEnter = null;
                    return;
                }
            }

            if (eventData.pointerEnter == currentPointerTarget)
                return;

            var commonRoot = FindCommonRoot(eventData.pointerEnter, currentPointerTarget);

            // Exit from old hierarchy up to common root
            if (eventData.pointerEnter != null)
            {
                var target = eventData.pointerEnter.transform;

                while (target != null)
                {
                    if (commonRoot != null && commonRoot.transform == target)
                        break;

                    var targetGameObject = target.gameObject;
                    ExecuteEvents.Execute(targetGameObject, eventData, ExecuteEvents.pointerExitHandler);

                    eventData.hovered.Remove(targetGameObject);

                    target = target.parent;
                }
            }

            eventData.pointerEnter = currentPointerTarget;
            if (currentPointerTarget != null)
            {
                var target = currentPointerTarget.transform;

                while (target != null && target.gameObject != commonRoot)
                {
                    var targetGameObject = target.gameObject;
                    ExecuteEvents.Execute(targetGameObject, eventData, ExecuteEvents.pointerEnterHandler);
                    if (wasMoved)
                    {
                        ExecuteEvents.Execute(targetGameObject, eventData, ExecuteEvents.pointerMoveHandler);
                    }
                    eventData.hovered.Add(targetGameObject);

                    target = target.parent;
                }
            }
        }

        /// <summary>
        /// called Correctly
        /// </summary>
        /// <param name="CurrentEventData"></param>
        /// <param name="pixelDragThresholdMultiplier"></param>
        public void ProcessPointerButtonDrag(BasisPointerEventData CurrentEventData, float pixelDragThresholdMultiplier = 1.0f)
        {
            // Only consider drag while the button is actually held.
            if (!CurrentEventData.WasLastDown)
            {
                return;
            }

            if (!CurrentEventData.IsPointerMoving() || CurrentEventData.pointerDrag == null)
            {
                return;
            }

            if (!CurrentEventData.dragging)
            {
                var threshold = EventSystem.current.pixelDragThreshold * pixelDragThresholdMultiplier;
                if (!CurrentEventData.useDragThreshold ||
                    (CurrentEventData.pressPosition - CurrentEventData.position).sqrMagnitude >= (threshold * threshold))
                {
                    var target = CurrentEventData.pointerDrag;
                    ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.beginDragHandler);
                    CurrentEventData.dragging = true;
                }
            }

            if (CurrentEventData.dragging)
            {
                // If we moved from our initial press object, process an up for that object.
                var target = CurrentEventData.pointerPress;
                if (target != null && target != CurrentEventData.pointerDrag)
                {
                    ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerUpHandler);
                    CurrentEventData.eligibleForClick = false;
                    CurrentEventData.pointerPress = null;
                    CurrentEventData.rawPointerPress = null;
                }

                ExecuteEvents.Execute(CurrentEventData.pointerDrag, CurrentEventData, ExecuteEvents.dragHandler);
            }
        }

        public static GameObject FindCommonRoot(GameObject g1, GameObject g2)
        {
            if (g1 == null || g2 == null)
            {
                return null;
            }

            var t1 = g1.transform;
            while (t1 != null)
            {
                var t2 = g2.transform;
                while (t2 != null)
                {
                    if (t1 == t2)
                        return t1.gameObject;
                    t2 = t2.parent;
                }
                t1 = t1.parent;
            }
            return null;
        }

        public void HandlePointerExitAndEnter(BasisPointerEventData currentPointerData, GameObject newEnterTarget)
        {
            // Left as-is from your original, this is an alternative movement handler.

            // if we have no target / pointerEnter has been deleted
            // just send exit events to anything we are tracking then exit
            if (newEnterTarget == null || currentPointerData.pointerEnter == null)
            {
                var hoveredCount = currentPointerData.hovered.Count;
                for (var i = 0; i < hoveredCount; ++i)
                {
                    currentPointerData.fullyExited = true;
                    ExecuteEvents.Execute(currentPointerData.hovered[i], currentPointerData, ExecuteEvents.pointerMoveHandler);
                    ExecuteEvents.Execute(currentPointerData.hovered[i], currentPointerData, ExecuteEvents.pointerExitHandler);
                }

                currentPointerData.hovered.Clear();

                if (newEnterTarget == null)
                {
                    currentPointerData.pointerEnter = null;
                    return;
                }
            }

            // if we have not changed hover target
            if (currentPointerData.pointerEnter == newEnterTarget && newEnterTarget)
            {
                if (currentPointerData.IsPointerMoving())
                {
                    var hoveredCount = currentPointerData.hovered.Count;
                    for (var i = 0; i < hoveredCount; ++i)
                        ExecuteEvents.Execute(currentPointerData.hovered[i], currentPointerData, ExecuteEvents.pointerMoveHandler);
                }
                return;
            }

            GameObject commonRoot = FindCommonRoot(currentPointerData.pointerEnter, newEnterTarget);
            GameObject pointerParent = ((Component)newEnterTarget.GetComponentInParent<IPointerExitHandler>())?.gameObject;

            // and we already have an entered object from last time
            if (currentPointerData.pointerEnter != null)
            {
                // send exit handler call to all elements in the chain until we reach the new target, or null
                Transform t = currentPointerData.pointerEnter.transform;

                while (t != null)
                {
                    if (commonRoot != null && commonRoot.transform == t)
                        break;

                    currentPointerData.fullyExited = t.gameObject != commonRoot && currentPointerData.pointerEnter != newEnterTarget;
                    ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerMoveHandler);
                    ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerExitHandler);
                    currentPointerData.hovered.Remove(t.gameObject);

                    t = t.parent;

                    if (commonRoot != null && commonRoot.transform == t)
                        break;
                }
            }

            // now issue the enter call up to but not including the common root
            var oldPointerEnter = currentPointerData.pointerEnter;
            currentPointerData.pointerEnter = newEnterTarget;
            if (newEnterTarget != null)
            {
                Transform t = newEnterTarget.transform;

                while (t != null)
                {
                    currentPointerData.reentered = t.gameObject == commonRoot && t.gameObject != oldPointerEnter;
                    if (currentPointerData.reentered)
                        break;

                    ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerMoveHandler);
                    currentPointerData.hovered.Add(t.gameObject);

                    t = t.parent;

                    if (commonRoot != null && commonRoot.transform == t)
                        break;
                }
            }
        }
    }
}
