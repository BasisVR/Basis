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
            ctx.SyncStanceSpeedSource();

            Quaternion facing = BasisLocalCharacterDriver.GetMovementFacing();

            // Planar
            Vector3 planar = new Vector3(ctx.MovementVector.x, 0, ctx.MovementVector.y).normalized;

            ctx.CurrentSpeed =
                (math.lerp(ctx.MinimumMovementSpeed, ctx.MaximumMovementSpeed, ctx.MovementSpeedScale)
                + ctx.MinimumMovementSpeed * ctx.MovementSpeedBoost) * BasisLocalCharacterDriver.LocomotionSpeedScale();

            Vector3 move = facing * planar * ctx.CurrentSpeed * dt;

            // ===== Vertical input (held) =====
            move.y = ctx.GetVerticalMovement() * ctx.CurrentSpeed * dt;

            // Clear tap
            ctx.HasJumpAction = false;

            if (ctx.MovementLock) move = Vector3.zero;

            bool pushed = ctx.HasExternalVelocity;
            if (pushed) move = BasisLocalCharacterDriver.CombineMotion(move, ctx.ExternalVelocity * dt);

            using (BasisLocalPlayerMarkers.MovePhysics.Auto())
            {
                ctx.Flags = ctx.characterController.Move(move);
            }
            // PhysX writes the root transform directly; the pose cache cannot observe it.
            BasisLocalPose.InvalidateAll();
            ctx.BasisLocalPlayerTransform.GetPose(out ctx.CurrentPosition, out ctx.CurrentRotation);

            if (pushed)
            {
                if (ctx.Flags != CollisionFlags.None) ctx.ClipExternalVelocity(move, ctx.CurrentPosition - ctx.MoveStartPosition, dt);
                ctx.DecayExternalVelocity(dt);
            }

            // Flight state
            ctx.groundedPlayer = false;
            ctx.IsFalling = false;
        }
    }
}
