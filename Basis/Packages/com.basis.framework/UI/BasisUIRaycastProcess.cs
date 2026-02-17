using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;

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

        public void Initalize()
        {
            BasisDeviceManagement = BasisDeviceManagement.Instance;
            if (!HasEvent)
            {
                BasisDeviceManagement.AllInputDevices.OnListChanged += AllInputDevices;
                HasEvent = true;
            }
            AllInputDevices();
        }

        public void OnDeInitalize()
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

            int DevicesCount = Inputs.Count;
            HasTarget = false;
            var EffectiveMouseAction = false;

            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = Inputs[Index];
                if (input == null)
                {
                    continue;
                }

                if (input.HasRaycaster && input.BasisUIRaycast.WasCorrectLayer)
                {
                    var eventData = input.BasisUIRaycast.CurrentEventData;
                    if (eventData == null)
                    {
                        continue;
                    }

                    bool isDownThisFrame = input.CurrentInputState.Trigger == 1;

                    // Track down-transition for deselection later
                    EffectiveMouseAction |= !eventData.WasLastDown && isDownThisFrame;

                    // Handle button UP transition, even if there is no UI hit this frame
                    if (eventData.WasLastDown && !isDownThisFrame)
                    {
                        EffectiveMouseUp(eventData, input);
                        eventData.WasLastDown = false;
                    }

                    if (input.BasisUIRaycast.HadRaycastUITarget)
                    {
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
                            BasisDebug.LogWarning("[BasisUIRaycastProcess] Skipping raycast simulate — hit data or ray results missing.");
                        }
                    }
                    else
                    {
                        // Lost UI hit: force “no target” and process movement so we get pointerExit events.
                        if (eventData.pointerEnter != null || eventData.hovered.Count > 0)
                        {
                            eventData.pointerCurrentRaycast = new RaycastResult();
                            ProcessPointerMovement(eventData);
                        }
                    }
                }
            }

            if (!HasTarget && EffectiveMouseAction)
            {
                EventSystem.current.SetSelectedGameObject(null, null);
            }

            DevicesCount = Inputs.Count;
            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = Inputs[Index];
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
            currentEventData.scrollDelta = BaseInput.CurrentInputState.Secondary2DAxisDeadZoned;

            // Always keep latest raycast, so movement / scroll / hover use up-to-date info
            currentEventData.pointerCurrentRaycast = raycastResult;

            bool IsDownThisFrame = BaseInput.CurrentInputState.Trigger == 1;

            Shader.SetGlobalVector(CursorPos, hit.worldHitPosition);

            // ---- BUTTON DOWN ----
            if (IsDownThisFrame)
            {
                if (!currentEventData.WasLastDown)
                {
                    // First frame of this press: set pressPosition & press raycast
                    currentEventData.pressPosition = hit.screenPosition;
                    currentEventData.pointerPressRaycast = raycastResult;

                    CheckOrApplySelectedGameobject(hit, currentEventData);
                    currentEventData.WasLastDown = true;
                    EffectiveMouseDown(hit, currentEventData);
                }
            }
            // Button UP is handled in Simulate() based on trigger state

            // ---- OTHER POINTER EVENTS ----
            ProcessScrollWheel(currentEventData);

            // VR-friendly: larger drag threshold so tiny jitter isn't a drag.
            ProcessPointerMovement(currentEventData);
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

            // Where did we "release"?
            GameObject releaseGameObject = CurrentEventData.pointerCurrentRaycast.gameObject;

            GameObject pointerUpHandler = null;
            if (releaseGameObject != null)
            {
                pointerUpHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(releaseGameObject);
            }

            var pointerDrag = CurrentEventData.pointerDrag;

            if (target == pointerUpHandler && CurrentEventData.eligibleForClick && pointerUpHandler != null)
            {
                BaseInput.PlayHaptic(0.1f, 1f, 0.5f);
                // BaseInput.PlaySoundEffect("press", SMModuleAudio.ActiveMenusVolume / 80);
                ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerClickHandler);
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
