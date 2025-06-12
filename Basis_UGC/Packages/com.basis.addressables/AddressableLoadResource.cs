using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Enums;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.SceneManagement;
namespace Basis.Scripts.Addressable_Driver
{
    [System.Serializable]
    public class AddressableLoadResourceBase
    {
        public string CatalogPath;
        public string Key;
        public AddressableLoadResourceType ResourceType = AddressableLoadResourceType.Generic;
        public AddressableExpectedResult ExpectedResult = AddressableExpectedResult.SingleItem;
        public IList<IResourceLocation> ResourceLocationHandles;
        public IList<AsyncOperationHandle> Handles = new List<AsyncOperationHandle>();
        public UnityEvent<string> OnLoadFailure = new UnityEvent<string>();
        public UnityEvent<AddressableLoadResourceBase> OnLoaded = new UnityEvent<AddressableLoadResourceBase>();
        public float LoadPercentage = 0;
        public bool IsLoading = false;
        public bool IsBuiltIn = true;
        public AddressableLoadResourceBase(string key, AddressableExpectedResult expectedResult)
        {
            Key = key;
            ExpectedResult = expectedResult;
        }
    }
    public class AddressableGenericResource : AddressableLoadResourceBase
    {
        public AddressableGenericResource(string key, AddressableExpectedResult expectedResult) : base(key, expectedResult)
        {
            ResourceType = AddressableLoadResourceType.Generic;
        }

        public AddressableGenericResource(AssetReference assetReference, AddressableExpectedResult expectedResult) : base(assetReference.RuntimeKeyIsValid() ? assetReference.RuntimeKey.ToString() : "Invalid", expectedResult)
        {
            if (!assetReference.RuntimeKeyIsValid())
            {
                Debug.Log("Construct Type is Other but Preset == Scene or Gameobject");
            }
        }

        public AddressableGenericResource(string key, AddressableLoadResourceType resourceType, AddressableExpectedResult expectedResult) : base(key, expectedResult)
        {
            ResourceType = resourceType;
        }
    }

    public class AddressableSceneResource : AddressableLoadResourceBase
    {
        public bool SetAsActiveScene = true;
        public bool HasActiveSceneCallback = false;
        public LoadSceneMode LoadSceneMode = LoadSceneMode.Additive;

        public AddressableSceneResource(string key, bool setAsActiveScene, LoadSceneMode loadSceneMode) : base(key, AddressableExpectedResult.SingleItem)
        {
            ResourceType = AddressableLoadResourceType.Scene;
            SetAsActiveScene = setAsActiveScene;
            LoadSceneMode = loadSceneMode;
        }
    }
}