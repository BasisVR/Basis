using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.Addressable_Driver.Factory;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using static BundledContentHolder;

namespace Basis.Scripts.Addressable_Driver.Resource
{
public static class AddressableResourceProcess
{
    public static List<SceneInstance> LoadAsSceneInstances(AddressableLoadResourceBase LoadRequest)
    {
        List<SceneInstance> Instantiated = new List<SceneInstance>();
        int Count = LoadRequest.Handles.Count;
        for (int ReleaseHandleIndex = 0; ReleaseHandleIndex < Count; ReleaseHandleIndex++)
        {
            SceneInstance SceneInstance = (SceneInstance)LoadRequest.Handles[ReleaseHandleIndex].Task.Result;
            Instantiated.Add(SceneInstance);
        }
        return Instantiated;
    }
    public static async Task<List<GameObject>> LoadAsGameObjectsAsync(AddressableGenericResource loadRequest, InstantiationParameters instantiationParameters, ChecksRequired Required, Selector Selector)
    {
        List<GameObject> instantiated = new List<GameObject>();
        for (int Index = 0; Index < loadRequest.Handles.Count; Index++)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = loadRequest.Handles[Index];
            object result = await handle.Task;
            if (result is GameObject resource)
            {
                    GameObject spawned = ContentPoliceControl.ContentControl(resource, Required, instantiationParameters.Position, instantiationParameters.Rotation,false,Vector3.zero, Selector, instantiationParameters.Parent);
              //  Debug.Log("Spawned " + spawned.name + " at " + spawned.transform.position + " with rotation " + spawned.transform.rotation);
                instantiated.Add(spawned);
            }
            else
            {
             UnityEngine.Debug.LogError("Unexpected result type: " + result.GetType());
            }
        }
        return instantiated;
    }

    public static async Task<(List<GameObject>, AddressableGenericResource)> LoadAsGameObjectsAsync(string key, InstantiationParameters instantiationParameters, ChecksRequired Required, Selector Selector)
    {
        AddressableGenericResource loadRequest = new AddressableGenericResource(key, AddressableExpectedResult.SingleItem);
        bool loaded = await AddressableLoadFactory.LoadAddressableResourceAsync<GameObject>(loadRequest);
        if (loaded)
        {
            return ( await LoadAsGameObjectsAsync(loadRequest, instantiationParameters,Required, Selector),loadRequest);
        }
        else
        {
                UnityEngine.Debug.LogError("Missing " + key);
            return (null, loadRequest);
        }
    }

    public static async Task<List<Texture>> LoadAsTexturesAsync(AddressableLoadResourceBase loadRequest)
    {
        List<Texture> instantiated = new List<Texture>();
        for (int Index = 0; Index < loadRequest.Handles.Count; Index++)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = loadRequest.Handles[Index];
            Texture texture = await handle.Task as Texture;
            instantiated.Add(texture);
        }
        return instantiated;
    }

    public static async Task<List<Material>> LoadAsMaterialsAsync(AddressableLoadResourceBase loadRequest)
    {
        List<Material> instantiated = new List<Material>();
        for (int Index = 0; Index < loadRequest.Handles.Count; Index++)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = loadRequest.Handles[Index];
            Material material = await handle.Task as Material;
            instantiated.Add(material);
        }
        return instantiated;
    }

    public static async Task<List<Mesh>> LoadAsMeshesAsync(AddressableLoadResourceBase loadRequest)
    {
        List<Mesh> instantiated = new List<Mesh>();
        for (int Index = 0; Index < loadRequest.Handles.Count; Index++)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = loadRequest.Handles[Index];
            Mesh mesh = await handle.Task as Mesh;
            instantiated.Add(mesh);
        }
        return instantiated;
    }
}
}
