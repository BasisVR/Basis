using UnityEngine.InputSystem;

namespace Basis.Scripts.Device_Management.Devices.UnityInputSystem
{
    [System.Serializable]
    public struct BasisOpenxrDeviceTrackedInfo
    {
        public string layoutName;
        public InputActionProperty State;
        public InputDevice device;
        public string usage;
        public int IsActive;
    }
}
