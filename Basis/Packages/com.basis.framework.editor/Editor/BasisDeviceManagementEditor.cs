using System.Threading.Tasks;
using UnityEditor;

namespace Basis.Scripts.Device_Management.Editor
{
    public static class BasisDeviceManagementEditor
    {
        [MenuItem("Basis/Debug/Force Load XR")]
        public static async Task ForceLoadXR()
        {
          await  BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
        }
        [MenuItem("Basis/Debug/Force Set Desktop")]
        public static async Task ForceSetDesktop()
        {
          await  BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
        }
    }
}
