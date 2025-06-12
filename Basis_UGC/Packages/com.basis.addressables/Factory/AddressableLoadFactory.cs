using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.Addressable_Driver.Validation;
using System;
using System.Threading.Tasks;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Addressable_Driver.Factory
{
    /// <summary>
    /// Factory class for loading addressable assets asynchronously.
    /// </summary>
    public static class AddressableLoadFactory
    {
        /// <summary>
        /// Asynchronously loads an addressable asset.
        /// </summary>
        /// <param name="LoadRequest">The load request parameters.</param>
        public static async Task<bool> LoadAddressableResourceAsync<T>(AddressableLoadResourceBase LoadRequest)
        {
            LoadRequest.IsLoading = true;
            try
            {
                if (AddressableValidation.ValidateLoadResource(LoadRequest))
                {
                    // For safety, we never assume people are going to do the right thing.
                    if (ReleaseResource(LoadRequest))
                    {
                        AddressableDebug.DebugError("Trying to load an addressable asset that was already loaded, resolving...", LoadRequest);
                    }

                    if (LoadRequest.IsBuiltIn)
                    {
                        LoadRequest.ResourceLocationHandles = await Addressables.LoadResourceLocationsAsync(LoadRequest.Key, typeof(T)).Task;
                    }
                    else
                    {
                        LoadRequest.ResourceLocationHandles = await AddressableRemoteLoader.LoadcatalogAndReturnIResourceLocation(LoadRequest.CatalogPath, LoadRequest.Key, typeof(T));
                    }
                    if (LoadRequest.ResourceLocationHandles != null)
                    {
                        if (LoadRequest.ResourceLocationHandles.Count == 0)
                        {
                            AddressableDebug.DebugError($"Resource handles were empty... [{LoadRequest.Key}]", LoadRequest);
                        }
                        else
                        {
                            return await AddressableLoadProcess.LoadAssetAsync<T>(LoadRequest);
                        }
                    }
                    else
                    {
                        AddressableDebug.DebugError("Resource handles result was null", LoadRequest);
                    }
                }
                LoadRequest.IsLoading = false;
            }
            catch (Exception E)
            {
                AddressableDebug.DebugError(E.StackTrace + " " + E.Message, LoadRequest);
            }
            LoadRequest.IsLoading = false;
            return false;
        }
        /// <summary>
        /// Unloads the previously loaded addressable asset.
        /// </summary>
        /// <param name="LoadRequest">The load request parameters.</param>
        /// <returns>True if the resource was unloaded, false otherwise.</returns>
        public static bool ReleaseResource(AddressableLoadResourceBase loadRequest)
        {
            if (loadRequest == null || (loadRequest.Handles?.Count == 0 && loadRequest.ResourceLocationHandles?.Count != 0))
            {
                return false;
            }

            if (loadRequest.Handles != null)
            {
                int HandleCount = loadRequest.Handles.Count;
                for (int HandleIndex = 0; HandleIndex < HandleCount; HandleIndex++)
                {
                    UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = loadRequest.Handles[HandleIndex];
                    Addressables.Release(handle);
                }
                loadRequest.Handles.Clear();
            }

            if (loadRequest.ResourceLocationHandles.Count != 0)
            {
                Addressables.Release(loadRequest.ResourceLocationHandles);
            }

            return true;
        }
    }
}
