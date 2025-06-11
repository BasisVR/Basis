using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using LiteNetLib;
using UnityEngine;
using static SerializableBasis;

public class BasisTestNetworkScene : MonoBehaviour
{
    public byte[] SendingData;
    public ushort[] Recipients;
    public ushort MessageIndex;
    public bool SceneLoadTest = false;
    public bool GameobjectLoadTest = false;
    public bool PropLoadTest = false;
    public LocalLoadResource Scene;
    public LocalLoadResource Gameobject;
    public bool IsPersistent;
    public string ScenePassword = "Scene";
    public string SceneMetaUrl = "https://BasisFramework.b-cdn.net/Worlds/DX11/3dd6aa45-a685-4ed2-ba6d-2d9c2f3c1765_638652274774362697.BasisEncyptedMeta";

    public string GameobjectPassword = "862eb77aa76d193284a806f040deb6c6b9d4866bef63f7c829237d524fb979d2";
    public string GameobjectMetaUrl = "https://BasisFramework.b-cdn.net/Props/DX11/NetworkedTestPickup/ec0fdd4d-9eb2-467c-9b52-40f05932f859_638736352879974628.BasisEncyptedMeta";

    public string PropPassword = "28d6240548cae8229e169777686b4b967ca23b924abb96565823206989795215";
    public string PropMetaUrl = "https://BasisFramework.b-cdn.net/Props/DX11/90516234-6412-4a1e-a45f-c3f8dfbd7071_638735227126189132.BasisEncyptedMeta";
    public bool OverrideSpawnPosition;
    public Vector3 Position;
    public bool ModifyScale = false;
    public void Awake()
    {
        BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalPlayerJoined;
        BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayerJoined;
    }
    public void OnEnable()
    {
        if(OverrideSpawnPosition)
        {
            Position = this.transform.position;
        }
        else
        {
            Position = BasisLocalPlayer.Instance.transform.position;
        }
        if (SceneLoadTest)
        {
            BasisNetworkSpawnItem.RequestSceneLoad(ScenePassword,
               SceneMetaUrl,IsPersistent, out Scene);
        }
        if (GameobjectLoadTest)
        {
            BasisNetworkSpawnItem.RequestGameObjectLoad(GameobjectPassword,
                 GameobjectMetaUrl, Position, Quaternion.identity, Vector3.one, IsPersistent, ModifyScale, out Gameobject);
        }
        if (PropLoadTest)
        {
            BasisNetworkSpawnItem.RequestGameObjectLoad(PropPassword,
                 PropMetaUrl, Position, Quaternion.identity, Vector3.one, IsPersistent, ModifyScale, out Gameobject);
        }
    }
    public void OnDisable()
    {
        if (SceneLoadTest)
        {
            BasisNetworkSpawnItem.RequestSceneUnLoad(Scene.LoadedNetID);
        }
        if (GameobjectLoadTest)
        {
            BasisNetworkSpawnItem.RequestGameObjectUnLoad(Gameobject.LoadedNetID);
        }
    }
    /// <summary>
    /// this runs after a remote user connects and passes all there local checks and balances with the server
    /// </summary>
    /// <param name="player1"></param>
    /// <param name="player2"></param>
    private void OnRemotePlayerJoined(BasisNetworkPlayer player1, BasisRemotePlayer player2)
    {

    }
    /// <summary>
    /// this is called once
    /// level is loaded
    /// network is connected
    /// player is created
    /// player is authenticated
    /// </summary>
    /// <param name="player1"></param>
    /// <param name="player2"></param>
    public void OnLocalPlayerJoined(BasisNetworkPlayer player1, BasisLocalPlayer player2)
    {
        BasisScene.OnNetworkMessageReceived += OnNetworkMessageReceived;
        BasisScene.OnNetworkMessageSend(MessageIndex, SendingData, DeliveryMethod.ReliableOrdered, Recipients);
    }
    private void OnNetworkMessageReceived(ushort PlayerID, ushort MessageIndex, byte[] buffer, DeliveryMethod Method = DeliveryMethod.ReliableOrdered)
    {

    }
}
