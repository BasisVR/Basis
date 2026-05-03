using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using static BundledContentHolder;
namespace Basis.Scripts.Addressable_Driver.Resource
{
    public static class AddressableResourceProcess
    {
        public static async Task<GameObject> LoadAsGameObjectsAsync(GameObject TempSpawnDisableGameobject,string loadstring,InstantiationParameters instantiationParameters, ChecksRequired Required, Selector Selector, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> data = Addressables.LoadAssetAsync<GameObject>(loadstring);

            object result = await data.Task;

            if (result is GameObject resource)
            {
                GameObject spawned = ContentPoliceControl.ContentControl(TempSpawnDisableGameobject, resource, Required, instantiationParameters.Position, instantiationParameters.Rotation, false, Vector3.zero, Selector, instantiationParameters.Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop);
                return spawned;
            }
            else
            {
                UnityEngine.Debug.LogError("Unexpected result type: " + result.GetType());
                Addressables.Release(data);
            }
            return null;
        }
        /// <summary>
        /// loads a system based gameobject,
        /// use this to get around loading things with required checks.
        /// </summary>
        /// <param name="loadstring"></param>
        /// <param name="InstantiationParameters"></param>
        /// <returns></returns>
        public static async Task<GameObject> LoadSystemGameobject(GameObject TempSpawnDisableGameobject, string loadstring, InstantiationParameters InstantiationParameters)
        {
            ChecksRequired Required = new ChecksRequired(false, false, false,false);
            GameObject data = await AddressableResourceProcess.LoadAsGameObjectsAsync(TempSpawnDisableGameobject, loadstring, InstantiationParameters, Required, BundledContentHolder.Selector.System);
            return data;
        }
        public static void ReleaseGameobject(GameObject Reference)
        {
            if (Reference != null)
            {
                Addressables.ReleaseInstance(Reference);
                if (Reference != null)
                {
                    GameObject.Destroy(Reference);
                }
            }
        }
    }
}
