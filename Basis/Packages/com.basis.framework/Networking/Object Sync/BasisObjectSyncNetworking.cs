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
            // InteractableObjects.OnInteractEndEvent += OnInteractEndEvent;
        }
    }
    public void OnDisable()
    {
        if (InteractableObjects != null)
        {
            InteractableObjects.OnInteractStartEvent -= OnInteractStartEvent;
        }
    }
    public override void OnNetworkReady()
    {
        ControlState();
    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        if(BasisNetworkManagement.LocalPlayerIsConnected)
        {
            if (IsOwnedLocally)
            {
                StartLocalControl();
            }
            else
            {
                //no need to use await ownership will get back here from lower level.
                BasisOwnershipResult Result = await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, BasisNetworkManagement.LocalPlayerPeer.RemoteId);
            }
        }
        else
        {
            StartLocalControl();
        }
    }
    /// <summary>
    /// ownership has been completely removed in that case lets just make it locally able
    /// </summary>
    public override void ServerOwnershipDestroyed()
    {
        BasisDebug.Log("Ownership Released Resetting To Local", BasisDebug.LogTag.Networking);
        StartLocalControl();
    }
    public override void OnOwnershipTransfer(ushort NetIdNewOwner)
    {
        ControlState();
    }
    public void ControlState()
    {
        if (IsOwnedLocally)
        {
            StartLocalControl();
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
        SendCustomNetworkEvent(SerializationUtility.SerializeValue(Current, DataFormat.Binary), DeliveryMethod.Sequenced);
    }
    public void StartRemoteControl()
    {
        BasisDebug.Log($"Starting Remote Control Of {this.gameObject.name}", BasisDebug.LogTag.Networking);
        if (InteractableObjects != null && InteractableObjects.IsPuppeted == false)
        {
            InteractableObjects.StartRemoteControl();
        }
        BasisObjectSyncDriver.RemoveOwnedObjectSync(this);
    }
    public void StartLocalControl()
    {
        BasisDebug.Log($"Starting Local Control Of {this.gameObject.name}", BasisDebug.LogTag.Networking);
        if (InteractableObjects != null && InteractableObjects.IsPuppeted)
        {
            InteractableObjects.StopRemoteControl();
        }
        BasisObjectSyncDriver.AddOwnedObjectSync(this);
    }
}
