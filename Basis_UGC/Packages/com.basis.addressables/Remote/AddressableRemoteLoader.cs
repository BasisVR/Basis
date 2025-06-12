using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

public static class AddressableRemoteLoader
{
    public static async Task<IList<IResourceLocation>> LoadcatalogAndReturnIResourceLocation(string LoadAddress, string IdInsideAddressable, System.Type AssetType)
    {

        if (string.IsNullOrEmpty(LoadAddress) == false && string.IsNullOrEmpty(IdInsideAddressable) == false)
        {
            AsyncOperationHandle<IResourceLocator> Handle = Addressables.LoadContentCatalogAsync(LoadAddress);
            IResourceLocator IResourceLocator = await Handle.Task;
            if (IResourceLocator.Locate(IdInsideAddressable, AssetType, out IList<IResourceLocation> List))
            {
                return List;
            }
            else
            {
                Debug.LogError("unable to find any Resource Locators at " + LoadAddress + " | " + IdInsideAddressable);
            }
        }
        else
        {
            Debug.LogError("Load Address or Addressable ID submited was null or empty");
        }
        IList<IResourceLocation> empty = new List<IResourceLocation>();

        return empty;
    }
}