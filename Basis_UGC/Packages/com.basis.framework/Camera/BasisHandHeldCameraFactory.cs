using Basis.Scripts.Addressable_Driver.Resource;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

public static class BasisHandHeldCameraFactory
{
    public static async Task<BasisHandHeldCamera> CreateCamera(InstantiationParameters InstantiationParameters)
    {
        ChecksRequired Required = new ChecksRequired();
        Required.UseContentRemoval = false;
        var data = await AddressableResourceProcess.LoadAsGameObjectsAsync("Packages/com.basis.sdk/Prefabs/UI/Player Held Camera.prefab", InstantiationParameters, Required, BundledContentHolder.Selector.System);
        List<GameObject> Gameobjects = data.Item1;
        if (Gameobjects.Count != 0)
        {
            foreach (GameObject gameObject in Gameobjects)
            {
                if (gameObject.TryGetComponent(out BasisHandHeldCamera Camera))
                {
                    return Camera;
                }
            }
        }
        else
        {
            BasisDebug.LogError("Missing ");
        }
        BasisDebug.LogError("Error Missing Player!");
        return null;
    }
}
