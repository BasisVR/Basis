using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using UnityEngine;
public class BasisObjectSyncNetworking : MonoBehaviour
{
    public ushort MessageIndex = 0;
    public bool HasMessageIndexAssigned;
    public string NetworkId;
    public int TargetFrequency = 10; // Target update frequency in Hz (10 times per second)
    public float NetworkLerpSpeed = 7;
    private double _updateInterval; // Time interval between updates
    private double _lastUpdateTime; // Last update timestamp
    public ushort CurrentOwner;
    public bool IsLocalOwner = false;
    public bool HasActiveOwnership = false;

    public BasisPositionRotationScale Current = new BasisPositionRotationScale();
    public BasisPositionRotationScale Next = new BasisPositionRotationScale();

    public float LerpMultiplier = 3f;
    public BasisContentBase ContentConnector;
    public BasisInteractableObject InteractableObjects;
    public DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced;
    public DataFormat DataFormat = DataFormat.Binary;
    public void Awake()
    {
        if (ContentConnector == null && TryGetComponent<BasisContentBase>(out ContentConnector))
        {
        }
        if (ContentConnector != null)
        {
            ContentConnector.OnNetworkIDSet += OnNetworkIDSet;
        }
        InteractableObjects = this.transform.GetComponentInChildren<BasisInteractableObject>();
        if (InteractableObjects != null)
        {
            InteractableObjects.OnInteractStartEvent += OnInteractStartEvent;
            InteractableObjects.OnInteractEndEvent += OnInteractEndEvent;
        }
        this.transform.GetLocalPositionAndRotation(out Current.Position, out Current.Rotation);
        Current.Scale = this.transform.localScale;

        Next.Scale = Current.Scale;
        Next.Position = Current.Position;
        Next.Rotation = Current.Rotation;
    }
    public void OnDestroy()
    {
   //     BasisObjectSyncSystem.RemoveLocallyOwnedPickup(this);
   //     BasisObjectSyncSystem.StopApplyRemoteData(this);
    }
    private void OnInteractEndEvent(BasisInput input)
    {
    //    BasisNetworkManagement.RemoveOwnership(NetworkId);
    //    BasisObjectSyncSystem.StartApplyRemoteData(this);

    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        await BasisNetworkManagement.TakeOwnershipAsync(NetworkId, (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId);
        //    BasisObjectSyncSystem.StopApplyRemoteData(this);
    }

    private void OnNetworkIDSet(string NetworkID)
    {
        NetworkId = NetworkID;
        BasisNetworkNetIDConversion.RequestId(NetworkId);
    }

    public void OnEnable()
    {
        HasMessageIndexAssigned = false;
        BasisScene.OnNetworkMessageReceived += OnNetworkMessageReceived;
        BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransfer;
        BasisNetworkPlayer.OnOwnershipReleased += OwnershipReleased;
        BasisNetworkNetIDConversion.OnNetworkIdAdded += OnNetworkIdAdded;
        _updateInterval = 1f / TargetFrequency; // Calculate interval (1/33 seconds)
        _lastUpdateTime = Time.timeAsDouble;

        StartRemoteControl();
     //   BasisObjectSyncSystem.StartApplyRemoteData(this);
    }
    public void OnDisable()
    {
        HasMessageIndexAssigned = false;
        BasisScene.OnNetworkMessageReceived -= OnNetworkMessageReceived;
        BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransfer;
        BasisNetworkPlayer.OnOwnershipReleased -= OwnershipReleased;
        BasisNetworkNetIDConversion.OnNetworkIdAdded -= OnNetworkIdAdded;
    }

    private void OwnershipReleased(string UniqueEntityID)
    {
        if (NetworkId == UniqueEntityID)
        {
            IsLocalOwner = false;
            CurrentOwner = 0;
            HasActiveOwnership = false;
            //drop any interactable objects on this transform.
            StartRemoteControl();
        //    BasisObjectSyncSystem.StartApplyRemoteData(this);
        }
    }
    private void OnOwnershipTransfer(string UniqueEntityID, ushort NetIdNewOwner, bool IsOwner)
    {
        if (NetworkId == UniqueEntityID)
        {
            IsLocalOwner = IsOwner;
            CurrentOwner = NetIdNewOwner;
            HasActiveOwnership = true;
            // if (Rigidbody != null && !IsLocalOwner)
            // {
            //     // TODO: have an initial state for this to reference since we cant always handle
            //     //       ownership changes properly if state is lost somewhere
            //     Rigidbody.isKinematic = true;
            // }
            if (!IsLocalOwner)
            {
                StartRemoteControl();
                //      BasisObjectSyncSystem.StartApplyRemoteData(this);
            }
            else
            {
                StopRemoteControl();
                //   BasisObjectSyncSystem.StopApplyRemoteData(this);
            }
            OwnedPickupSet();
        }
    }
    public void OwnedPickupSet()
    {
        if (IsLocalOwner && HasMessageIndexAssigned)
        {
         //   BasisObjectSyncSystem.AddLocallyOwnedPickup(this);
        }
        else
        {
         //   BasisObjectSyncSystem.RemoveLocallyOwnedPickup(this);
        }
    }
    public void OnNetworkIdAdded(string uniqueId, ushort ushortId)
    {
        if (NetworkId == uniqueId)
        {
            MessageIndex = ushortId;
            HasMessageIndexAssigned = true;
            if (HasActiveOwnership == false)
            {
                BasisNetworkManagement.RequestCurrentOwnershipAsync(NetworkId);
            }
            OwnedPickupSet();
        }
    }
    public void LateUpdate()
    {
        if (IsLocalOwner)
        {
            double timeAsDouble = Time.timeAsDouble;
            LateUpdateTime(timeAsDouble);
        }
        else
        {
            float Output = NetworkLerpSpeed * Time.deltaTime;
            Current.Rotation = Quaternion.Slerp(Current.Rotation, Next.Rotation, Output);
            Current.Position = Vector3.Lerp(Current.Position, Next.Position, Output);
            Current.Scale = Vector3.Lerp(Current.Scale, Next.Scale, Output);

            transform.SetLocalPositionAndRotation(Current.Position, Current.Rotation);
            transform.localScale = Current.Scale;
        }
    }
    public void LateUpdateTime(double DoubleTime)
    {
        if (DoubleTime - _lastUpdateTime >= _updateInterval)
        {
            _lastUpdateTime = DoubleTime;
            SendNetworkMessage();
        }
    }
    public void SendNetworkMessage()
    {
        transform.GetLocalPositionAndRotation(out Current.Position, out Current.Rotation);
        Current.Scale = transform.localScale;
        BasisScene.OnNetworkMessageSend?.Invoke(MessageIndex, SerializationUtility.SerializeValue(Current, DataFormat),DeliveryMethod);
    }
    public void OnNetworkMessageReceived(ushort PlayerID, ushort messageIndex, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (HasMessageIndexAssigned && messageIndex == MessageIndex)
        {
            Next = SerializationUtility.DeserializeValue<BasisPositionRotationScale>(buffer, DataFormat);
        }
    }
    public void StartRemoteControl()
    {
        InteractableObjects?.StartRemoteControl();
    }
    public void StopRemoteControl()
    {
        InteractableObjects?.StopRemoteControl();
    }
}
