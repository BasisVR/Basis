using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.Addressable_Driver.Loading;
using Basis.Scripts.Addressable_Driver.Scene;
using Basis.Scripts.Addressable_Driver.Validation;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Basis.Scripts.Addressable_Driver.Resource
{
    public static class AddressableLoadProcess
    {
        /// <summary>
        /// Generates Handle Based on Resource Type
        /// </summary>
        /// <param name="LoadRequest"></param>
        /// <returns></returns>
        public static async Task<bool> LoadAssetAsync<T>(AddressableLoadResourceBase LoadRequest)
        {
            AddressableValidation.HasExpectedResult(LoadRequest);


            LoadRequest.Handles = new List<AsyncOperationHandle>();

            switch (LoadRequest.ResourceType)
            {
                case AddressableLoadResourceType.Scene:
                    GenerateHandleScene((AddressableSceneResource)LoadRequest);
                    break;
                case AddressableLoadResourceType.Generic:
                    GenerateHandles<T>(LoadRequest);
                    break;
            }
            return await AddressableManagement.AwaitLoading(LoadRequest);
        }
        /// <summary>
        /// Generate 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="LoadRequest"></param>
        public static void GenerateHandles<T>(AddressableLoadResourceBase LoadRequest)
        {
            foreach (IResourceLocation Location in LoadRequest.ResourceLocationHandles)
            {
                AsyncOperationHandle handle = Addressables.LoadAssetAsync<T>(Location);
                AddReleaseHandle(LoadRequest, handle);
            }
        }
        /// <summary>
        /// Handles Scene Generation Through Addressables
        /// </summary>
        /// <param name="LoadRequest"></param>
        public static void GenerateHandleScene(AddressableSceneResource LoadRequest)
        {
            if (LoadRequest.SetAsActiveScene)
            {
                if (LoadRequest.HasActiveSceneCallback == false)
                {
                    LoadRequest.HasActiveSceneCallback = true;
                    LoadRequest.OnLoaded.AddListener(AddressableSceneSetActive.SetSceneActive);
                }
            }
            foreach (IResourceLocation Location in LoadRequest.ResourceLocationHandles)
            {
                AddReleaseHandle(LoadRequest, Addressables.LoadSceneAsync(Location, LoadRequest.LoadSceneMode));
            }
        }
        public static void AddReleaseHandle(AddressableLoadResourceBase LoadRequest, AsyncOperationHandle handle)
        {
            if (AddressableValidation.ValidHandle(LoadRequest, handle))
            {
                LoadRequest.Handles.Add(handle);
            }
        }
        public static void OnCompleteLoad(AddressableLoadResourceBase LoadRequest)
        {
            LoadRequest.OnLoaded.Invoke(LoadRequest);
        }
        public static void StopProgressCheck(AddressableLoadResourceBase LoadRequest)
        {
            LoadRequest.LoadPercentage = 1;
        }
        public static void UpdateProgress(AddressableLoadResourceBase LoadRequest, float totalProgress)
        {
            LoadRequest.LoadPercentage = totalProgress;
        }
    }
}