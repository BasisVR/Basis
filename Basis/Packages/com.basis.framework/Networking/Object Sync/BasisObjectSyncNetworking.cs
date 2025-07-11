using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using static BasisObjectSyncDriver;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    public BasisInteractableObject InteractableObjects;
    public DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced;
    private int HasIndex;
    private bool HasIndexAssigned;
    public void Awake()
    {
        if (InteractableObjects == null)
        {
            InteractableObjects = this.transform.GetComponentInChildren<BasisInteractableObject>();
        }
        if (InteractableObjects != null)
        {
            InteractableObjects.OnInteractStartEvent += OnInteractStartEvent;
            InteractableObjects.OnInteractEndEvent += OnInteractEndEvent;
        }
    }
    public void OnEnable()
    { 
     StartRemoteControl();
    }
    public void OnDisable()
    {
     StopRemoteControl();
    }
    private void OnInteractEndEvent(BasisInput input)
    {
    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, BasisNetworkManagement.LocalPlayerPeer.RemoteId);
    }
    public override void OwnershipReleased()
    {
        StartRemoteControl();
    }
    public override void OnOwnershipTransfer(ushort NetIdNewOwner)
    {
        if (IsLocalOwner)
        {
            StopRemoteControl();
        }
        else
        {
            StartRemoteControl();
        }
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
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
        if (HasIndexAssigned)
        {
            BasisObjectSyncDriver.UpdateObject(HasIndex, BTU);
        }
        else
        {
            HasIndexAssigned = BasisObjectSyncDriver.AddObject(BTU, this.transform, out HasIndex);
        }
    }
    public void SendNetworkSync()
    {
        BasisPositionRotationScale Current = new BasisPositionRotationScale();
        transform.GetLocalPositionAndRotation(out Current.Position, out Current.Rotation);
        Current.Scale = transform.localScale;

        SendCustomNetworkEvent(SerializationUtility.SerializeValue(Current, DataFormat.Binary), DeliveryMethod);
    }
    public void StartRemoteControl()
    {
        if (InteractableObjects != null)
        {
            InteractableObjects.StartRemoteControl();
        }
    }
    public void StopRemoteControl()
    {
        if (InteractableObjects != null)
        {
            InteractableObjects.StopRemoteControl();
        }
    }
}
