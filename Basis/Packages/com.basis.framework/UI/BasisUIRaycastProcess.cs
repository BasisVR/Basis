using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using static Basis.Scripts.UI.BasisUIRaycast;

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
        if (HasEvent == false)
        {
            BasisDeviceManagement.AllInputDevices.OnListChanged += AllInputDevices;
            HasEvent = true;
        }
        AllInputDevices();
    }
    public void OnDeInitalize()
    {
        if (HasEvent)
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
            int DevicesCount = Inputs.Count;
            HasTarget = false;
            var EffectiveMouseAction = false;

            for (int Index = 0; Index < DevicesCount; Index++)
            {
                BasisInput input = Inputs[Index];
                if (input.HasRaycaster && input.BasisUIRaycast.WasCorrectLayer)
                {
                    EffectiveMouseAction |= input.BasisUIRaycast.CurrentEventData.WasLastDown == false && input.CurrentInputState.Trigger == 1;
                    if (input.BasisUIRaycast.HadRaycastUITarget)
                    {
                        List<RaycastUIHitData> hitData = input.BasisUIRaycast.SortedGraphics;
                        List<RaycastResult> RaycastResults = input.BasisUIRaycast.SortedRays;
                        // TODO: test things and make sure we dont need to re-sort using default sort algo since UI raycast uses a custom sort fn
                        // hitData.Sort((g1, g2) => g2.graphic.depth.CompareTo(g1.graphic.depth));
                        if (hitData.Count > 0 && RaycastResults.Count > 0)
                        {
                            RaycastResult hit = RaycastResults[0];
                            hit.gameObject = hitData[0].graphic?.gameObject;

                            if (hit.gameObject != null)
                            {
                                SimulateOnCanvas(hit, hitData[0], input.BasisUIRaycast.CurrentEventData, input.CurrentInputState, input.LastInputState);
                                HasTarget = true;
                            }
                        }
                        else
                        {
                            BasisDebug.LogWarning("[BasisUIRaycastProcess] Skipping raycast simulate — hit data or ray results missing.");
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
                if (input.HasRaycaster && input.BasisUIRaycast.WasCorrectLayer)
                {
                    SendUpdateEventToSelectedObject(input.BasisUIRaycast.CurrentEventData); //needed if you want to use the keyboard
                }
            }
        }
    public void SimulateOnCanvas(RaycastResult raycastResult, RaycastUIHitData hit, BasisPointerEventData currentEventData, BasisInputState Current, BasisInputState LastCurrent)
    {
        if (hit.graphic != null)
        {
            HasTarget = true;
            currentEventData.Reset();
            currentEventData.delta = hit.screenPosition - currentEventData.position;
          //  raycastResult.module = baseRayOverride;
            currentEventData.position = hit.screenPosition;
            currentEventData.pressPosition = hit.screenPosition;
            currentEventData.pointerCurrentRaycast = raycastResult;
            currentEventData.pointerPressRaycast = raycastResult;
            bool IsDownThisFrame = Current.Trigger == 1;
            //BasisDebug.Log("running "  + raycastResult.gameObject);
            if (IsDownThisFrame)
            {
                if (currentEventData.WasLastDown == false)
                {
                    CheckOrApplySelectedGameobject(hit, currentEventData);
                    currentEventData.WasLastDown = true;
                    EffectiveMouseDown(hit, currentEventData);
                }
            }
            else
            {
                if (currentEventData.WasLastDown)
                {
                    EffectiveMouseUp(hit, currentEventData);
                    currentEventData.WasLastDown = false;
                }
            }

            ProcessScrollWheel(currentEventData);
            ProcessPointerMovement(currentEventData);
            ProcessPointerButtonDrag(currentEventData);
        }
    }
    public void CheckOrApplySelectedGameobject(RaycastUIHitData hit, BasisPointerEventData CurrentEventData)
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
    public void EffectiveMouseDown(RaycastUIHitData hit, BasisPointerEventData CurrentEventData)
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
    public void EffectiveMouseUp(RaycastUIHitData hit, BasisPointerEventData CurrentEventData)
    {
        var target = CurrentEventData.pointerPress;
        ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerUpHandler);

        var pointerUpHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.graphic.gameObject.gameObject);
        var pointerDrag = CurrentEventData.pointerDrag;
        if (target == pointerUpHandler && CurrentEventData.eligibleForClick)
        {
            ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerClickHandler);
        }
        else if (CurrentEventData.dragging && pointerDrag != null)
        {
            ExecuteEvents.ExecuteHierarchy(hit.graphic.gameObject, CurrentEventData, ExecuteEvents.dropHandler);
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
            var scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(eventData.pointerEnter);
            ExecuteEvents.ExecuteHierarchy(scrollHandler, eventData, ExecuteEvents.scrollHandler);
        }
    }
    public void ProcessPointerMovement(BasisPointerEventData eventData)
    {
        var currentPointerTarget = eventData.pointerCurrentRaycast.gameObject;
        // If the pointer moved, send move events to all UI elements the pointer is
        // currently over.
        var wasMoved = eventData.IsPointerMoving();
        if (wasMoved)
        {
            for (var i = 0; i < eventData.hovered.Count; ++i)
            {
                ExecuteEvents.Execute(eventData.hovered[i], eventData, ExecuteEvents.pointerMoveHandler);
            }
        }

        // If we have no target or pointerEnter has been deleted,
        // we just send exit events to anything we are tracking
        // and then exit.
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

        // We walk up the tree until a common root and the last entered and current entered object is found.
        // Then send exit and enter events up to, but not including, the common root.
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
        if (!CurrentEventData.IsPointerMoving() || CurrentEventData.pointerDrag == null)
        {
            return;
        }

        if (!CurrentEventData.dragging)
        {
            var threshold = EventSystem.current.pixelDragThreshold * pixelDragThresholdMultiplier;
            if (!CurrentEventData.useDragThreshold || (CurrentEventData.pressPosition - CurrentEventData.position).sqrMagnitude >= (threshold * threshold))
            {
                var target = CurrentEventData.pointerDrag;
            //    BasisDebug.Log("Started Dragging " + (CurrentEventData.pressPosition - CurrentEventData.position).sqrMagnitude);
                ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.beginDragHandler);
                CurrentEventData.dragging = true;
            }
        }

        if (CurrentEventData.dragging)
        {
            // If we moved from our initial press object, process an up for that object.
            var target = CurrentEventData.pointerPress;
            if (target != CurrentEventData.pointerDrag)
            {
                ExecuteEvents.Execute(target, CurrentEventData, ExecuteEvents.pointerUpHandler);
              //  BasisDebug.Log("pointerUpHandler");
                CurrentEventData.eligibleForClick = false;
                CurrentEventData.pointerPress = null;
                CurrentEventData.rawPointerPress = null;
            }
         //   BasisDebug.Log("dragHandler " + CurrentEventData.position);
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
        // if we have no target / pointerEnter has been deleted
        // just send exit events to anything we are tracking
        // then exit
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

        // and we already an entered object from last time
        if (currentPointerData.pointerEnter != null)
        {
            // send exit handler call to all elements in the chain
            // until we reach the new target, or null!
            // ** or when !m_SendPointerEnterToParent, stop when meeting a gameobject with an exit event handler
            Transform t = currentPointerData.pointerEnter.transform;

            while (t != null)
            {
                // if we reach the common root break out!
                if (true && commonRoot != null && commonRoot.transform == t)
                    break;

                currentPointerData.fullyExited = t.gameObject != commonRoot && currentPointerData.pointerEnter != newEnterTarget;
                ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerMoveHandler);
                ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerExitHandler);
                currentPointerData.hovered.Remove(t.gameObject);

                t = t.parent;

                // if we reach the common root break out!
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
                // if we are sending the event to parent, they are already in hover mode at that point. No need to bubble up the event.
                if (currentPointerData.reentered)
                    break;

                ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(t.gameObject, currentPointerData, ExecuteEvents.pointerMoveHandler);
                currentPointerData.hovered.Add(t.gameObject);

                // stop when encountering an object with the pointerEnterHandler

                t = t.parent;

                // if we reach the common root break out!
                if (commonRoot != null && commonRoot.transform == t)
                    break;

            }
        }
    }
}
}
