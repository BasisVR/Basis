using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;

public static class AddressableIdTransformer
{
    //Implement a method to transform the internal ids of locations
    static string TransformId(IResourceLocation location)
    {
        //  if (location.ResourceType == typeof(IAssetBundleResource)
        //      && location.InternalId.StartsWith("http", System.StringComparison.Ordinal))
        //     return location.InternalId + "?customQueryTag=customQueryValue";

        return location.InternalId;
    }

    //Override the Addressables transform method with your custom method.
    //This can be set to null to revert to default behavior.
    [RuntimeInitializeOnLoadMethod]
    static void SetInternalIdTransform()
    {
        Addressables.InternalIdTransformFunc = TransformId;
    }
}