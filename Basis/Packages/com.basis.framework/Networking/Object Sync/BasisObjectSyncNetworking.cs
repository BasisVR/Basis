using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.TransformBinders.BoneControl;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using Unity.Mathematics;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    [PreviouslySerializedAs("InteractableObjects")]
    public BasisPickupInteractable BasisPickupInteractable;
    public bool HasRemoteIndex = false;
    public bool CanNetworkSteal = true;
    BasisPositionRotationScale LocalLastData = new BasisPositionRotationScale();
    BasisCompression.QuaternionCompressor.Quaternion ConvertQat = new BasisCompression.QuaternionCompressor.Quaternion();
    public BasisObjectSyncDriver.BasisTranslationUpdate BTU = new BasisObjectSyncDriver.BasisTranslationUpdate();
    public BasisInput pendingStealRequest = null;

    public void Awake()
    {
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.OnInteractStartEvent += OnInteractStartEvent;
            BasisPickupInteractable.CanHoverInjected.Add(CanHover);
            BasisPickupInteractable.CanInteractInjected.Add(CanInteract);
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
            BasisPickupInteractable.CanHoverInjected.Remove(CanHover);
            BasisPickupInteractable.CanInteractInjected.Remove(CanInteract);
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

    private bool CanHover(BasisInput input)
    {
        // Allow hover if we aren't connected
        if (!BasisNetworkManagement.LocalPlayerIsConnected) return true;
        return IsOwnedLocally || CanNetworkSteal;
    }
    private bool CanInteract(BasisInput input)
    {
        // TODO: revisit this, this might break expectations if we loose connection but keep the pickup loaded
        // Allow interact if we arent connected or if we own it locally
        if (!BasisNetworkManagement.LocalPlayerIsConnected || IsOwnedLocally) return true;
        // NOTE: this is called 2 times per frame on interact start, once to tell HoverEnd that it will be interacting, and again for the actual interact check
        if (CanNetworkSteal && !IsOwnedLocally && pendingStealRequest == null)
        {
            // TODO: some sort of timeout to re-send the request
            pendingStealRequest = input;
            UnsafeTakeOwnership(); // ControlState handles the ownership transfer logic here
        }
        return false;
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
            // Delayed InteractStart when local user gets ownership
            if (pendingStealRequest != null)
            {
                BasisPlayerInteract.Instance.UnsafeSetInteractcting(BasisPickupInteractable, pendingStealRequest);
                // still reset the request, we dont care if we actually picked up
                pendingStealRequest = null;
            }
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
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (HasRemoteIndex)
        {
            LocalLastData = SerializationUtility.DeserializeValue<BasisPositionRotationScale>(buffer, DataFormat.Binary);
            BasisCompression.QuaternionCompressor.DecompressQuaternion(ref ConvertQat, LocalLastData.Rotation);
            quaternion Rot = new quaternion(ConvertQat.Data[0], ConvertQat.Data[1], ConvertQat.Data[2], ConvertQat.Data[3]);
            BTUUpdate(Rot);
        }
    }
    public void WriteLocalLastData()
    {
        transform.GetLocalPositionAndRotation(out UnityEngine.Vector3 Position, out UnityEngine.Quaternion Temp);
        BasisCompression.QuaternionCompressor.Quaternion ConvertQat = new BasisCompression.QuaternionCompressor.Quaternion(Temp);
        LocalLastData.Compress(Position, BasisCompression.QuaternionCompressor.CompressQuaternion(ref ConvertQat));
        // LocalLastData.Scale = transform.localScale;
    }
    public void BTUUpdate(UnityEngine.Quaternion Rotation)
    {
        BTU = new BasisObjectSyncDriver.BasisTranslationUpdate
        {
            LerpMultipliers = 5,
            SyncDestination = true,
            SyncScales = false,
            // TargetScales = LocalLastData.Scale,
            TargetPositions = LocalLastData.DeCompress(),
            TargetRotations = Rotation
        };
    }
    public void SendNetworkSync()
    {
        WriteLocalLastData();
        byte[] Data = SerializationUtility.SerializeValue(LocalLastData, DataFormat.Binary);
        SendCustomNetworkEvent(Data, DeliveryMethod.Sequenced);
    }
    /// <summary>
    /// ownership has been completely removed in that case lets just make it locally able
    /// </summary>
    public override void ServerOwnershipDestroyed()
    {
        BasisDebug.Log("This Objects Ownership was Destroyed", BasisDebug.LogTag.Networking);
    }
}
