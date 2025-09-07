using Basis.Scripts.Addressable_Driver.Resource;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

public class BasisLoadInGameobjectAddressable : MonoBehaviour
{
    public string LoadRequest;
    public ChecksRequired RequiredChecks;
    public BundledContentHolder.Selector Selector = BundledContentHolder.Selector.Prop;
    public GameObject Result;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool ReleaseOnDestroy = false;
    async void Start()
    {
        InstantiationParameters instantiationParameters = new InstantiationParameters(Position, Rotation, null);
        Result = await AddressableResourceProcess.LoadAsGameObjectsAsync(LoadRequest, instantiationParameters, RequiredChecks, Selector);
    }
    public void OnDestroy()
    {
        if (ReleaseOnDestroy)
        {
            AddressableResourceProcess.ReleaseGameobject(Result);
        }
    }
}
