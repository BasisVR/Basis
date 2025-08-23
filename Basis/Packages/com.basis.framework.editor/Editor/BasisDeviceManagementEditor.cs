using System.Threading.Tasks;
using UnityEditor;

namespace Basis.Scripts.Device_Management.Editor
{
    public static class BasisDeviceManagementEditor
    {
        [MenuItem("Basis/ForceLoadXR")]
        public static async Task ForceLoadXR()
        {
          await  BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
        }
        [MenuItem("Basis/ForceSetDesktop")]
        public static async Task ForceSetDesktop()
        {
          await  BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
        }
    }
}
