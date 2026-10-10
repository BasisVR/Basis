using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [AutoStaticsCleanup]
    public partial class BasisPickupJointInteractable : BasisPickupInteractable
    {
        [Header("Joint Hold")]
        [Tooltip("How tightly the object is pulled to the hand, independent of its mass.")]
        public float PositionSpring = 5000f;
        [Tooltip("Damping of that pull, relative to the hand's own motion.")]
        public float PositionDamper = 140f;
        [Tooltip("Strongest force in newtons the hand applies. Heavier objects lag behind, and solid objects stop the held one instead of it passing through.")]
        public float MaximumForce = 2000f;
        [Tooltip("Turn the object with the hand. Turn off for doors and levers that should only follow the hand's position.")]
        public bool DriveRotation = true;
        public float RotationSpring = 5000f;
        public float RotationDamper = 140f;
        [Tooltip("Strongest torque in newton metres the hand applies.")]
        public float MaximumTorque = 300f;
        [Tooltip("Let go automatically when the object is pulled too far from the hand, for example when it is caught behind a wall.")]
        public bool DropWhenPulledAway = true;
        [Tooltip("How far is too far, in metres at default avatar size.")]
        public float DropDistance = 0.6f;
        [Tooltip("Smooth the held object's motion between physics steps.")]
        public bool InterpolateWhileHeld = true;
        private const float teleportDistance = 1.5f;
        private const float separationArmSeconds = 1f;
        private const float separationGraceSeconds = 0.2f;
        public static System.Action<double> OnFixedSimulate;
        private static readonly List<BasisPickupJointInteractable> held = new List<BasisPickupJointInteractable>();
        private readonly List<Collider> ignoredColliders = new List<Collider>();
        private Rigidbody anchorBody;
        private ConfigurableJoint holdJoint;
        private CharacterController ignoredController;
        private RigidbodyInterpolation previousInterpolation;
        private CollisionDetectionMode previousCollisionMode;
        private Vector3 anchorLocal, bodyLocal, targetPosition, previousTargetPosition, targetHand;
        private Quaternion targetRotation = Quaternion.identity, previousTargetRotation = Quaternion.identity;
        private double targetTime, previousTargetTime;
        private float holdTime, separatedTime;
        private bool jointPhysics, holding, articulated, hasTarget, hasPreviousTarget, hasHand, separationArmed;

        protected internal override bool HoldControlsKinematic => CanHoldByJoint();

        protected internal override bool HoldFollowsHand => !articulated;

        internal bool JointHolding => holding;

        internal Rigidbody HoldAnchor => anchorBody;

        public new void Start()
        {
            KinematicWhileInteracting = false;
            base.Start();
            if (!CanHoldByJoint())
            {
                BasisDebug.LogWarning($"{name}: {nameof(BasisPickupJointInteractable)} needs a Rigidbody on its own GameObject to hold it by joint, so it is held like a regular pickup.", BasisDebug.LogTag.Pickups);
                return;
            }
            articulated = IsConstrainedByJoint(RigidRef);
            if (articulated)
            {
                LerpToHandOnPickup = false;
                GripPoint = null;
            }
        }

        private void Reset()
        {
            KinematicWhileInteracting = false;
        }

        public override void OnInteractStart(BasisInput input)
        {
            base.OnInteractStart(input);
            if (jointPhysics && !holding && IsInteractingWith(input))
            {
                ConnectHold();
            }
        }

        public override void InputUpdate()
        {
            base.InputUpdate();
            if (!holding)
            {
                return;
            }
            if (anchorBody == null || RigidRef == null || (DropWhenPulledAway && PulledAway(Time.deltaTime)))
            {
                Drop();
            }
        }

        public override void OnDestroy()
        {
            if (jointPhysics)
            {
                EndHoldPhysics();
            }
            if (anchorBody != null)
            {
                Destroy(anchorBody.gameObject);
            }
            base.OnDestroy();
        }

        protected override void BeginHoldPhysics()
        {
            if (jointPhysics)
            {
                return;
            }
            KinematicWhileInteracting = false;
            base.BeginHoldPhysics();
            if (!CanHoldByJoint())
            {
                return;
            }
            _previousKinematicValue = RigidRef.isKinematic;
            previousInterpolation = RigidRef.interpolation;
            previousCollisionMode = RigidRef.collisionDetectionMode;
            RigidRef.isKinematic = false;
            if (InterpolateWhileHeld)
            {
                RigidRef.interpolation = RigidbodyInterpolation.Interpolate;
            }
            if (previousCollisionMode == CollisionDetectionMode.Discrete)
            {
                RigidRef.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            jointPhysics = true;
        }

        protected override void EndHoldPhysics()
        {
            if (!jointPhysics)
            {
                base.EndHoldPhysics();
                return;
            }
            jointPhysics = false;
            ReleaseHold();
            KinematicWhileInteracting = false;
            base.EndHoldPhysics();
            if (RigidRef != null)
            {
                RigidRef.interpolation = previousInterpolation;
                RigidRef.collisionDetectionMode = previousCollisionMode;
                RigidRef.isKinematic = _previousKinematicValue;
            }
        }

        protected override void ApplyHeldPose(ref Vector3 position, ref Quaternion rotation)
        {
            if (!holding)
            {
                base.ApplyHeldPose(ref position, ref rotation);
                return;
            }
            SetHoldTarget(position, rotation, InputConstraint.sources[0].position, Time.timeAsDouble);
            transform.GetPositionAndRotation(out position, out rotation);
        }

        private static void SimulateHeld(double time)
        {
            for (int index = held.Count - 1; index >= 0; index--)
            {
                BasisPickupJointInteractable pickup = held[index];
                if (pickup == null)
                {
                    held.RemoveAt(index);
                    continue;
                }
                pickup.StepHold(time);
            }
            if (held.Count == 0)
            {
                OnFixedSimulate -= SimulateHeld;
            }
        }

        internal bool BeginJointHold()
        {
            BeginHoldPhysics();
            return jointPhysics && ConnectHold();
        }

        internal void EndJointHold()
        {
            EndHoldPhysics();
        }

        internal void SetHoldTarget(Vector3 position, Quaternion rotation, Vector3 hand, double time)
        {
            float jump = BasisPlayerInteract.AvatarScaledRange(teleportDistance);
            if (hasHand && (hand - targetHand).sqrMagnitude > jump * jump)
            {
                SnapHold(position, rotation);
            }
            else if (hasTarget && time > targetTime)
            {
                previousTargetPosition = targetPosition;
                previousTargetRotation = targetRotation;
                previousTargetTime = targetTime;
                hasPreviousTarget = true;
            }
            targetPosition = position;
            targetRotation = rotation;
            targetTime = time;
            targetHand = hand;
            hasTarget = true;
            hasHand = true;
        }

        internal static void Extrapolate(Vector3 fromPosition, Quaternion fromRotation, double fromTime, Vector3 toPosition, Quaternion toRotation, double toTime, double time, out Vector3 position, out Quaternion rotation)
        {
            double span = toTime - fromTime;
            float ahead = span > 0d ? Mathf.Clamp01((float)((time - toTime) / span)) : 0f;
            position = toPosition + (toPosition - fromPosition) * ahead;
            if (ahead <= 0f)
            {
                rotation = toRotation;
                return;
            }
            if (Quaternion.Dot(fromRotation, toRotation) < 0f)
            {
                fromRotation = new Quaternion(-fromRotation.x, -fromRotation.y, -fromRotation.z, -fromRotation.w);
            }
            rotation = Quaternion.SlerpUnclamped(fromRotation, toRotation, 1f + ahead);
        }

        internal static bool IsConstrainedByJoint(Rigidbody body)
        {
            Joint[] joints = body.GetComponents<Joint>();
            for (int index = 0; index < joints.Length; index++)
            {
                if (joints[index] is not ConfigurableJoint configurable || !IsInert(configurable))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsInert(ConfigurableJoint joint)
        {
            return joint.xMotion == ConfigurableJointMotion.Free && joint.yMotion == ConfigurableJointMotion.Free && joint.zMotion == ConfigurableJointMotion.Free
                && joint.angularXMotion == ConfigurableJointMotion.Free && joint.angularYMotion == ConfigurableJointMotion.Free && joint.angularZMotion == ConfigurableJointMotion.Free
                && IsIdle(joint.xDrive) && IsIdle(joint.yDrive) && IsIdle(joint.zDrive) && IsIdle(joint.angularXDrive) && IsIdle(joint.angularYZDrive) && IsIdle(joint.slerpDrive);
        }

        private static bool IsIdle(JointDrive drive)
        {
            return drive.positionSpring <= 0f && drive.positionDamper <= 0f;
        }

        private bool CanHoldByJoint()
        {
            return RigidRef != null && RigidRef.transform == transform;
        }

        private bool ConnectHold()
        {
            if (holding || !CanHoldByJoint())
            {
                return false;
            }
            EnsureHoldAnchor();
            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            RigidRef.position = position;
            RigidRef.rotation = rotation;

            Vector3 grab = GetClosestPoint(position + rotation * HandOnObject());
            anchorLocal = Quaternion.Inverse(rotation) * (grab - position);
            bodyLocal = transform.InverseTransformPoint(grab);

            anchorBody.transform.SetPositionAndRotation(position, rotation);
            anchorBody.gameObject.SetActive(true);
            holdJoint.connectedBody = RigidRef;
            holdJoint.anchor = anchorLocal;
            holdJoint.connectedAnchor = bodyLocal;
            ApplyDrives();

            targetPosition = position;
            targetRotation = rotation;
            targetTime = Time.timeAsDouble;
            hasTarget = true;
            hasPreviousTarget = false;
            hasHand = false;
            holdTime = 0f;
            separatedTime = 0f;
            separationArmed = false;
            holding = true;
            held.Add(this);
            if (held.Count == 1)
            {
                OnFixedSimulate += SimulateHeld;
            }
            IgnorePlayerCollisions();
            return true;
        }

        private void ReleaseHold()
        {
            if (!holding)
            {
                return;
            }
            holding = false;
            held.Remove(this);
            if (held.Count == 0)
            {
                OnFixedSimulate -= SimulateHeld;
            }
            hasTarget = false;
            hasPreviousTarget = false;
            if (anchorBody != null)
            {
                anchorBody.gameObject.SetActive(false);
            }
            if (holdJoint != null)
            {
                holdJoint.connectedBody = null;
            }
            RestorePlayerCollisions();
        }

        private Vector3 HandOnObject()
        {
            if (InputConstraint == null || InputConstraint.sources == null || InputConstraint.sources.Length == 0)
            {
                return Vector3.zero;
            }
            BasisConstraintSourceData source = InputConstraint.sources[0];
            Quaternion offsetRotation = source.rotationOffset;
            if (Quaternion.Dot(offsetRotation, offsetRotation) < 1e-6f)
            {
                offsetRotation = Quaternion.identity;
            }
            return -(Quaternion.Inverse(offsetRotation) * source.positionOffset);
        }

        private void StepHold(double time)
        {
            if (!hasTarget || anchorBody == null || RigidRef == null)
            {
                return;
            }
            Vector3 position = targetPosition;
            Quaternion rotation = targetRotation;
            if (hasPreviousTarget)
            {
                Extrapolate(previousTargetPosition, previousTargetRotation, previousTargetTime, targetPosition, targetRotation, targetTime, time, out position, out rotation);
            }
            anchorBody.Move(position, rotation);
            RigidRef.WakeUp();
        }

        private void SnapHold(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            if (RigidRef != null)
            {
                RigidRef.position = position;
                RigidRef.rotation = rotation;
                RigidRef.linearVelocity = Vector3.zero;
                RigidRef.angularVelocity = Vector3.zero;
            }
            if (anchorBody != null)
            {
                anchorBody.position = position;
                anchorBody.rotation = rotation;
            }
            hasPreviousTarget = false;
            separatedTime = 0f;
        }

        internal bool PulledAway(float deltaTime)
        {
            holdTime += deltaTime;
            float limit = BasisPlayerInteract.AvatarScaledRange(DropDistance);
            Vector3 offset = transform.TransformPoint(bodyLocal) - (targetPosition + targetRotation * anchorLocal);
            if (offset.sqrMagnitude <= limit * limit)
            {
                separationArmed = true;
                separatedTime = 0f;
                return false;
            }
            if (!separationArmed && holdTime < separationArmSeconds)
            {
                return false;
            }
            separatedTime += deltaTime;
            return separatedTime >= separationGraceSeconds;
        }

        private void EnsureHoldAnchor()
        {
            if (anchorBody != null)
            {
                return;
            }
            GameObject anchorObject = new GameObject(name + " Hold Anchor");
            anchorObject.SetActive(false);
            anchorBody = anchorObject.AddComponent<Rigidbody>();
            anchorBody.isKinematic = true;
            anchorBody.useGravity = false;
            holdJoint = anchorObject.AddComponent<ConfigurableJoint>();
            holdJoint.autoConfigureConnectedAnchor = false;
            holdJoint.rotationDriveMode = RotationDriveMode.Slerp;
        }

        private void ApplyDrives()
        {
            JointDrive linear = new JointDrive { positionSpring = Mathf.Max(0f, PositionSpring), positionDamper = Mathf.Max(0f, PositionDamper), maximumForce = Mathf.Max(0f, MaximumForce), useAcceleration = true };
            holdJoint.xDrive = linear;
            holdJoint.yDrive = linear;
            holdJoint.zDrive = linear;
            holdJoint.slerpDrive = DriveRotation ? new JointDrive { positionSpring = Mathf.Max(0f, RotationSpring), positionDamper = Mathf.Max(0f, RotationDamper), maximumForce = Mathf.Max(0f, MaximumTorque), useAcceleration = true } : default;
        }

        private void IgnorePlayerCollisions()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            CharacterController controller = player != null ? player.LocalCharacterDriver.characterController : null;
            if (controller == null || !controller.enabled || !controller.gameObject.activeInHierarchy)
            {
                return;
            }
            int playerLayer = controller.gameObject.layer;
            RigidRef.GetComponentsInChildren(false, ignoredColliders);
            for (int index = ignoredColliders.Count - 1; index >= 0; index--)
            {
                Collider collider = ignoredColliders[index];
                if (collider.isTrigger || !collider.enabled || collider.attachedRigidbody != RigidRef || Physics.GetIgnoreLayerCollision(collider.gameObject.layer, playerLayer))
                {
                    ignoredColliders.RemoveAt(index);
                    continue;
                }
                Physics.IgnoreCollision(collider, controller, true);
            }
            ignoredController = controller;
        }

        private void RestorePlayerCollisions()
        {
            CharacterController controller = ignoredController;
            ignoredController = null;
            if (controller != null && controller.enabled && controller.gameObject.activeInHierarchy)
            {
                Bounds playerBounds = controller.bounds;
                for (int index = 0; index < ignoredColliders.Count; index++)
                {
                    Collider collider = ignoredColliders[index];
                    if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.bounds.Intersects(playerBounds))
                    {
                        Physics.IgnoreCollision(collider, controller, false);
                    }
                }
            }
            ignoredColliders.Clear();
        }
    }
}
