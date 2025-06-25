using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    public class SwitchActiveMode : MonoBehaviour
    {
        public UnityEngine.UI.Button VRButton;
        public UnityEngine.UI.Button DesktopButton;
        public void Start()
        {
            VRButton.onClick.AddListener(OpenVRLoader);
            DesktopButton.onClick.AddListener(Desktop);
        }
        public void Desktop()
        {
            BasisDeviceManagement.Instance.SwitchMode(BasisDeviceManagement.Desktop);
        }
        public void OpenVRLoader()
        {
            BasisDeviceManagement.Instance.SwitchMode("OpenVRLoader");
        }
    }
}
