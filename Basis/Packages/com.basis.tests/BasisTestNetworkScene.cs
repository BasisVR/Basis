using Basis;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using static SerializableBasis;

public class BasisTestNetworkScene : MonoBehaviour
{
    [Header("Load Settings")]
    public string Password;
    public string MetaUrl;
    public bool IsScene = false;
    public bool IsPersistent = false;
    [Header("Transform Settings")]
    public bool OverrideSpawnPosition = false;
    public Vector3 Position;
    public bool ModifyScale = false;
    [Header("Advanced")]
    public byte[] SendingData;
    public ushort[] Recipients;
    public ushort MessageIndex;
    private LocalLoadResource loadedResource;

    private void OnEnable()
    {
        // Set spawn position
        Position = OverrideSpawnPosition ? transform.position : BasisLocalPlayer.Instance.transform.position;

        if (IsScene)
        {
            BasisNetworkSpawnItem.RequestSceneLoad(Password, MetaUrl, IsPersistent, out loadedResource);
        }
        else
        {
            BasisNetworkSpawnItem.RequestGameObjectLoad(
                Password,
                MetaUrl,
                Position,
                Quaternion.identity,
                Vector3.one,
                IsPersistent,
                ModifyScale,
                out loadedResource
            );
        }
    }

    private void OnDisable()
    {
        if (IsScene)
        {
            BasisNetworkSpawnItem.RequestSceneUnLoad(loadedResource.LoadedNetID);
        }
        else
        {
            BasisNetworkSpawnItem.RequestGameObjectUnLoad(loadedResource.LoadedNetID);
        }
    }
}
