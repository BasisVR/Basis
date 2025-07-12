using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using static BasisObjectSyncDriver;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    [PreviouslySerializedAs("InteractableObjects")]
    public BasisPickupInteractable BasisPickupInteractable;
    private int HasObjectSyncIndex;
    private bool HasIndexAssigned;
    BasisPositionRotationScale LocalLastData;
    public void Awake()
    {
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.OnInteractStartEvent += OnInteractStartEvent;
        }
    }
    public void OnDisable()
    {
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.OnInteractStartEvent -= OnInteractStartEvent;
        }
    }
    public override void OnNetworkReady()
    {
        ControlState();
    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        if (BasisNetworkManagement.LocalPlayerIsConnected)
        {
            BasisObjectSyncDriver.AddLocalOwner(this);
            //no need to use await ownership will get back here from lower level.
            BasisOwnershipResult Result = await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, BasisNetworkManagement.LocalPlayerPeer.RemoteId);
        }
        else
        {
            BasisDebug.Log("Missing Network Playing Dead", BasisDebug.LogTag.Networking);
        }
    }
    public override void OnOwnershipTransfer(ushort NetIdNewOwner)
    {
        ControlState();
    }
    public void ControlState()
    {
        if (IsOwnedLocally)
        {
            BasisObjectSyncDriver.AddLocalOwner(this);
        }
        else
        {
            BasisObjectSyncDriver.RemoveLocalOwner(this);
            if (BasisPickupInteractable != null)
            {
                BasisPickupInteractable.Drop();
            }
            AddRemoteMovement();
        }
    }
    public void AddRemoteMovement()
    {
        BasisTranslationUpdate BTU = new BasisTranslationUpdate
        {
            LerpMultipliers = 1,
            SyncDestination = true,
            SyncScales = true,
            TargetScales = LocalLastData.Scale,
            TargetPositions = LocalLastData.Position,
            TargetRotations = LocalLastData.Rotation
        };
        if(HasIndexAssigned)//this shouldnt occur but i guess it will
        {
            BasisObjectSyncDriver.RemoveObject(HasObjectSyncIndex);
        }
        HasIndexAssigned = BasisObjectSyncDriver.AddObject(BTU, this.transform, out HasObjectSyncIndex);
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (HasIndexAssigned)
        {
            BasisPositionRotationScale Next = SerializationUtility.DeserializeValue<BasisPositionRotationScale>(buffer, DataFormat.Binary);
            BasisTranslationUpdate BTU = new BasisTranslationUpdate
            {
                LerpMultipliers = 1,
                SyncDestination = true,
                SyncScales = true,
                TargetScales = Next.Scale,
                TargetPositions = Next.Position,
                TargetRotations = Next.Rotation
            };
            BasisObjectSyncDriver.UpdateObject(HasObjectSyncIndex, BTU);
        }
    }
    public void SendNetworkSync()
    {
        LocalLastData = new BasisPositionRotationScale();
        transform.GetLocalPositionAndRotation(out LocalLastData.Position, out LocalLastData.Rotation);
        LocalLastData.Scale = transform.localScale;
        SendCustomNetworkEvent(SerializationUtility.SerializeValue(LocalLastData, DataFormat.Binary), DeliveryMethod.Sequenced);
    }
    /// <summary>
    /// ownership has been completely removed in that case lets just make it locally able
    /// </summary>
    public override void ServerOwnershipDestroyed()
    {
        BasisDebug.Log("This Objects Ownership was Destroyed", BasisDebug.LogTag.Networking);
    }
    /*
    public void StopRemotePuppeting()
    {
        BasisDebug.Log($"Starting Local Control Of {this.gameObject.name}", BasisDebug.LogTag.Networking);
        if (BasisPickupInteractable != null && BasisPickupInteractable.IsPuppeted)
        {
            BasisPickupInteractable.StopRemoteControl();
        }
        BasisObjectSyncDriver.AddLocalOwner(this);
    }
    public void AllowRemotePuppeting()
    {
        BasisDebug.Log($"Starting Remote Control Of {this.gameObject.name}", BasisDebug.LogTag.Networking);
        if (BasisPickupInteractable != null && BasisPickupInteractable.IsPuppeted == false)
        {
            BasisPickupInteractable.StartRemoteControl();
        }
    }*/
}
