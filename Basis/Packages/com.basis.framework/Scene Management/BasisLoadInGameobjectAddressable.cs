using Basis.Scripts.Addressable_Driver.Resource;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

/// <summary>
/// Loads an addressable GameObject at runtime using the provided load key and optional validation checks.
/// The object is instantiated with the specified transform parameters and can optionally be released when destroyed.
/// </summary>
public class BasisLoadInGameobjectAddressable : MonoBehaviour
{
    /// <summary>
    /// Addressables key or path used to locate the asset.
    /// </summary>
    public string LoadRequest;

    /// <summary>
    /// Validation checks that must pass before the content is allowed to load.
    /// </summary>
    public ChecksRequired RequiredChecks;

    /// <summary>
    /// Selector type indicating the content category (Avatar, System, Prop).
    /// Defaults to Prop.
    /// </summary>
    public BundledContentHolder.Selector Selector = BundledContentHolder.Selector.Prop;

    /// <summary>
    /// Reference to the instantiated result returned by the addressable load.
    /// </summary>
    public GameObject Result;

    /// <summary>
    /// Desired world position for the instantiated GameObject.
    /// </summary>
    public Vector3 Position;

    /// <summary>
    /// Desired world rotation for the instantiated GameObject.
    /// </summary>
    public Quaternion Rotation;

    /// <summary>
    /// If true, the instantiated GameObject will be released through the Addressables system when destroyed.
    /// </summary>
    public bool ReleaseOnDestroy = false;

    /// <summary>
    /// Unity Start hook. Loads and instantiates the addressable GameObject using the configured parameters.
    /// </summary>
    private async void Start()
    {
        InstantiationParameters instantiationParameters = new InstantiationParameters(Position, Rotation, null);
        Result = await AddressableResourceProcess.LoadAsGameObjectsAsync(LoadRequest, instantiationParameters, RequiredChecks, Selector);
    }

    /// <summary>
    /// Unity OnDestroy hook. Releases the instantiated GameObject if <see cref="ReleaseOnDestroy"/> is true.
    /// </summary>
    public void OnDestroy()
    {
        if (ReleaseOnDestroy)
        {
            AddressableResourceProcess.ReleaseGameobject(Result);
        }
    }
}
