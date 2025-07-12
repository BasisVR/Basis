using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Compression;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using Unity.Mathematics;
using UnityEngine;
using static BasisObjectSyncDriver;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    [PreviouslySerializedAs("InteractableObjects")]
    public BasisPickupInteractable BasisPickupInteractable;
    public bool HasRemoteIndex = false;
    BasisPositionRotationScale LocalLastData = new BasisPositionRotationScale();
    BasisCompression.QuaternionCompressor.Quaternion ConvertQat = new BasisCompression.QuaternionCompressor.Quaternion();
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
        if (BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = false;
        }
    }
    public void OnDisable()
    {
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.OnInteractStartEvent -= OnInteractStartEvent;
        }
        BasisObjectSyncDriver.RemoveRemoteOwner(this);
        BasisObjectSyncDriver.RemoveLocalOwner(this);
    }
    public override void OnNetworkReady()
    {
        if (BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = false;
        }
        ControlState();
    }
    private async void OnInteractStartEvent(BasisInput input)
    {
        if (BasisNetworkManagement.LocalPlayerIsConnected)
        {
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
        //lets always just update the last data so going from here we have some reference of last.
        WriteLocalLastData();
        if (IsOwnedLocally)
        {
            BasisObjectSyncDriver.AddLocalOwner(this);
            BasisObjectSyncDriver.RemoveRemoteOwner(this);
            HasRemoteIndex = false;
        }
        else
        {
            BasisObjectSyncDriver.RemoveLocalOwner(this);
            BasisObjectSyncDriver.AddRemoteOwner(this);
            if (BasisPickupInteractable != null)
            {
                BasisPickupInteractable.Drop();
            }
            HasRemoteIndex = true;
        }
    }
    public BasisTranslationUpdate BTU;
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (HasRemoteIndex)
        {
            LocalLastData = SerializationUtility.DeserializeValue<BasisPositionRotationScale>(buffer, DataFormat.Binary);
            BasisCompression.QuaternionCompressor.DecompressQuaternion(ref ConvertQat, LocalLastData.Rotation);
            quaternion Rot = new quaternion(ConvertQat.Data[0], ConvertQat.Data[1], ConvertQat.Data[2], ConvertQat.Data[3]);
            BTU = new BasisTranslationUpdate
            {
                LerpMultipliers = 5,
                SyncDestination = true,
                SyncScales = false,
               // TargetScales = LocalLastData.Scale,
                TargetPositions = LocalLastData.Position,
                TargetRotations = Rot
            };
        }
    }
    public void WriteLocalLastData()
    {
        transform.GetLocalPositionAndRotation(out LocalLastData.Position, out UnityEngine.Quaternion Temp);
        BasisCompression.QuaternionCompressor.Quaternion ConvertQat = new BasisCompression.QuaternionCompressor.Quaternion(Temp);
        LocalLastData.Rotation = BasisCompression.QuaternionCompressor.CompressQuaternion(ref ConvertQat);
       // LocalLastData.Scale = transform.localScale;
    }
    public void SendNetworkSync()
    {
        WriteLocalLastData();
        SendCustomNetworkEvent(SerializationUtility.SerializeValue(LocalLastData, DataFormat.Binary), DeliveryMethod.Sequenced);
    }
    /// <summary>
    /// ownership has been completely removed in that case lets just make it locally able
    /// </summary>
    public override void ServerOwnershipDestroyed()
    {
        BasisDebug.Log("This Objects Ownership was Destroyed", BasisDebug.LogTag.Networking);
    }
}
