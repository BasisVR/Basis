using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.Addressable_Driver.Factory;
using UnityEngine;

public class BasisLoadInGameobjectAddressable : MonoBehaviour
{
    public AddressableGenericResource Resource;
    public string LoadRequest;
    async void Start()
    {
        Resource = new AddressableGenericResource(LoadRequest, AddressableExpectedResult.SingleItem);
        await AddressableLoadFactory.LoadAddressableResourceAsync<GameObject>(Resource);
        GameObject Result = (GameObject)Resource.Handles[0].Result;
        Result = GameObject.Instantiate(Result);
    }
    public void OnDestroy()
    {
        AddressableLoadFactory.ReleaseResource(Resource);
    }
}
