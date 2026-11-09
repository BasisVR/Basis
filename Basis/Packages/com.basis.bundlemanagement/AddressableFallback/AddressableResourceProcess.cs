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
        public static async Task<GameObject> LoadAsGameObjectsAsync(GameObject TempSpawnDisableGameobject,string loadstring,InstantiationParameters instantiationParameters, ChecksRequired Required, Selector Selector, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null, int RendererLayer = -1)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> data = Addressables.LoadAssetAsync<GameObject>(loadstring);

            object result = await data.Task;

            if (result is GameObject resource)
            {
                GameObject spawned = ContentPoliceControl.ContentControl(TempSpawnDisableGameobject, resource, Required, instantiationParameters.Position, instantiationParameters.Rotation, false, Vector3.zero, Selector, instantiationParameters.Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, null, null, RendererLayer);
                return spawned;
            }

            // A failed load (no addressable location for the key, or a wrong asset type) completes
            // with a null result, so the old "Unexpected result type: " + result.GetType() dereferenced
            // null and threw a misleading NullReferenceException here instead of surfacing the real
            // cause. Release the handle and throw a clear message; callers such as
            // BasisAvatarFactory.LoadAvatarRemote catch this to run their fallback-avatar path.
            string reason = result == null
                ? (data.OperationException?.Message ?? "no addressable location for key")
                : "unexpected result type " + result.GetType();
            if (data.IsValid())
            {
                Addressables.Release(data);
            }
            throw new System.Exception($"Failed to load '{loadstring}' as a GameObject: {reason}");
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
