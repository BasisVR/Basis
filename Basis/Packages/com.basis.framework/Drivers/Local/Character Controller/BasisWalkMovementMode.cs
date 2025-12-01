using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.BasisCharacterController
{
    public class BasisWalkMovementMode : IMovementMode
    {
        public string Name => "Walk";
        public CollisionHandling Collision => CollisionHandling.Solid;

        public void Enter(BasisLocalCharacterDriver ctx)
        {
            if (ctx.characterController != null)
            {
                ctx.characterController.detectCollisions = true;
                ctx.characterController.enabled = true;
            }
        }

        public void Exit(BasisLocalCharacterDriver ctx) { }

        public void Tick(BasisLocalCharacterDriver ctx, float dt)
        {
            if (ctx.CrouchBlendDelta != 0)
            {
                ctx.UpdateCrouchBlend(ctx.CrouchBlendDelta);
            }

            // Flatten head yaw for input space
            var rot = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation.eulerAngles;
            rot.x = rot.z = 0;
            Quaternion facing = Quaternion.Euler(rot);

            Vector3 inputDir = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            // Speed model (kept from original)
            ctx.CurrentSpeed = math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale) + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost;

            Vector3 move = facing * inputDir * ctx.CurrentSpeed * dt;

            // Ground & gravity
            ctx.GroundCheck();

            if (ctx.groundedPlayer && ctx.HasJumpAction && !ctx.MovementLock)
            {
                ctx.currentVerticalSpeed = Mathf.Sqrt(ctx.jumpHeight * -2f * ctx.gravityValue);
                ctx.JustJumped?.Invoke();
            }
            else
            {
                ctx.currentVerticalSpeed += ctx.gravityValue * dt;
            }

            ctx.currentVerticalSpeed = Mathf.Max(ctx.currentVerticalSpeed, -Mathf.Abs(ctx.gravityValue));
            ctx.HasJumpAction = false;

            move.y = ctx.currentVerticalSpeed * dt;

            if (ctx.MovementLock)
            {
                move = Vector3.zero;
            }

            ctx.Flags = ctx.characterController.Move(move);
            ctx.BasisLocalPlayerTransform.GetPositionAndRotation(out ctx.CurrentPosition, out ctx.CurrentRotation);
        }
    }
}
