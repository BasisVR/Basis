using Basis.Scripts.Device_Management.Devices;
using BattlePhaze.SettingsManager.Intergrations;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public class BasisPickupJointInteractable : BasisInteractableObject
    {
        [Header("Pickup Joint Settings")]
        [Header("References")]
        public Collider ColliderRef;
        public Rigidbody RigidRef;
        private ConfigurableJoint JointRef;
        private Transform anchor;
        private float offsetDistance;
        private Quaternion offsetRot;
        bool isHeld = false;

        public void Start()
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
            input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume / 80);
            // save object distance and rotation
            anchor = input.transform;

            this.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion rotation);
            offsetDistance = Vector3.Distance(Position, input.transform.position);

            offsetRot = rotation;

            // lock all axes of the joint
            LockJoint(true);
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
}
