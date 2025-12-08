using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.BasisCharacterController
{
    public class BasisNoClipMovementMode : IMovementMode
    {
        public string Name => "NoClip";
        public CollisionHandling Collision => CollisionHandling.Ghost;

        // Trigger-only rig we enable during noclip
        private Rigidbody _triggerBody;
        private CapsuleCollider _triggerCapsule;

        public void Enter(BasisLocalCharacterDriver ctx)
        {
            // Fully ghost the CharacterController so it won’t push/depenetrate
            if (ctx.characterController != null)
            {
                ctx.characterController.detectCollisions = false;
                ctx.characterController.enabled = false;
            }

            // Ensure a trigger-only probe exists and matches the CC size
            EnsureTriggerProbe(ctx);

            // enable the trigger probe
            _triggerBody.gameObject.SetActive(true);
            _triggerCapsule.enabled = true;

            ctx.currentVerticalSpeed = 0f;
            ctx.groundedPlayer = false;
            ctx.IsFalling = false;
        }

        public void Exit(BasisLocalCharacterDriver ctx)
        {
            // Re-enable CC for other modes
            if (ctx.characterController != null)
            {
                ctx.characterController.detectCollisions = true;
                ctx.characterController.enabled = true;
            }

            // Disable the probe (keep it around to avoid allocations)
            if (_triggerCapsule != null) GameObject.Destroy(_triggerCapsule);
            if (_triggerBody != null) GameObject.Destroy(_triggerBody);
        }

        public void Tick(BasisLocalCharacterDriver ctx, float dt)
        {
            // Make forward be camera yaw on the horizontal plane
            var yaw = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation.eulerAngles;
            yaw.x = 0f; yaw.z = 0f;
            Quaternion facing = Quaternion.Euler(yaw);

            // Same speed model you already use
            ctx.CurrentSpeed =
                math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale)
                + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost;

            // Planar input
            Vector3 planar = new Vector3(ctx.MovementVector.x, 0f, ctx.MovementVector.y).normalized;
            Vector3 move = facing * planar * ctx.CurrentSpeed * dt;

            // Vertical input
            move.y = ctx.GetVerticalMovement() * ctx.CurrentSpeed * dt;
            ctx.HasJumpAction = false;

            if (ctx.MovementLock) move = Vector3.zero;

            // Ghost move: translate transform directly
            ctx.BasisLocalPlayerTransform.position += move;

            // Keep trigger probe perfectly aligned with the player
            if (_triggerBody != null)
            {
                _triggerBody.position = ctx.BasisLocalPlayerTransform.position;
                _triggerBody.rotation = ctx.BasisLocalPlayerTransform.rotation;
            }
            var cc = ctx.characterController;
            if (cc != null && _triggerCapsule != null)
            {
                _triggerCapsule.center = cc.center;
                _triggerCapsule.radius = cc.radius;
                _triggerCapsule.height = cc.height;
                _triggerCapsule.direction = 1; // Y axis like CharacterController
            }
            // Sync state
            ctx.BasisLocalPlayerTransform.GetPositionAndRotation(out ctx.CurrentPosition, out ctx.CurrentRotation);
            ctx.groundedPlayer = false;
            ctx.IsFalling = false;
            ctx.Flags = CollisionFlags.None;
        }

        private void EnsureTriggerProbe(BasisLocalCharacterDriver ctx)
        {
            if (_triggerBody != null && _triggerCapsule != null) return;

            // Put the probe on the same GameObject as the player (or create a child)
            var go = ctx.BasisLocalPlayerTransform.gameObject;
            _triggerBody = BasisHelpers.GetOrAddComponent<Rigidbody>(go);
            _triggerBody.isKinematic = true;
            _triggerBody.useGravity = false;
            _triggerCapsule = BasisHelpers.GetOrAddComponent<CapsuleCollider>(go);
            _triggerCapsule.isTrigger = true;

            // Match CC dimensions so overlaps are accurate
            var cc = ctx.characterController;
            if (cc != null)
            {
                _triggerCapsule.center = cc.center;
                _triggerCapsule.radius = cc.radius;
                _triggerCapsule.height = cc.height;
                _triggerCapsule.direction = 1; // Y axis like CharacterController
            }

            // Make sure physics queries will consider triggers (usually true by default)
            Physics.queriesHitTriggers = true;
        }
    }
}
