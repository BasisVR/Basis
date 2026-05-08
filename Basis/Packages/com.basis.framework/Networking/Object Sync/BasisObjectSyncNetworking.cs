using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Compression;
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
    [SerializeField]
    private bool ReleaseOwnershipOnDrop = false;
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
            BasisPickupInteractable.OnInteractEndEvent.AddListener(OnInteractEndEvent);
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
            BasisPickupInteractable.OnInteractEndEvent.RemoveListener(OnInteractEndEvent);
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
        ApplyState();
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
        return IsOwnedLocallyOnClient || CanNetworkSteal;
    }
    private void OnInteractStartEvent(BasisInput input)
    {
        // Remote grabbed pickup sets kinematic during lerp, so preserve locally expected kinematic state 
        if (BasisPickupInteractable != null && BasisPickupInteractable.KinematicWhileInteracting)
        {
            BasisPickupInteractable._previousKinematicValue = false;
        }
        _ = ClaimOwnership();
    }
    private void OnInteractEndEvent(BasisInput input)
    {
        if (!ReleaseOwnershipOnDrop) return;
        // Only release if no other hand is still interacting with the pickup.
        if (BasisPickupInteractable != null && BasisPickupInteractable.Inputs.AnyInteracting()) return;
        _ = ReleaseOwnership();
    }
    public void SetIsKinematicOnPickup(bool state)
    {
        if (BasisPickupInteractable != null && BasisPickupInteractable.RigidRef != null)
        {
            BasisPickupInteractable.RigidRef.isKinematic = state;
        }
    }
    protected override void OnOwnershipStateChanged() => ApplyState();
    /// <summary>
    /// Pure state-driven physics + driver-set assignment. Local press already ran
    /// OnInteractStart end-to-end with CanNetworkSteal=true, so no replay path.
    /// </summary>
    public void ApplyState()
    {
        #if UNITY_SERVER
        if (SelfTransform == null)
        {
            SelfTransform = transform;
            if (SelfTransform == null)
            {
                BasisDebug.LogWarning($"Skipping object sync state update because transform is missing on '{name}'.", BasisDebug.LogTag.Networking);
                return;
            }
        }
        #endif
        switch (OwnershipState)
        {
            case NetworkOwnershipState.PendingClaim:
            case NetworkOwnershipState.Local:
                BasisObjectSyncDriver.AddLocalOwner(this);
                BasisObjectSyncDriver.RemoveRemoteOwner(this);
                if (BasisPickupInteractable != null
                    && BasisPickupInteractable.KinematicWhileInteracting
                    && BasisPickupInteractable.RequiresUpdateLoop)
                {
                    // Currently held with KinematicWhileInteracting - preserve kinematic state
                }
                else
                {
                    SetIsKinematicOnPickup(false);
                }
                break;
            case NetworkOwnershipState.Remote:
            case NetworkOwnershipState.Unknown:
            case NetworkOwnershipState.PendingRelease:
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
                break;
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
