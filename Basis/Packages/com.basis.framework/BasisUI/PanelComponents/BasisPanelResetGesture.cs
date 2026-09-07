using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Basis.BasisUI
{
    /// <summary>
    /// Options gesture for panel elements: while hovering a control, a right-click (desktop) or a
    /// thumbstick click (VR) opens what can be done with it — a reset to default, and for a slider
    /// the offer of this hand's thumbstick. Both are polled rather than handled
    /// as pointer buttons, because <c>BasisUIInput</c> never fills its right-button state (so a
    /// right-click produces no UI pointer event at all) and a thumbstick click is not a pointer
    /// button in the first place.
    ///
    /// <para>
    /// Only the device whose pointer is on the control is listened to. The other hand's stick is
    /// that hand's own pointer, and far more often it is walking or snap-turning; a control under
    /// one ray must not answer the stick of the hand doing something else entirely.
    /// </para>
    ///
    /// <para>
    /// It is a click, not a press: the window opens on release, and only when the press both started
    /// and ended on the same control with the same device, within <see cref="MaxClickHoldSeconds"/>.
    /// A stick pushed and held — locomotion, or a panel drag — is not a click and never opens one.
    /// </para>
    ///
    /// Only one element can be hovered at a time, so the poll lives here as a single frame-clock
    /// subscription held for the duration of that hover, instead of an Update on every control.
    /// </summary>
    public static class BasisPanelResetGesture
    {
        /// <summary>
        /// Longest a press can be held and still read as a click. Past this the stick is being
        /// used for something else and the press is abandoned rather than completed on release.
        /// </summary>
        private const float MaxClickHoldSeconds = 0.75f;

        private static PanelComponent _hovered;

        /// <summary>The device pointing at <see cref="_hovered"/>. Null when the pointer could not be traced back to one.</summary>
        private static BasisInput _hoveredInput;

        private static bool _wasDown;
        private static bool _pressArmed;
        private static float _pressStartTime;

        /// <summary>
        /// The hand whose gesture is opening an options window right now. Set only for the duration
        /// of the <see cref="PanelComponent.RequestReset"/> call, so a window can offer things that
        /// belong to a hand — a thumbstick bind — and offer them to the hand that asked. Null on
        /// desktop, and for any pointer that could not be traced back to a device.
        /// </summary>
        public static BasisInput GestureDevice { get; private set; }

        /// <summary>
        /// One-line reminder of the gesture, for the hover tooltip of any control that offers it.
        /// Worded for the pointer the player is holding, since the two are nothing alike.
        /// </summary>
        public static string HintText => BasisDeviceManagement.IsUserInDesktop()
            ? BasisLocalization.Get("ui.resetValue.hint.desktop")
            : BasisLocalization.Get("ui.resetValue.hint.vr");

        /// <summary>
        /// Takes the hover, along with the pointer event that carried it — that event identifies
        /// which device is pointing, and so which stick this control will answer.
        /// </summary>
        public static void SetHovered(PanelComponent component, PointerEventData eventData)
        {
            if (component == null) return;

            BasisInput input = ResolvePointerDevice(eventData);
            if (_hovered == component && ReferenceEquals(_hoveredInput, input)) return;

            bool wasIdle = _hovered == null;
            _hovered = component;
            _hoveredInput = input;

            // Seed the edge state so a stick already held on hover doesn't complete a click here;
            // only a press made after pointing at the control counts.
            _wasDown = IsInputDown(input);
            _pressArmed = false;

            if (wasIdle)
            {
                BasisFrameClock.OnTick += Tick;
                BasisFrameClock.AddRequest();
            }
        }

        /// <summary>
        /// Drops the hover. A second pointer leaving a control the tracked device is still on is
        /// ignored, so one hand's exit cannot disarm the hand actually pointing.
        /// </summary>
        public static void ClearHovered(PanelComponent component, PointerEventData eventData = null)
        {
            if (_hovered != component) return;
            if (eventData != null && !ReferenceEquals(ResolvePointerDevice(eventData), _hoveredInput)) return;
            Release();
        }

        private static void Release()
        {
            _hovered = null;
            _hoveredInput = null;
            _pressArmed = false;
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
        }

        private static void Tick()
        {
            if (_hovered == null)
            {
                Release();
                return;
            }

            bool down = IsInputDown(_hoveredInput);
            bool wasDown = _wasDown;
            _wasDown = down;

            bool eligible = _hovered.HasPanelOptions && _hovered.IsInteractable;

            if (down && !wasDown)
            {
                // Arm on the press rather than testing only at release, so a press that began on a
                // control that could not be reset cannot complete as one if that changes mid-hold.
                _pressArmed = eligible;
                _pressStartTime = Time.unscaledTime;
                return;
            }

            if (!down && wasDown)
            {
                bool armed = _pressArmed;
                _pressArmed = false;

                if (armed && eligible && Time.unscaledTime - _pressStartTime <= MaxClickHoldSeconds)
                {
                    // Cleared however the call leaves: a device left standing here would later hand
                    // a bind to whichever hand happened to gesture last.
                    GestureDevice = _hoveredInput;
                    try
                    {
                        _hovered.RequestReset();
                    }
                    finally
                    {
                        GestureDevice = null;
                    }
                }
            }
        }

        /// <summary>True this frame if <paramref name="input"/> is holding its reset gesture.</summary>
        private static bool IsInputDown(BasisInput input)
        {
            if (input != null && input.TryGetRole(out BasisBoneTrackedRole role) &&
                (role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand))
            {
                return input.CurrentInputState.Primary2DAxisClick || input.CurrentInputState.Secondary2DAxisClick;
            }

            // Desktop, and any pointer we could not trace to a hand. Read the mouse directly rather
            // than the device's input state: the desktop eye fills Secondary2DAxisClick from the
            // scroll-wheel click, which is a different button and not this gesture.
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
        }

        /// <summary>
        /// Finds which device a pointer event came from. Every raycasting device owns exactly one
        /// persistent <see cref="Basis.Scripts.UI.BasisPointerEventData"/>, so the instance that
        /// arrived with the event identifies the hand, its ray, and its thumbstick.
        /// </summary>
        public static BasisInput ResolvePointerDevice(PointerEventData eventData)
        {
            if (eventData == null) return null;

            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null) return null;

            BasisObservableList<BasisInput> devices = management.AllInputDevices;
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = devices[Index];
                if (input != null && input.HasRaycaster && input.BasisUIRaycast != null &&
                    ReferenceEquals(input.BasisUIRaycast.CurrentEventData, eventData))
                {
                    return input;
                }
            }
            return null;
        }
    }
}
