using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using UnityEngine;
using static BasisObjectSyncDriver;
public class BasisObjectSyncNetworking : MonoBehaviour
{
    public string NetworkId;

    public ushort MessageIndex = 0;
    public bool HasMessageIndexAssigned;

    public ushort CurrentOwner;
    public bool IsLocalOwner = false;
    public bool HasActiveOwnership = false;
    public BasisContentBase ContentConnector;
    public BasisInteractableObject InteractableObjects;
    public DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced;
    public DataFormat DataFormat = DataFormat.Binary;
    public bool HasIndex;
    public int Index;
    public void Awake()
    {
        if (ContentConnector == null && TryGetComponent<BasisContentBase>(out ContentConnector))
        {
        }
        if (ContentConnector != null)
        {
            ContentConnector.OnClientIdentifierAssigned += OnNetworkIDSet;
        }
        InteractableObjects = this.transform.GetComponentInChildren<BasisInteractableObject>();
        if (InteractableObjects != null)
        {
            InteractableObjects.OnInteractStartEvent += OnInteractStartEvent;
            InteractableObjects.OnInteractEndEvent += OnInteractEndEvent;
        }
    }

    public void OnEnable()
    {
        HasMessageIndexAssigned = false;
        BasisScene.OnNetworkMessageReceived += OnNetworkMessageReceived;
        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransfer;
        BasisNetworkPlayer.OnOwnershipReleased += OwnershipReleased;
        StartRemoteControl();
    }
    public void OnDisable()
    {
        HasMessageIndexAssigned = false;
        BasisScene.OnNetworkMessageReceived -= OnNetworkMessageReceived;
        BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransfer;
        BasisNetworkPlayer.OnOwnershipReleased -= OwnershipReleased;
        StopRemoteControl();
    }
    private void OnInteractEndEvent(BasisInput input)
    {
    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        await BasisNetworkManagement.TakeOwnershipAsync(NetworkId, (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId);
    }

    private async void OnNetworkIDSet(string NetworkID)
    {
        NetworkId = NetworkID;
        await BasisNetworkIdResolver.ResolveAsync(NetworkId);
    }

    private void OwnershipReleased(string UniqueEntityID)
    {
        if (NetworkId == UniqueEntityID)
        {
            IsLocalOwner = false;
            CurrentOwner = 0;
            HasActiveOwnership = false;
            StartRemoteControl();
        }
    }
    private void OnOwnershipTransfer(string UniqueEntityID, ushort NetIdNewOwner, bool IsOwner)
    {
        if (NetworkId == UniqueEntityID)
        {
            IsLocalOwner = IsOwner;
            CurrentOwner = NetIdNewOwner;
            HasActiveOwnership = true;
            if (!IsLocalOwner)
            {
                StartRemoteControl();
            }
            else
            {
                StopRemoteControl();
            }
            OwnedPickupSet();
        }
    }
    public void OwnedPickupSet()
    {
        if (IsLocalOwner && HasMessageIndexAssigned)
        {

        }
        else
        {
        }
    }
    public async void OnNetworkIdAdded(string uniqueId, ushort ushortId)
    {
        if (NetworkId == uniqueId)
        {
            MessageIndex = ushortId;
            HasMessageIndexAssigned = true;
            if (HasActiveOwnership == false)
            {
                (bool, ushort) State = await BasisNetworkManagement.RequestCurrentOwnershipAsync(NetworkId);
                if (State.Item1)
                {
                    ushort Owner = State.Item2;
                }
            }
            OwnedPickupSet();
        }
    }
    public void SendNetworkSync()
    {
        BasisPositionRotationScale Current = new BasisPositionRotationScale();
        transform.GetLocalPositionAndRotation(out Current.Position, out Current.Rotation);
        Current.Scale = transform.localScale;
        BasisScene.OnNetworkMessageSend.Invoke(MessageIndex, SerializationUtility.SerializeValue(Current, DataFormat), DeliveryMethod);
    }
    public void OnNetworkMessageReceived(ushort PlayerID, ushort messageIndex, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (HasMessageIndexAssigned && messageIndex == MessageIndex)
        {
            BasisPositionRotationScale Next = SerializationUtility.DeserializeValue<BasisPositionRotationScale>(buffer, DataFormat);
            BasisTranslationUpdate BTU = new BasisTranslationUpdate
            {
                LerpMultipliers = 1,
                SyncDestination = true,
                SyncScales = true,
                TargetScales = Next.Scale,
                TargetPositions = Next.Position,
                TargetRotations = Next.Rotation
            };
            if (HasIndex)
            {
                BasisObjectSyncDriver.UpdateObject(Index,BTU);
            }
            else
            {
                HasIndex = BasisObjectSyncDriver.AddObject(BTU, this.transform, out Index);
            }
        }
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
