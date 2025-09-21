using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.Scripts.UI.UI_Panels
{
    public abstract class BasisUIBase : MonoBehaviour
    {
        public abstract void InitalizeEvent();
        public abstract void DestroyEvent();
        public void CloseThisMenu()
        {
            BasisUIManagement.RemoveUI(this);
            DestroyEvent();

            Addressables.ReleaseInstance(this.gameObject);
            Destroy(this.gameObject);
        }
        public static void OpenThisMenu(string resource)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.InstantiateAsync(resource, BasisDeviceManagement.Instance.transform, true);
            GameObject RAC = op.WaitForCompletion();
            BasisUIBase BasisUIBase = BasisHelpers.GetOrAddComponent<BasisUIBase>(RAC);
            BasisUIManagement.AddUI(BasisUIBase);
            BasisUIBase.InitalizeEvent();
        }
        public static BasisUIBase OpenMenuNow(string resource)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.InstantiateAsync(resource, BasisDeviceManagement.Instance.transform, true);
            GameObject RAC = op.WaitForCompletion();
            BasisUIBase BasisUIBase = BasisHelpers.GetOrAddComponent<BasisUIBase>(RAC);
            BasisUIManagement.AddUI(BasisUIBase);
            BasisUIBase.InitalizeEvent();
            return BasisUIBase;
        }
    }
}
