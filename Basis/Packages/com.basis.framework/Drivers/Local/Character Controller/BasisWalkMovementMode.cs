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

            Quaternion facing = BasisLocalCharacterDriver.GetMovementFacing();

            Vector3 inputDir = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            // Speed model (kept from original), then scaled to the avatar. The gait system derives every
            // step parameter from the avatar's own leg via the Froude number v/sqrt(g*L); leaving speed a
            // fixed m/s inflated v-hat by 1/sqrt(scale) on a small avatar, which pinned the bob/sway
            // amplitudes and step urgency at maximum and roughly doubled cadence at half size.
            ctx.CurrentSpeed = (math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale) + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost)
                * BasisLocalCharacterDriver.LocomotionSpeedScale();

            Vector3 move = facing * inputDir * ctx.CurrentSpeed * dt;

            // Ground & gravity
            ctx.GroundCheck(dt);


            if (ctx.CanJump && ctx.HasJumpAction && !ctx.MovementLock)
            {
                // Apex in metres, so it scales linearly with the avatar (v0 = sqrt(2gh) then goes as sqrt(L)).
                ctx.currentVerticalSpeed = Mathf.Sqrt(ctx.jumpHeight * BasisLocalCharacterDriver.AvatarSizeRatio() * -2f * ctx.gravityValue);
                ctx.coyoteTimeCounter = 0f; // Consume coyote time to prevent double jumps
                ctx.JustJumped?.Invoke();
            }
            else
            {
                ctx.currentVerticalSpeed += ctx.gravityValue * dt;
            }

            // Was clamping against gravityValue — an m/s^2 constant used as an m/s cap.
            ctx.currentVerticalSpeed = Mathf.Max(ctx.currentVerticalSpeed, -BasisLocalCharacterDriver.TerminalVelocity());
            ctx.HasJumpAction = false;

            move.y = ctx.currentVerticalSpeed * dt;

            if (ctx.MovementLock)
            {
                move.x = 0;
                move.z = 0;
            }

            ctx.Flags = ctx.characterController.Move(move);
            ctx.BasisLocalPlayerTransform.GetPositionAndRotation(out ctx.CurrentPosition, out ctx.CurrentRotation);
        }
    }
}
