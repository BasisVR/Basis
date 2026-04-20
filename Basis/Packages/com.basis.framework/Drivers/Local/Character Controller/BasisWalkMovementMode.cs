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

            // Project head forward onto horizontal plane (avoids gimbal lock near ±90° pitch)
            Quaternion headRot = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation;
            Vector3 flatForward = headRot * Vector3.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.0001f)
            {
                flatForward = -(headRot * Vector3.up);
                flatForward.y = 0f;
            }
            Quaternion facing = Quaternion.LookRotation(flatForward.normalized, Vector3.up);

            Vector3 inputDir = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            // Speed model (kept from original)
            ctx.CurrentSpeed = math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale) + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost;

            Vector3 move = facing * inputDir * ctx.CurrentSpeed * dt;

            // Ground & gravity
            ctx.GroundCheck(dt);


            if (ctx.CanJump && ctx.HasJumpAction && !ctx.MovementLock)
            {
                ctx.currentVerticalSpeed = Mathf.Sqrt(ctx.jumpHeight * -2f * ctx.gravityValue);
                ctx.coyoteTimeCounter = 0f; // Consume coyote time to prevent double jumps
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
                move.x = 0;
                move.z = 0;
            }

            ctx.Flags = ctx.characterController.Move(move);
            ctx.BasisLocalPlayerTransform.GetPositionAndRotation(out ctx.CurrentPosition, out ctx.CurrentRotation);
        }
    }
}
