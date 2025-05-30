using Basis.Scripts.Addressable_Driver.Resource;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
public static class BasisPersonalMirrorFactory
{
    public static async Task<BasisPersonalMirror> CreateMirror(InstantiationParameters InstantiationParameters, string Path = "Packages/com.basis.sdk/Prefabs/UI/Personal Mirror Prefab/PersonalMirror.prefab")
    {
        ChecksRequired Required = new ChecksRequired();
        Required.UseContentRemoval = false;
        var data = await AddressableResourceProcess.LoadAsGameObjectsAsync("Packages/com.basis.sdk/Prefabs/UI/Personal Mirror Prefab/PersonalMirror.prefab", InstantiationParameters, Required, BundledContentHolder.Selector.System);
        List<GameObject> Gameobjects = data.Item1;
        if (Gameobjects.Count != 0)
        {
            foreach (GameObject gameObject in Gameobjects)
            {
                if (gameObject.TryGetComponent(out BasisPersonalMirror Mirror))
                {
                    return Mirror;
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
