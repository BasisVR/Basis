using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine.InputSystem;
using BasisInputKey = Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.BasisInputKey;

namespace Basis.Scripts.Device_Management.Devices
{
    public static class BasisInputDisplayNames
    {
        public static string Get(BasisInput device, BasisInputKey key) => TryGet(device, key, out string name) ? name : Generic(key);
        public static string Get(BasisBoneTrackedRole role, BasisInputKey key) => TryGet(role, key, out string name) ? name : Generic(key);
        public static bool TryGet(BasisInput device, BasisInputKey key, out string name)
        {
            name = null;
            if (device == null) { return false; }
            try { if (device.TryGetInputDisplayName(key, out name) && !string.IsNullOrWhiteSpace(name)) { name = name.Trim(); return true; } }
            catch (System.Exception e) { BasisDebug.LogWarning($"Input display name lookup failed on {device.name}: {e.Message}", BasisDebug.LogTag.Input); }
            name = null;
            return false;
        }
        public static bool TryGet(BasisBoneTrackedRole role, BasisInputKey key, out string name)
        {
            name = null;
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null) { return false; }
            var devices = management.AllInputDevices;
            for (int index = 0; index < devices.Count; index++)
            {
                BasisInput device = devices[index];
                if (device != null && device.TryGetRole(out BasisBoneTrackedRole deviceRole) && deviceRole == role && TryGet(device, key, out name)) { return true; }
            }
            return false;
        }
        public static bool TryGetHands(BasisInputKey key, bool left, bool right, out string name)
        {
            string leftName = null, rightName = null;
            bool hasLeft = left && TryGet(BasisBoneTrackedRole.LeftHand, key, out leftName);
            bool hasRight = right && TryGet(BasisBoneTrackedRole.RightHand, key, out rightName);
            name = hasLeft && hasRight && leftName != rightName ? leftName + " / " + rightName : hasLeft ? leftName : hasRight ? rightName : null;
            return name != null;
        }
        public static string FromControls(InputAction action)
        {
            if (action == null) { return null; }
            InputControl control = action.activeControl;
            if (control == null && action.controls.Count > 0) { control = action.controls[0]; }
            if (control == null) { return null; }
            return string.IsNullOrWhiteSpace(control.displayName) ? control.shortDisplayName : control.displayName;
        }
        public static string Generic(BasisInputKey key)
        {
            switch (key)
            {
                case BasisInputKey.Trigger: return "Trigger";
                case BasisInputKey.SecondaryTrigger: return "Grip";
                case BasisInputKey.Primary2DAxis: return "Thumbstick";
                case BasisInputKey.Secondary2DAxis: return "Touchpad";
                case BasisInputKey.Primary2DAxisClick: return "Thumbstick Click";
                case BasisInputKey.Secondary2DAxisClick: return "Touchpad Click";
                case BasisInputKey.SecondaryButtonGetState: return "Secondary Button";
                case BasisInputKey.PrimaryButtonGetState: return "Primary Button";
                case BasisInputKey.SystemOrMenuButton: return "Menu";
                case BasisInputKey.GripButton: return "Grip";
                case BasisInputKey.PrimaryButtonTouch: return "Primary Button Touch";
                case BasisInputKey.SecondaryButtonTouch: return "Secondary Button Touch";
                case BasisInputKey.Primary2DAxisTouch: return "Thumbstick Touch";
                case BasisInputKey.Secondary2DAxisTouch: return "Touchpad Touch";
                case BasisInputKey.TriggerTouch: return "Trigger Touch";
                case BasisInputKey.ThumbrestTouch: return "Thumbrest Touch";
                default: return key.ToString();
            }
        }
    }
}
