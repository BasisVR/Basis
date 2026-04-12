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
                ctx.characterController.detectCollisions = true;
                ctx.characterController.enabled = true;
            }
            ctx.currentVerticalSpeed = 0f;
        }

        public void Exit(BasisLocalCharacterDriver ctx) { }

        public void Tick(BasisLocalCharacterDriver ctx, float dt)
        {
            Vector3 up = ctx.UpDirection;

            // Project head forward onto the plane perpendicular to gravity
            Quaternion headRot = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation;
            Vector3 flatForward = headRot * Vector3.forward;
            flatForward -= up * Vector3.Dot(flatForward, up);
            if (flatForward.sqrMagnitude < 0.0001f)
            {
                flatForward = -(headRot * Vector3.up);
                flatForward -= up * Vector3.Dot(flatForward, up);
            }
            Quaternion facing = Quaternion.LookRotation(flatForward.normalized, up);

            // Planar
            Vector3 planar = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            ctx.CurrentSpeed =
                math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale)
                + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost;

            Vector3 move = facing * planar * ctx.CurrentSpeed * dt;

            // Vertical input along gravity-relative up axis
            move += up * (ctx.GetVerticalMovement() * ctx.CurrentSpeed * dt);

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
