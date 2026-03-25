using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using UnityEngine;
public class BasisObjectSyncNetworking : BasisNetworkBehaviour
{
    public BasisPickupInteractable BasisPickupInteractable;
    public bool CanNetworkSteal = true;
    [SerializeField]
    BasisPositionRotationScale LocalLastData = new BasisPositionRotationScale();
    [SerializeField]
    public BasisTranslationUpdate BTU = new BasisTranslationUpdate();
    public BasisInput pendingStealRequest = null;
    public float CatchupLerp = 5;
    public byte[] buffer = new byte[BasisPositionRotationScale.Size];
    public Transform SelfTransform;
    public void Awake()
    {
        SelfTransform = this.transform;
        if (BasisPickupInteractable == null)
        {
            BasisPickupInteractable = this.transform.GetComponentInChildren<BasisPickupInteractable>();
        }
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.CanHoverInjected.Add(CanHover);
            BasisPickupInteractable.CanInteractInjected.Add(CanInteract);
            BasisPickupInteractable.OnInteractStartEvent.AddListener(OnInteractStartEvent);
        }
        if (BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = false;
        }
        if (buffer == null || buffer.Length < BasisPositionRotationScale.Size)
        {
            buffer = new byte[BasisPositionRotationScale.Size];
        }
    }
    public void OnDisable()
    {
        if (BasisPickupInteractable != null)
        {
            BasisPickupInteractable.CanHoverInjected.Remove(CanHover);
            BasisPickupInteractable.CanInteractInjected.Remove(CanInteract);
            BasisPickupInteractable.OnInteractStartEvent.RemoveListener(OnInteractStartEvent);
        }
    }
    public override void OnDestroy()
    {
        BasisObjectSyncDriver.RemoveRemoteOwner(this);
        BasisObjectSyncDriver.RemoveLocalOwner(this);
        base.OnDestroy();
    }
    public override void OnNetworkReady()
    {
        ControlState();
    }

    private bool CanHover(BasisInput input)
    {
        // Allow hover if we aren't connected
        if (!BasisNetworkConnection.LocalPlayerIsConnected)
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
        // Allow if stealing is enabled and no other input has a steal in progress
        // NOTE: pendingStealRequest is only set in OnInteractStartEvent to avoid
        // side effects when this is called speculatively (e.g. via IsInfluencable)
        return CanNetworkSteal && (pendingStealRequest == null || pendingStealRequest == input);
    }
    private void OnInteractStartEvent(BasisInput input)
    {
        if (!IsOwnedLocallyOnClient)
        {
            pendingStealRequest = input;
        }
        CanInteractAsync(); // ControlState handles the ownership transfer logic here
    }
    private async void CanInteractAsync()
    {
        var result = await TakeOwnershipAsync(5000); // 5 second timeout 
        if (result.Success == false)
        {
            pendingStealRequest = null;
        }
    }
    public void SetIsKinematicOnPickup(bool state)
    {
        if (BasisPickupInteractable != null && BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = state;
        }
    }
    public override void OnOwnershipTransfer(BasisNetworkPlayer NetIdNewOwner)
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
            if (pendingStealRequest != null)
            {
                // Set non-kinematic before ForceSetInteracting so that OnInteractStart
                // saves the correct _previousKinematicValue (false) for restore on drop
                SetIsKinematicOnPickup(false);
                BasisPlayerInteract.Instance.ForceSetInteracting(BasisPickupInteractable, pendingStealRequest);
                pendingStealRequest = null;
                // ForceSetInteracting -> OnInteractStart re-applies isKinematic = true
                // when KinematicWhileInteracting is enabled, so don't override after
            }
            else if (BasisPickupInteractable != null
                && BasisPickupInteractable.KinematicWhileInteracting
                && BasisPickupInteractable.RequiresUpdateLoop)
            {
                // Currently held with KinematicWhileInteracting - preserve kinematic state
            }
            else
            {
                SetIsKinematicOnPickup(false);
            }
        }
        else
        {
            // Initialize BTU from current transform so the lerp job doesn't
            // snap the object back to origin before the first sync arrives
            SelfTransform.GetLocalPositionAndRotation(out UnityEngine.Vector3 currentPos, out UnityEngine.Quaternion currentRot);
            BTU.TargetPosition = currentPos;
            BTU.TargetRotation = currentRot;
            BTU.TargetScales = SelfTransform.localScale;
            BTU.LerpMultipliers = CatchupLerp;

            BasisObjectSyncDriver.RemoveLocalOwner(this);
            BasisObjectSyncDriver.AddRemoteOwner(this);
            if (BasisPickupInteractable != null)
            {
                BasisPickupInteractable.Drop();
            }
            SetIsKinematicOnPickup(true);
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
            BTU.TargetScales = LocalLastData.DecompressScale();
        }
    }
    public void SendNetworkSync()
    {
        transform.GetLocalPositionAndRotation(out UnityEngine.Vector3 Position, out UnityEngine.Quaternion Temp);
        LocalLastData.Compress(Position, BasisCompression.QuaternionCompressor.CompressQuaternion(ref Temp), transform.localScale);
        LocalLastData.ToBytes(buffer, 0);
        SendCustomNetworkEvent(buffer, DeliveryMethod.Sequenced);
    }
}
