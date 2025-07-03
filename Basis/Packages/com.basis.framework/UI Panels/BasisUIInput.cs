/*
 * using System;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.Serialization;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;
using UnityEngine;
using UnityEngine.InputSystem;



#if UNITY_EDITOR
using UnityEditor;

namespace Basis.Scripts.UI.UI_Panels
{
#endif

public class BasisUIInput : BaseInputModule
{
    /// <summary>
    /// Called by <c>EventSystem</c> when the input module is made current.
    /// </summary>
    public override void ActivateModule()
    {
        base.ActivateModule();

        // Select firstSelectedGameObject if nothing is selected ATM.
        var toSelect = eventSystem.currentSelectedGameObject;
        if (toSelect == null)
            toSelect = eventSystem.firstSelectedGameObject;
        eventSystem.SetSelectedGameObject(toSelect, GetBaseEventData());
    }
    public override bool IsPointerOverGameObject(int pointerOrTouchId)
    {
        return true;
    }
    private RaycastResult PerformRaycast(ExtendedPointerEventData eventData)
    {
        return new RaycastResult();
    }
    private void ProcessPointer(ref PointerModel state)
    {
        var eventData = state.eventData;

        // Sync position.
        var pointerType = eventData.pointerType;
        if (pointerType == UIPointerType.MouseOrPen && Cursor.lockState == CursorLockMode.Locked)
        {
            eventData.position = new Vector2(Screen.width / 2f, Screen.height / 2f);
            ////REVIEW: This is consistent with StandaloneInputModule but having no deltas in locked mode seems wrong
            eventData.delta = default;
        }

        // Clear the 'used' flag.
        eventData.Reset();

        // Raycast from current position.
        eventData.pointerCurrentRaycast = PerformRaycast(eventData);

        // Sync position for tracking devices. For those, we can only do this
        // after the raycast as the screen-space position is a byproduct of the raycast.
        if (pointerType == UIPointerType.Tracked && eventData.pointerCurrentRaycast.isValid)
        {
            var screenPos = eventData.pointerCurrentRaycast.screenPosition;
            eventData.delta = screenPos - eventData.position;
            eventData.position = eventData.pointerCurrentRaycast.screenPosition;
        }

        ////REVIEW: for touch, we only need the left button; should we skip right and middle button processing? then we also don't need to copy to/from the event

        // Left mouse button. Movement and scrolling is processed with event set left button.
        eventData.button = PointerEventData.InputButton.Left;
        state.leftButton.CopyPressStateTo(eventData);

        // Unlike StandaloneInputModule, we process moves before processing buttons. This way
        // UI elements get pointer enters/exits before they get button ups/downs and clicks.
        ProcessPointerMovement(ref state, eventData);

        // We always need to process move-related events in order to get PointerEnter and Exit events
        // when we change UI state (e.g. show/hide objects) without moving the pointer. This unfortunately
        // also means that we will invariably raycast on every update.
        // However, after that, early out at this point when there's no changes to the pointer state (except
        // for tracked pointers as the tracking origin may have moved).
        if (!state.changedThisFrame && (xrTrackingOrigin == null || state.pointerType != UIPointerType.Tracked))
            return;

        ProcessPointerButton(ref state.leftButton, eventData);
        ProcessPointerButtonDrag(ref state.leftButton, eventData);
        ProcessPointerScroll(ref state, eventData);

        // Right mouse button.
        eventData.button = PointerEventData.InputButton.Right;
        state.rightButton.CopyPressStateTo(eventData);

        ProcessPointerButton(ref state.rightButton, eventData);
        ProcessPointerButtonDrag(ref state.rightButton, eventData);

        // Middle mouse button.
        eventData.button = PointerEventData.InputButton.Middle;
        state.middleButton.CopyPressStateTo(eventData);

        ProcessPointerButton(ref state.middleButton, eventData);
        ProcessPointerButtonDrag(ref state.middleButton, eventData);
    }
    private void ProcessPointerMovement(ref PointerModel pointer, ExtendedPointerEventData eventData)
    {
        var currentPointerTarget =
            // If the pointer is a touch that was released the *previous* frame, we generate pointer-exit events
            // and then later remove the pointer.
            eventData.pointerType == UIPointerType.Touch && !pointer.leftButton.isPressed && !pointer.leftButton.wasReleasedThisFrame
            ? null
            : eventData.pointerCurrentRaycast.gameObject;

        ProcessPointerMovement(eventData, currentPointerTarget);
    }

    private void ProcessPointerMovement(ExtendedPointerEventData eventData, GameObject currentPointerTarget)
    {
        var wasMoved = eventData.IsPointerMoving();
        if (wasMoved)
        {
            for (var i = 0; i < eventData.hovered.Count; ++i)
                ExecuteEvents.Execute(eventData.hovered[i], eventData, ExecuteEvents.pointerMoveHandler);
        }

        // If we have no target or pointerEnter has been deleted,
        // we just send exit events to anything we are tracking
        // and then exit.
        if (currentPointerTarget == null || eventData.pointerEnter == null)
        {
            for (var i = 0; i < eventData.hovered.Count; ++i)
                ExecuteEvents.Execute(eventData.hovered[i], eventData, ExecuteEvents.pointerExitHandler);

            eventData.hovered.Clear();

            if (currentPointerTarget == null)
            {
                eventData.pointerEnter = null;
                return;
            }
        }

        if (eventData.pointerEnter == currentPointerTarget && currentPointerTarget)
            return;

        var commonRoot = FindCommonRoot(eventData.pointerEnter, currentPointerTarget)?.transform;

        // We walk up the tree until a common root and the last entered and current entered object is found.
        // Then send exit and enter events up to, but not including, the common root.
        if (eventData.pointerEnter != null)
        {
            for (var current = eventData.pointerEnter.transform; current != null && current != commonRoot; current = current.parent)
            {
                ExecuteEvents.Execute(current.gameObject, eventData, ExecuteEvents.pointerExitHandler);
                eventData.hovered.Remove(current.gameObject);
            }
        }

        eventData.pointerEnter = currentPointerTarget;
        if (currentPointerTarget != null)
        {
            for (var current = currentPointerTarget.transform;
                 current != null && current != commonRoot;
                 current = current.parent)
            {
                ExecuteEvents.Execute(current.gameObject, eventData, ExecuteEvents.pointerEnterHandler);

                if (wasMoved)
                {
                    ExecuteEvents.Execute(current.gameObject, eventData, ExecuteEvents.pointerMoveHandler);
                }
                eventData.hovered.Add(current.gameObject);
            }
        }
    }

    private const float kClickSpeed = 0.3f;

    private void ProcessPointerButton(ref PointerModel.ButtonState button, PointerEventData eventData)
    {
        var currentOverGo = eventData.pointerCurrentRaycast.gameObject;

        if (currentOverGo != null && PointerShouldIgnoreTransform(currentOverGo.transform))
            return;

        // Button press.
        if (button.wasPressedThisFrame)
        {
            button.pressTime = InputRuntime.s_Instance.unscaledGameTime;

            eventData.delta = Vector2.zero;
            eventData.dragging = false;
            eventData.pressPosition = eventData.position;
            eventData.pointerPressRaycast = eventData.pointerCurrentRaycast;
            eventData.eligibleForClick = true;
            eventData.useDragThreshold = true;

            var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(currentOverGo);

            // If we have clicked something new, deselect the old thing and leave 'selection handling' up
            // to the press event (except if there's none and we're told to not deselect in that case).
            if (selectHandler != eventSystem.currentSelectedGameObject && (selectHandler != null || true))
                eventSystem.SetSelectedGameObject(null, eventData);

            // Invoke OnPointerDown, if present.
            var newPressed = ExecuteEvents.ExecuteHierarchy(currentOverGo, eventData, ExecuteEvents.pointerDownHandler);

            // If no GO responded to OnPointerDown, look for one that responds to OnPointerClick.
            // NOTE: This only looks up the handler. We don't invoke OnPointerClick here.
            if (newPressed == null)
                newPressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);

            // Reset click state if delay to last release was too long or if we didn't
            // press on the same object as last time. The latter part we don't know until
            // we've actually run the press handler.
            button.clickedOnSameGameObject = newPressed == eventData.lastPress && button.pressTime - eventData.clickTime <= kClickSpeed;
            if (eventData.clickCount > 0 && !button.clickedOnSameGameObject)
            {
                eventData.clickCount = default;
                eventData.clickTime = default;
            }

            // Set pointerPress. This nukes lastPress. Meaning that after OnPointerDown, lastPress will
            // become null.
            eventData.pointerPress = newPressed;
            eventData.rawPointerPress = currentOverGo;

            // Save the drag handler for drag events during this mouse down.
            eventData.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(currentOverGo);

            if (eventData.pointerDrag != null)
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.initializePotentialDrag);
        }

        // Button release.
        if (button.wasReleasedThisFrame)
        {
            // Check for click. Release must be on same GO that we pressed on and we must not
            // have moved beyond our move tolerance (doing so will set eligibleForClick to false).
            // NOTE: There's two difference to click handling here compared to StandaloneInputModule.
            //       1) StandaloneInputModule counts clicks entirely on press meaning that clickCount is increased
            //          before a click has actually happened.
            //       2) StandaloneInputModule increases click counts even if something is eventually not deemed a
            //          click and OnPointerClick is thus never invoked.
            var pointerClickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            var isClick = eventData.pointerPress == pointerClickHandler && eventData.eligibleForClick;
            if (isClick)
            {
                // Count clicks.
                if (button.clickedOnSameGameObject)
                {
                    // We re-clicked on the same UI element within 0.3 seconds so count
                    // it as a repeat click.
                    ++eventData.clickCount;
                }
                else
                {
                    // First click on this object.
                    eventData.clickCount = 1;
                }
                eventData.clickTime = InputRuntime.s_Instance.unscaledGameTime;
            }

            // Invoke OnPointerUp.
            ExecuteEvents.Execute(eventData.pointerPress, eventData, ExecuteEvents.pointerUpHandler);

            // Invoke OnPointerClick or OnDrop.
            if (isClick)
                ExecuteEvents.Execute(eventData.pointerPress, eventData, ExecuteEvents.pointerClickHandler);
            else if (eventData.dragging && eventData.pointerDrag != null)
                ExecuteEvents.ExecuteHierarchy(currentOverGo, eventData, ExecuteEvents.dropHandler);

            eventData.eligibleForClick = false;
            eventData.pointerPress = null;
            eventData.rawPointerPress = null;

            if (eventData.dragging && eventData.pointerDrag != null)
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.endDragHandler);

            eventData.dragging = false;
            eventData.pointerDrag = null;

            button.ignoreNextClick = false;
        }

        button.CopyPressStateFrom(eventData);
    }

    private void ProcessPointerButtonDrag(ref PointerModel.ButtonState button, ExtendedPointerEventData eventData)
    {
        if (!eventData.IsPointerMoving() ||
            (eventData.pointerType == UIPointerType.MouseOrPen && Cursor.lockState == CursorLockMode.Locked) ||
            eventData.pointerDrag == null)
            return;

        // Detect drags.
        if (!eventData.dragging)
        {
            if (!eventData.useDragThreshold || (eventData.pressPosition - eventData.position).sqrMagnitude >=
                (double)eventSystem.pixelDragThreshold * eventSystem.pixelDragThreshold * (eventData.pointerType == UIPointerType.Tracked
                                                                                           ? m_TrackedDeviceDragThresholdMultiplier
                                                                                           : 1))
            {
                // Started dragging. Invoke OnBeginDrag.
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.beginDragHandler);
                eventData.dragging = true;
            }
        }

        if (eventData.dragging)
        {
            // If we moved from our initial press object, process an up for that object.
            if (eventData.pointerPress != eventData.pointerDrag)
            {
                ExecuteEvents.Execute(eventData.pointerPress, eventData, ExecuteEvents.pointerUpHandler);

                eventData.eligibleForClick = false;
                eventData.pointerPress = null;
                eventData.rawPointerPress = null;
            }

            ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.dragHandler);
            button.CopyPressStateFrom(eventData);
        }
    }

    private static void ProcessPointerScroll(ref PointerModel pointer, PointerEventData eventData)
    {
        var scrollDelta = pointer.scrollDelta;
        if (!Mathf.Approximately(scrollDelta.sqrMagnitude, 0.0f))
        {
            eventData.scrollDelta = scrollDelta;
            var scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(eventData.pointerEnter);
            ExecuteEvents.ExecuteHierarchy(scrollHandler, eventData, ExecuteEvents.scrollHandler);
        }
    }

    internal void ProcessNavigation(ref NavigationModel navigationState)
    {
        var usedSelectionChange = false;
        if (eventSystem.currentSelectedGameObject != null)
        {
            var data = GetBaseEventData();
            ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, data, ExecuteEvents.updateSelectedHandler);
            usedSelectionChange = data.used;
        }

        // Don't send move events if disabled in the EventSystem.
        if (!eventSystem.sendNavigationEvents)
            return;

        // Process move.
        var movement = navigationState.move;
        if (!usedSelectionChange && (!Mathf.Approximately(movement.x, 0f) || !Mathf.Approximately(movement.y, 0f)))
        {
            var time = InputRuntime.s_Instance.unscaledGameTime;
            var moveVector = navigationState.move;

            var moveDirection = MoveDirection.None;
            if (moveVector.sqrMagnitude > 0)
            {
                if (Mathf.Abs(moveVector.x) > Mathf.Abs(moveVector.y))
                    moveDirection = moveVector.x > 0 ? MoveDirection.Right : MoveDirection.Left;
                else
                    moveDirection = moveVector.y > 0 ? MoveDirection.Up : MoveDirection.Down;
            }

            ////REVIEW: is resetting move repeats when direction changes really useful behavior?
            if (moveDirection != m_NavigationState.lastMoveDirection)
                m_NavigationState.consecutiveMoveCount = 0;

            if (moveDirection != MoveDirection.None)
            {
                var allow = true;
                if (m_NavigationState.consecutiveMoveCount != 0)
                {
                    if (m_NavigationState.consecutiveMoveCount > 1)
                        allow = time > m_NavigationState.lastMoveTime + moveRepeatRate;
                    else
                        allow = time > m_NavigationState.lastMoveTime + moveRepeatDelay;
                }

                if (allow)
                {
                    var eventData = m_NavigationState.eventData;
                    if (eventData == null)
                    {
                        eventData = new ExtendedAxisEventData(eventSystem);
                        m_NavigationState.eventData = eventData;
                    }
                    eventData.Reset();

                    eventData.moveVector = moveVector;
                    eventData.moveDir = moveDirection;

                    if (IsMoveAllowed(eventData))
                    {
                        ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, eventData, ExecuteEvents.moveHandler);
                        usedSelectionChange = eventData.used;

                        m_NavigationState.consecutiveMoveCount = m_NavigationState.consecutiveMoveCount + 1;
                        m_NavigationState.lastMoveTime = time;
                        m_NavigationState.lastMoveDirection = moveDirection;
                    }
                }
            }
            else
                m_NavigationState.consecutiveMoveCount = 0;
        }
        else
        {
            m_NavigationState.consecutiveMoveCount = 0;
        }

        // Process submit and cancel events.
        if (!usedSelectionChange && eventSystem.currentSelectedGameObject != null)
        {
            // NOTE: Whereas we use callbacks for the other actions, we rely on WasPressedThisFrame() for
            //       submit and cancel. This makes their behavior inconsistent with pointer click behavior where
            //       a click will register on button *up*, but consistent with how other UI systems work where
            //       click occurs on key press. This nuance in behavior becomes important in combination with
            //       action enable/disable changes in response to submit or cancel. We react to button *down*
            //       instead of *up*, so button *up* will come in *after* we have applied the state change.
            var submitAction = m_SubmitAction?.action;
            var cancelAction = m_CancelAction?.action;

            var data = GetBaseEventData();
            if (cancelAction != null && cancelAction.WasPressedThisFrame())
                ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, data, ExecuteEvents.cancelHandler);
            if (!data.used && submitAction != null && submitAction.WasPressedThisFrame())
                ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, data, ExecuteEvents.submitHandler);
        }
    }

    private bool IsMoveAllowed(AxisEventData eventData)
    {
        if (m_LocalMultiPlayerRoot == null)
            return true;

        if (eventSystem.currentSelectedGameObject == null)
            return true;

        var selectable = eventSystem.currentSelectedGameObject.GetComponent<Selectable>();

        if (selectable == null)
            return true;

        Selectable navigationTarget = null;
        switch (eventData.moveDir)
        {
            case MoveDirection.Right:
                navigationTarget = selectable.FindSelectableOnRight();
                break;

            case MoveDirection.Up:
                navigationTarget = selectable.FindSelectableOnUp();
                break;

            case MoveDirection.Left:
                navigationTarget = selectable.FindSelectableOnLeft();
                break;

            case MoveDirection.Down:
                navigationTarget = selectable.FindSelectableOnDown();
                break;
        }

        if (navigationTarget == null)
            return true;

        return navigationTarget.transform.IsChildOf(m_LocalMultiPlayerRoot.transform);
    }

    [FormerlySerializedAs("m_RepeatDelay")]
    [Tooltip("The Initial delay (in seconds) between an initial move action and a repeated move action.")]
    [SerializeField]
    private float m_MoveRepeatDelay = 0.5f;

    [FormerlySerializedAs("m_RepeatRate")]
    [Tooltip("The speed (in seconds) that the move action repeats itself once repeating (max 1 per frame).")]
    [SerializeField]
    private float m_MoveRepeatRate = 0.1f;

    [Tooltip("Scales the Eventsystem.DragThreshold, for tracked devices, to make selection easier.")]
    // Hide this while we still have to figure out what to do with this.
    private float m_TrackedDeviceDragThresholdMultiplier = 2.0f;

    [Tooltip("Transform representing the real world origin for tracking devices. When using the XR Interaction Toolkit, this should be pointing to the XR Rig's Transform.")]
    [SerializeField]
    private Transform m_XRTrackingOrigin;

    /// <summary>
    /// Delay in seconds between an initial move action and a repeated move action while <see cref="move"/> is actuated.
    /// </summary>
    /// <remarks>
    /// While <see cref="move"/> is being held down, the input module will first wait for <see cref="moveRepeatDelay"/> seconds
    /// after the first actuation of <see cref="move"/> and then trigger a move event every <see cref="moveRepeatRate"/> seconds.
    /// </remarks>
    /// <seealso cref="moveRepeatRate"/>
    /// <seealso cref="AxisEventData"/>
    /// <see cref="move"/>
    public float moveRepeatDelay
    {
        get => m_MoveRepeatDelay;
        set => m_MoveRepeatDelay = value;
    }

    /// <summary>
    /// Delay in seconds between repeated move actions while <see cref="move"/> is actuated.
    /// </summary>
    /// <remarks>
    /// While <see cref="move"/> is being held down, the input module will first wait for <see cref="moveRepeatDelay"/> seconds
    /// after the first actuation of <see cref="move"/> and then trigger a move event every <see cref="moveRepeatRate"/> seconds.
    ///
    /// Note that a maximum of one <see cref="AxisEventData"/> will be sent per frame. This means that even if multiple time
    /// increments of the repeat delay have passed since the last update, only one move repeat event will be generated.
    /// </remarks>
    /// <seealso cref="moveRepeatDelay"/>
    /// <seealso cref="AxisEventData"/>
    /// <see cref="move"/>
    public float moveRepeatRate
    {
        get => m_MoveRepeatRate;
        set => m_MoveRepeatRate = value;
    }

    private bool explictlyIgnoreFocus => InputSystem.settings.backgroundBehavior == InputSettings.BackgroundBehavior.IgnoreFocus;

    private bool shouldIgnoreFocus
    {
        // By default, key this on whether running the background is enabled or not. Rationale is that
        // if running in the background is enabled, we already have rules in place what kind of input
        // is allowed through and what isn't. And for the input that *IS* allowed through, the UI should
        // react.
        get => explictlyIgnoreFocus || InputRuntime.s_Instance.runInBackground;
    }

    [Obsolete("'repeatRate' has been obsoleted; use 'moveRepeatRate' instead. (UnityUpgradable) -> moveRepeatRate", false)]
    public float repeatRate
    {
        get => moveRepeatRate;
        set => moveRepeatRate = value;
    }

    [Obsolete("'repeatDelay' has been obsoleted; use 'moveRepeatDelay' instead. (UnityUpgradable) -> moveRepeatDelay", false)]
    public float repeatDelay
    {
        get => moveRepeatDelay;
        set => moveRepeatDelay = value;
    }

    /// <summary>
    /// A <see cref="Transform"/> representing the real world origin for tracking devices.
    /// This is used to convert real world positions and rotations for <see cref="UIPointerType.Tracked"/> pointers into Unity's global space.
    /// When using the XR Interaction Toolkit, this should be pointing to the XR Rig's Transform.
    /// </summary>
    /// <remarks>This will transform all tracked pointers. If unset, or set to null, the Unity world origin will be used as the basis for all tracked positions and rotations.</remarks>
    public Transform xrTrackingOrigin
    {
        get => m_XRTrackingOrigin;
        set => m_XRTrackingOrigin = value;
    }

    /// <summary>
    /// Scales the drag threshold of <c>EventSystem</c> for tracked devices to make selection easier.
    /// </summary>
    public float trackedDeviceDragThresholdMultiplier
    {
        get => m_TrackedDeviceDragThresholdMultiplier;
        set => m_TrackedDeviceDragThresholdMultiplier = value;
    }

    private void SwapAction(ref InputActionReference property, InputActionReference newValue, bool actionsHooked, Action<InputAction.CallbackContext> actionCallback)
    {
        if (property == newValue || (property != null && newValue != null && property.action == newValue.action))
            return;

        if (property != null && actionCallback != null && actionsHooked)
        {
            property.action.performed -= actionCallback;
            property.action.canceled -= actionCallback;
        }

        var oldActionNull = property?.action == null;
        var oldActionEnabled = property?.action != null && property.action.enabled;

        TryDisableInputAction(property);
        property = newValue;


        if (newValue?.action != null && actionCallback != null && actionsHooked)
        {
            property.action.performed += actionCallback;
            property.action.canceled += actionCallback;
        }

        if (isActiveAndEnabled && newValue?.action != null && (oldActionEnabled || oldActionNull))
            EnableInputAction(property);
    }

    protected override void Awake()
    {
        base.Awake();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
    }

    protected override void OnDisable()
    {

        base.OnDisable();
    }
    private void EnableInputAction(InputActionReference inputActionReference)
    {
        var action = inputActionReference?.action;
        if (action == null)
            return;

        if (s_InputActionReferenceCounts.TryGetValue(action, out var referenceState))
        {
            referenceState.refCount++;
            s_InputActionReferenceCounts[action] = referenceState;
        }
        else
        {
            // if the action is already enabled but its reference count is zero then it was enabled by
            // something outside the input module and the input module should never disable it.
            referenceState = new InputActionReferenceState { refCount = 1, enabledByInputModule = !action.enabled };
            s_InputActionReferenceCounts.Add(action, referenceState);
        }

        action.Enable();
    }

    private void TryDisableInputAction(InputActionReference inputActionReference, bool isComponentDisabling = false)
    {
        var action = inputActionReference?.action;
        if (action == null)
            return;

        // Don't decrement refCount when we were not responsible for incrementing it.
        // I.e. when we were not enabled yet. When OnDisabled is called, isActiveAndEnabled will
        // already have been set to false. In that case we pass isComponentDisabling to check if we
        // came from OnDisabled and therefore need to allow disabling.
        if (!isActiveAndEnabled && !isComponentDisabling)
            return;

        if (!s_InputActionReferenceCounts.TryGetValue(action, out var referenceState))
            return;

        if (referenceState.refCount - 1 == 0 && referenceState.enabledByInputModule)
        {
            action.Disable();
            s_InputActionReferenceCounts.Remove(action);
            return;
        }

        referenceState.refCount--;
        s_InputActionReferenceCounts[action] = referenceState;
    }

    private int GetPointerStateIndexFor(int pointerOrTouchId)
    {
        if (pointerOrTouchId == m_CurrentPointerId)
            return m_CurrentPointerIndex;

        for (var i = 0; i < m_PointerIds.length; ++i)
            if (m_PointerIds[i] == pointerOrTouchId)
                return i;

        // Search for Device or Touch Ids as a fallback
        for (var i = 0; i < m_PointerStates.length; ++i)
        {
            var eventData = m_PointerStates[i].eventData;
            if (eventData.touchId == pointerOrTouchId || (eventData.touchId != 0 && eventData.device.deviceId == pointerOrTouchId))
                return i;
        }

        return -1;
    }

    private ref PointerModel GetPointerStateForIndex(int index)
    {
        if (index == 0)
            return ref m_PointerStates.firstValue;
        return ref m_PointerStates.additionalValues[index - 1];
    }

    private int GetPointerStateIndexFor(ref InputAction.CallbackContext context)
    {
        if (CheckForRemovedDevice(ref context))
            return -1;

        var phase = context.phase;
        return GetPointerStateIndexFor(context.control, createIfNotExists: phase != InputActionPhase.Canceled);
    }
    private int GetPointerStateIndexFor(InputControl control, bool createIfNotExists = true)
    {
        Debug.Assert(control != null, "Control must not be null");

        var device = control.device;
        var controlParent = control.parent;
        var touchControlIndex = m_PointerTouchControls.IndexOfReference(controlParent);
        if (touchControlIndex != -1)
        {
            // For touches, we cache a reference to the control of a pointer so that we don't
            // have to continuously do ReadValue() on the touch ID control.
            m_CurrentPointerId = m_PointerIds[touchControlIndex];
            m_CurrentPointerIndex = touchControlIndex;
            m_CurrentPointerType = UIPointerType.Touch;

            return touchControlIndex;
        }

        var pointerId = device.deviceId;
        var touchId = 0;
        var touchPosition = Vector2.zero;

        // Need to check if it's a touch so that we get a correct pointerId.
        if (controlParent is TouchControl touchControl)
        {
            touchId = touchControl.touchId.value;
            touchPosition = touchControl.position.value;
        }
        // Could be it's a toplevel control on Touchscreen (like "<Touchscreen>/position"). In that case,
        // read the touch ID from primaryTouch.
        else if (controlParent is Touchscreen touchscreen)
        {
            touchId = touchscreen.primaryTouch.touchId.value;
            touchPosition = touchscreen.primaryTouch.position.value;
        }

        int displayIndex = GetDisplayIndexFor(control);

        if (touchId != 0)
            pointerId = ExtendedPointerEventData.MakePointerIdForTouch(pointerId, touchId);

        // Early out if it's the last used pointer.
        // NOTE: Can't just compare by device here because of touchscreens potentially having multiple associated pointers.
        if (m_CurrentPointerId == pointerId)
            return m_CurrentPointerIndex;

        // Search m_PointerIds for an existing entry.
        // NOTE: This is a linear search but m_PointerIds is only IDs and the number of concurrent pointers
        //       should be very low at any one point (in fact, we don't generally expect to have more than one
        //       which is why we are using InlinedArrays).
        if (touchId == 0) // Not necessary for touches; see above.
        {
            for (var i = 0; i < m_PointerIds.length; i++)
            {
                if (m_PointerIds[i] == pointerId)
                {
                    // Existing entry found. Make it the current pointer.
                    m_CurrentPointerId = pointerId;
                    m_CurrentPointerIndex = i;
                    m_CurrentPointerType = m_PointerStates[i].pointerType;
                    return i;
                }
            }
        }

        if (!createIfNotExists)
            return -1;

        // Determine pointer type.
        var pointerType = UIPointerType.None;
        if (touchId != 0)
            pointerType = UIPointerType.Touch;
        else if (HaveControlForDevice(device, point))
            pointerType = UIPointerType.MouseOrPen;
        else if (HaveControlForDevice(device, trackedDevicePosition))
            pointerType = UIPointerType.Tracked;

        ////REVIEW: For touch, probably makes sense to force-ignore any input other than from primaryTouch.
        // If the behavior is SingleUnifiedPointer, we only ever create a single pointer state
        // and use that for all pointer input that is coming in.
        if ((m_PointerBehavior == UIPointerBehavior.SingleUnifiedPointer && pointerType != UIPointerType.None) ||
            (m_PointerBehavior == UIPointerBehavior.SingleMouseOrPenButMultiTouchAndTrack && pointerType == UIPointerType.MouseOrPen))
        {
            if (m_CurrentPointerIndex == -1)
            {
                m_CurrentPointerIndex = AllocatePointer(pointerId, displayIndex, touchId, pointerType, control, device, touchId != 0 ? controlParent : null);
            }
            else
            {
                // Update pointer record to reflect current device. We know they're different because we checked
                // m_CurrentPointerId earlier in the method.
                // NOTE: This path may repeatedly switch the pointer type and ID on the same single event instance.

                ref var pointer = ref GetPointerStateForIndex(m_CurrentPointerIndex);

                var eventData = pointer.eventData;
                eventData.control = control;
                eventData.device = device;
                eventData.pointerType = pointerType;
                eventData.pointerId = pointerId;
                eventData.touchId = touchId;
                eventData.displayIndex = displayIndex;

                // Make sure these don't linger around when we switch to a different kind of pointer.
                eventData.trackedDeviceOrientation = default;
                eventData.trackedDevicePosition = default;
            }

            if (pointerType == UIPointerType.Touch)
                GetPointerStateForIndex(m_CurrentPointerIndex).screenPosition = touchPosition;

            m_CurrentPointerId = pointerId;
            m_CurrentPointerType = pointerType;

            return m_CurrentPointerIndex;
        }

        // No existing record for the device. Find out if the device has the ability to point at all.
        // If not, we need to use a pointer state from a different device (if present).
        var index = -1;
        if (pointerType != UIPointerType.None)
        {
            // Device has an associated position input. Create a new pointer record.
            index = AllocatePointer(pointerId, displayIndex, touchId, pointerType, control, device, touchId != 0 ? controlParent : null);
        }
        else
        {
            // Device has no associated position input. Find a pointer device to route the change into.
            // As a last resort, create a pointer without a position input.

            // If we have a current pointer, route the input into that. The majority of times we end
            // up in this branch, this should settle things.
            if (m_CurrentPointerId != -1)
                return m_CurrentPointerIndex;

            // NOTE: In most cases, we end up here when there is input on a non-pointer device bound to one of the pointer-related
            //       actions before there is input from a pointer device. In this scenario, we don't have a pointer state allocated
            //       for the device yet.

            // If we have anything bound to the `point` action, create a pointer for it.
            var pointControls = point?.action?.controls;
            var pointerDevice = pointControls.HasValue && pointControls.Value.Count > 0 ? pointControls.Value[0].device : null;
            if (pointerDevice != null && !(pointerDevice is Touchscreen)) // Touchscreen only temporarily allocate pointer states.
            {
                // Create MouseOrPen style pointer.
                index = AllocatePointer(pointerDevice.deviceId, displayIndex, 0, UIPointerType.MouseOrPen, pointControls.Value[0], pointerDevice);
            }
            else
            {
                // Do the same but look at the `position` action.
                var positionControls = trackedDevicePosition?.action?.controls;
                var trackedDevice = positionControls.HasValue && positionControls.Value.Count > 0
                    ? positionControls.Value[0].device
                    : null;
                if (trackedDevice != null)
                {
                    // Create a Tracked style pointer.
                    index = AllocatePointer(trackedDevice.deviceId, displayIndex, 0, UIPointerType.Tracked, positionControls.Value[0], trackedDevice);
                }
                else
                {
                    // We got input from a non-pointer device and apparently there's no pointer we can route the
                    // input into. Just create a pointer state for the device and leave it at that.
                    index = AllocatePointer(pointerId, displayIndex, 0, UIPointerType.None, control, device);
                }
            }
        }

        if (pointerType == UIPointerType.Touch)
            GetPointerStateForIndex(index).screenPosition = touchPosition;

        m_CurrentPointerId = pointerId;
        m_CurrentPointerIndex = index;
        m_CurrentPointerType = pointerType;

        return index;
    }

    // Remove any pointer that no longer has the ability to point.
    private void PurgeStalePointers()
    {
        for (var i = 0; i < m_PointerStates.length; ++i)
        {
            ref var state = ref GetPointerStateForIndex(i);
            var device = state.eventData.device;
            if (!device.added || // Check if device was removed altogether.
                (!HaveControlForDevice(device, point) &&
                 !HaveControlForDevice(device, trackedDevicePosition) &&
                 !HaveControlForDevice(device, trackedDeviceOrientation)))
            {
                SendPointerExitEventsAndRemovePointer(i);
                --i;
            }
        }

        m_NeedToPurgeStalePointers = false;
    }
    internal const float kPixelPerLine = 20;

    private void FilterPointerStatesByType()
    {
        var pointerTypeToProcess = UIPointerType.None;
        // Read all pointers device states
        // Find first pointer that has changed this frame to be processed later
        for (var i = 0; i < m_PointerStates.length; ++i)
        {
            ref var state = ref GetPointerStateForIndex(i);
            state.eventData.ReadDeviceState();
            state.CopyTouchOrPenStateFrom(state.eventData);
            if (state.changedThisFrame && pointerTypeToProcess == UIPointerType.None)
                pointerTypeToProcess = state.pointerType;
        }

        // For SingleMouseOrPenButMultiTouchAndTrack, we keep a single pointer for mouse and pen but only for as
        // long as there is no touch or tracked input. If we get that kind, we remove the mouse/pen pointer.
        if (m_PointerBehavior == UIPointerBehavior.SingleMouseOrPenButMultiTouchAndTrack && pointerTypeToProcess != UIPointerType.None)
        {
            // var pointerTypeToProcess = m_PointerStates.firstValue.pointerType;
            if (pointerTypeToProcess == UIPointerType.MouseOrPen)
            {
                // We have input on a mouse or pen. Kill all touch and tracked pointers we may have.
                for (var i = 0; i < m_PointerStates.length; ++i)
                {
                    if (m_PointerStates[i].pointerType != UIPointerType.MouseOrPen)
                    {
                        SendPointerExitEventsAndRemovePointer(i);
                        --i;
                    }
                }
            }
            else
            {
                // We have touch or tracked input. Kill mouse/pen pointer, if we have it.
                for (var i = 0; i < m_PointerStates.length; ++i)
                {
                    if (m_PointerStates[i].pointerType == UIPointerType.MouseOrPen)
                    {
                        SendPointerExitEventsAndRemovePointer(i);
                        --i;
                    }
                }
            }
        }
    }

    public override void Process()
    {
        if (m_NeedToPurgeStalePointers)
            PurgeStalePointers();

        // Reset devices of changes since we don't want to spool up changes once we gain focus.
        if (!eventSystem.isFocused && !shouldIgnoreFocus)
        {
            for (var i = 0; i < m_PointerStates.length; ++i)
                m_PointerStates[i].OnFrameFinished();
        }
        else
        {
            // Navigation input.
            ProcessNavigation(ref m_NavigationState);

            FilterPointerStatesByType();

            // Pointer input.
            for (var i = 0; i < m_PointerStates.length; i++)
            {
                ref var state = ref GetPointerStateForIndex(i);

                ProcessPointer(ref state);

                // If it's a touch and the touch has ended, release the pointer state.
                // NOTE: We defer this by one frame such that OnPointerUp happens in the frame of release
                //       and OnPointerExit happens one frame later. This is so that IsPointerOverGameObject()
                //       stays true for the touch in the frame of release (see UI_TouchPointersAreKeptForOneFrameAfterRelease).
                if (state.pointerType == UIPointerType.Touch && !state.leftButton.isPressed && !state.leftButton.wasReleasedThisFrame)
                {
                    RemovePointerAtIndex(i);
                    --i;
                    continue;
                }

                state.OnFrameFinished();
            }
        }
    }
    public override int ConvertUIToolkitPointerId(PointerEventData sourcePointerData)
    {

        return sourcePointerData is ExtendedPointerEventData ep
            ? ep.uiToolkitPointerId
            : base.ConvertUIToolkitPointerId(sourcePointerData);
    }
}
 */