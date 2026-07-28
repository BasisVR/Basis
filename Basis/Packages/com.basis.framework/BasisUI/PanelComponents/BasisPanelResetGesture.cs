using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine.InputSystem;

namespace Basis.BasisUI
{
    /// <summary>
    /// Reset-to-default gesture for panel elements: while hovering a control, a right-click
    /// (desktop) or a thumbstick click (VR) asks to reset it. Both are polled rather than handled
    /// as pointer buttons, because <c>BasisUIInput</c> never fills its right-button state (so a
    /// right-click produces no UI pointer event at all) and a thumbstick click is not a pointer
    /// button in the first place.
    ///
    /// Only one element can be hovered at a time, so the poll lives here as a single frame-clock
    /// subscription held for the duration of that hover, instead of an Update on every control.
    /// </summary>
    public static class BasisPanelResetGesture
    {
        private static PanelComponent _hovered;
        private static bool _wasDown;

        public static void SetHovered(PanelComponent component)
        {
            if (component == null || _hovered == component) return;

            bool wasIdle = _hovered == null;
            _hovered = component;
            // Seed the edge state so a button already held on hover doesn't instantly fire;
            // only a fresh press after pointing at the control counts.
            _wasDown = IsInputDown();

            if (wasIdle)
            {
                BasisFrameClock.OnTick += Tick;
                BasisFrameClock.AddRequest();
            }
        }

        public static void ClearHovered(PanelComponent component)
        {
            if (_hovered != component) return;
            Release();
        }

        private static void Release()
        {
            _hovered = null;
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

            bool down = IsInputDown();
            bool rising = down && !_wasDown;
            _wasDown = down;

            if (rising && _hovered.HasResetDefault && _hovered.IsInteractable)
            {
                _hovered.RequestReset();
            }
        }

        /// <summary>True this frame if a reset gesture is held: right mouse button, or a hand thumbstick/trackpad click.</summary>
        private static bool IsInputDown()
        {
            if (Mouse.current != null && Mouse.current.rightButton.isPressed) return true;
            return AnyHandThumbstickClick();
        }

        /// <summary>True if either hand controller has its thumbstick (or trackpad) pressed in this frame.</summary>
        private static bool AnyHandThumbstickClick()
        {
            BasisDeviceManagement device = BasisDeviceManagement.Instance;
            if (device == null) return false;

            BasisObservableList<BasisInput> inputs = device.AllInputDevices;
            for (int Index = 0; Index < inputs.Count; Index++)
            {
                BasisInput input = inputs[Index];
                if (input == null || !input.TryGetRole(out BasisBoneTrackedRole role)) continue;
                if (role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand) continue;
                if (input.CurrentInputState.Primary2DAxisClick || input.CurrentInputState.Secondary2DAxisClick) return true;
            }
            return false;
        }
    }
}
