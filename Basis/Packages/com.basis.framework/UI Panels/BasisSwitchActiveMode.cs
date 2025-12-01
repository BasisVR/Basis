using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisSwitchActiveMode : MonoBehaviour
    {
        public UnityEngine.UI.Button VRButton;
        public UnityEngine.UI.Button DesktopButton;
        public void Start()
        {
            VRButton.onClick.AddListener(OpenVRLoader);
            DesktopButton.onClick.AddListener(Desktop);
        }
        public async void Desktop()
        {
            await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
        }
        public async void OpenVRLoader()
        {
            await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
        }
    }
}
