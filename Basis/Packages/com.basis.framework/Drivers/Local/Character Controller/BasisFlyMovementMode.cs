using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers; // for BasisLocalInputActions
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.BasisCharacterController
{
    public class BasisFlyMovementMode : IMovementMode
    {
        public string Name => "Fly";
        public CollisionHandling Collision => CollisionHandling.Solid;

        public void Enter(BasisLocalCharacterDriver ctx)
        {
            if (ctx.characterController != null)
            {
                ctx.characterController.detectCollisions = true;  // solid, but no gravity
                ctx.characterController.enabled = true;
            }
            ctx.currentVerticalSpeed = 0f;
        }

        public void Exit(BasisLocalCharacterDriver ctx) { }

        public void Tick(BasisLocalCharacterDriver ctx, float dt)
        {
            // Flatten yaw for input space
            var e = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation.eulerAngles;
            e.x = e.z = 0;
            Quaternion facing = Quaternion.Euler(e);

            // Planar
            Vector3 planar = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            ctx.CurrentSpeed =
                math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale)
                + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost;

            Vector3 move = facing * planar * ctx.CurrentSpeed * dt;

            // ===== Vertical input (held) =====
            float ascendHeld = 0f;
            float descendHeld = 0f;

            // Prefer your input system if available:
            if (BasisLocalInputActions.Instance != null)
            {
                ascendHeld = BasisLocalInputActions.Instance.IsJumpHeld ? 1f : 0f;
                descendHeld = BasisLocalInputActions.Instance.IsCrouchHeld ? 1f : 0f;
            }

            if (ctx.HasJumpAction) ascendHeld = 1f;

            float verticalInput = ascendHeld - descendHeld; // -1..+1
            move.y = verticalInput * ctx.CurrentSpeed * dt;

            // Clear tap
            ctx.HasJumpAction = false;

            if (ctx.MovementLock) move = Vector3.zero;

            ctx.Flags = ctx.characterController.Move(move);
            ctx.BasisLocalPlayerTransform.GetPositionAndRotation(out ctx.CurrentPosition, out ctx.CurrentRotation);

            // Flight state
            ctx.groundedPlayer = false;
            ctx.IsFalling = false;
        }
    }
}
