using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

public class PickupJointInteractable : InteractableObject
{
    [Header("Pickup Joint Settings")]


    [Header("References")]
    public Collider ColliderRef;
    public Rigidbody RigidRef;
    private ConfigurableJoint JointRef;
    private bool lastDetectCollisions;

    private Transform anchor;
    private float offsetDistance;
    private Quaternion offsetRot;


    bool isHeld = false;
    // public BasisObjectSyncNetworking syncNetworking;

    void Start()
    {
        if (RigidRef == null)
        {
            TryGetComponent(out RigidRef);
        }
        if (ColliderRef == null)
        {
            TryGetComponent(out ColliderRef);
        }
        if (JointRef == null)
        {
            TryGetComponent(out JointRef);
        }

        JointRef.autoConfigureConnectedAnchor = false;
        JointRef.configuredInWorldSpace = true;
        LockJoint(false);


        // TODO: netsync
        // syncNetworking = GetComponent<BasisObjectSyncNetworking>();
    }

    public void LockJoint(bool isLocked)
    {
        if (isLocked)
        {
            JointRef.xMotion = ConfigurableJointMotion.Locked;
            JointRef.yMotion = ConfigurableJointMotion.Locked;
            JointRef.zMotion = ConfigurableJointMotion.Locked;

            JointRef.angularXMotion = ConfigurableJointMotion.Locked;
            JointRef.angularYMotion = ConfigurableJointMotion.Locked;
            JointRef.angularZMotion = ConfigurableJointMotion.Locked;
        }
        else
        {
            JointRef.xMotion = ConfigurableJointMotion.Free;
            JointRef.yMotion = ConfigurableJointMotion.Free;
            JointRef.zMotion = ConfigurableJointMotion.Free;

            JointRef.angularXMotion = ConfigurableJointMotion.Free;
            JointRef.angularYMotion = ConfigurableJointMotion.Free;
            JointRef.angularZMotion = ConfigurableJointMotion.Free;
        }
    }

    public void FixedUpdate()
    {
        if (isHeld)
        {
            // set world position and rotation of the joint
            JointRef.connectedAnchor = anchor.position + anchor.forward * offsetDistance;
            JointRef.targetRotation = Quaternion.Inverse(anchor.rotation) * offsetRot;
            // TODO: send postition to network while held
        }
    }

    public override bool CanHover(BasisInput input)
    {
        throw new System.NotImplementedException();
    }

    public override bool IsHoveredBy(BasisInput input)
    {
        throw new System.NotImplementedException();
    }

    public override bool CanInteract(BasisInput input)
    {
        throw new System.NotImplementedException();
    }

    public override bool IsInteractingWith(BasisInput input)
    {
        throw new System.NotImplementedException();
    }

    public override void OnInteractStart(BasisInput input)
    {
        // save object distance and rotation
        anchor = input.transform;
        offsetDistance = Vector3.Distance(transform.position, input.transform.position);
        offsetRot = gameObject.transform.rotation;

        // lock all axes of the joint
        LockJoint(true);

        // // Update the networked data (Storeddata) to reflect the position, rotation, and scale
        // syncNetworking.Storeddata.Position = transform.position;
        // syncNetworking.Storeddata.Rotation = transform.rotation;
        // syncNetworking.Storeddata.Scale = transform.localScale;

        // Set ownership to the local player when they pick up the object
        // syncNetworking.IsOwner = true;
    }

    public override void OnInteractEnd(BasisInput input)
    {
        LockJoint(false);
    }

    public override void OnHoverStart(BasisInput input)
    {
        throw new System.NotImplementedException();
    }

    public override void OnHoverEnd(BasisInput input, bool willInteract)
    {
        throw new System.NotImplementedException();
    }

    public override void InputUpdate()
    {
        throw new System.NotImplementedException();
    }
}
