using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Compression;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using UnityEngine;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    [PreviouslySerializedAs("InteractableObjects")]
    public BasisPickupInteractable BasisPickupInteractable;
    public bool CanNetworkSteal = true;
    [SerializeField]
    BasisPositionRotationScale LocalLastData = new BasisPositionRotationScale();
    [SerializeField]
    public BasisObjectSyncDriver.BasisTranslationUpdate BTU = new BasisObjectSyncDriver.BasisTranslationUpdate();
    public BasisInput pendingStealRequest = null;
    public float CatchupLerp = 5;
    public byte[] buffer = new byte[BasisPositionRotationScale.Size];
    public void Awake()
    {
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null)
        {
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
            BasisPickupInteractable.CanHoverInjected.Remove(CanHover);
            BasisPickupInteractable.CanInteractInjected.Remove(CanInteract);
        }
        BasisObjectSyncDriver.RemoveRemoteOwner(this);
        BasisObjectSyncDriver.RemoveLocalOwner(this);
    }
    public override void OnNetworkReady()
    {
        if (BasisPickupInteractable != null)
        {
            if (BasisPickupInteractable.RigidRef != null)
            {
                BasisPickupInteractable.RigidRef.isKinematic = false;
            }
        }
        ControlState();
    }

    private bool CanHover(BasisInput input)
    {
        // Allow hover if we aren't connected
        if (!BasisNetworkManagement.LocalPlayerIsConnected)
        {
            return true;
        }
        return IsOwnedLocallyOnClient || CanNetworkSteal;
    }
    private bool CanInteract(BasisInput input)
    {
        // Allow interact if we arent connected or if we own it locally
        if (IsOwnedLocallyOnClient)
        {
            return true;
        }
        // NOTE: this is called 2 times per frame on interact start, once to tell HoverEnd that it will be interacting, and again for the actual interact check
        if (CanNetworkSteal && !IsOwnedLocallyOnClient && pendingStealRequest == null)
        {
            pendingStealRequest = input;
            CanInteractAsync(); // ControlState handles the ownership transfer logic here
        }
        return false;
    }

    private async void CanInteractAsync()
    {
        var result = await TakeOwnershipAsync(5000); // 5 second timeout 
        if (result.Success == false)
        {
            pendingStealRequest = null;
        }
    }

    public override void OnOwnershipTransfer(ushort NetIdNewOwner)
    {
        ControlState();
    }
    public void ControlState()
    {
        //lets always just update the last data so going from here we have some reference of last.
        if (IsOwnedLocallyOnClient)
        {
            BasisObjectSyncDriver.AddLocalOwner(this);
            BasisObjectSyncDriver.RemoveRemoteOwner(this);
            // Delayed InteractStart when local user gets ownership
            if (pendingStealRequest != null)
            {
                BasisPlayerInteract.Instance.ForceSetInteracting(BasisPickupInteractable, pendingStealRequest);
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
        }
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (IsOwnedLocallyOnClient == false)
        {
            var LocalLastData = BasisPositionRotationScale.FromBytes(buffer);
            BTU.TargetRotation = BasisCompression.QuaternionCompressor.DecompressQuaternion(LocalLastData.Rotation);
            BTU.LerpMultipliers = CatchupLerp;
            BTU.TargetPosition = LocalLastData.DeCompress();
        }
    }
    public void SendNetworkSync()
    {
        transform.GetLocalPositionAndRotation(out UnityEngine.Vector3 Position, out UnityEngine.Quaternion Temp);
        LocalLastData.Compress(Position, BasisCompression.QuaternionCompressor.CompressQuaternion(ref Temp));
        LocalLastData.ToBytes(buffer, 0);
        SendCustomNetworkEvent(buffer, DeliveryMethod.Sequenced);
    }
}
