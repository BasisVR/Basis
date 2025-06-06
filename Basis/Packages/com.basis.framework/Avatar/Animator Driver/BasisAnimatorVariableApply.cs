using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
    [System.Serializable]
    public class BasisAnimatorVariableApply
    {
        public Animator Animator;
        [SerializeField]
        public BasisAvatarAnimatorHash BasisAvatarAnimatorHash = new BasisAvatarAnimatorHash();
        [SerializeField]
        public BasisAnimatorVariables BasisAnimatorVariables = new BasisAnimatorVariables();
        public void UpdateAnimator(float Scale)
        {
            // Check if values have changed before applying updates
            if (BasisAnimatorVariables.cachedAnimSpeed != BasisAnimatorVariables.AnimationsCurrentSpeed)
            {
                Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentSpeed, BasisAnimatorVariables.AnimationsCurrentSpeed);
                BasisAnimatorVariables.cachedAnimSpeed = BasisAnimatorVariables.AnimationsCurrentSpeed;
            }

            if (BasisAnimatorVariables.cachedCrouchBlend != BasisAnimatorVariables.CrouchBlend)
            {
                Animator.SetFloat(BasisAvatarAnimatorHash.HashCrouchBlend, BasisAnimatorVariables.CrouchBlend);
                BasisAnimatorVariables.cachedCrouchBlend = BasisAnimatorVariables.CrouchBlend;
            }

            if (BasisAnimatorVariables.cachedIsMoving != BasisAnimatorVariables.isMoving)
            {
                Animator.SetBool(BasisAvatarAnimatorHash.HashMovingState, BasisAnimatorVariables.isMoving);
                BasisAnimatorVariables.cachedIsMoving = BasisAnimatorVariables.isMoving;
            }
            if (BasisAnimatorVariables.cachedIsCrouching != BasisAnimatorVariables.IsCrouching)
            {
                Animator.SetBool(BasisAvatarAnimatorHash.HashCrouchedState, BasisAnimatorVariables.IsCrouching);
                BasisAnimatorVariables.cachedIsCrouching = BasisAnimatorVariables.IsCrouching;
            }
            if (BasisAnimatorVariables.cachedIsFalling != BasisAnimatorVariables.IsFalling)
            {
                Animator.SetBool(BasisAvatarAnimatorHash.HashIsFalling, BasisAnimatorVariables.IsFalling);
                BasisAnimatorVariables.cachedIsFalling = BasisAnimatorVariables.IsFalling;
            }

            float horizontalMovement = BasisAnimatorVariables.Velocity.x / Scale;
            if (BasisAnimatorVariables.cachedHorizontalMovement != horizontalMovement)
            {
                Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentHorizontalMovement, horizontalMovement);
                BasisAnimatorVariables.cachedHorizontalMovement = horizontalMovement;
            }

            float verticalMovement = BasisAnimatorVariables.Velocity.z / Scale;
            if (BasisAnimatorVariables.cachedVerticalMovement != verticalMovement)
            {
                Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentVerticalMovement, verticalMovement);
                BasisAnimatorVariables.cachedVerticalMovement = verticalMovement;
            }
            if (BasisAnimatorVariables.cachedIsJumping != BasisAnimatorVariables.IsJumping)
            {
                Animator.SetBool(BasisAvatarAnimatorHash.HashIsJumping, BasisAnimatorVariables.IsJumping);
                BasisAnimatorVariables.cachedIsJumping = BasisAnimatorVariables.IsJumping;
            }
            // UpdateJumpState();
            if (IsStopped != false)
            {
                IsStopped = false;
                Animator.SetBool(BasisAvatarAnimatorHash.IsPaused, false);
            }
        }
        public bool IsStopped = false;
        public void StopAll()
        {
            BasisDebug.Log("Stopping all");
            // Set all animator boolean parameters to false
            Animator.SetBool(BasisAvatarAnimatorHash.HashMovingState, false);
            Animator.SetBool(BasisAvatarAnimatorHash.HashCrouchedState, false);
            Animator.SetBool(BasisAvatarAnimatorHash.HashIsFalling, false);

            // Update cached variables for boolean states
            BasisAnimatorVariables.cachedIsMoving = false;
            BasisAnimatorVariables.isMoving = false;

            BasisAnimatorVariables.cachedIsCrouching = false;
            BasisAnimatorVariables.IsCrouching = false;

            BasisAnimatorVariables.cachedIsFalling = false;
            BasisAnimatorVariables.IsFalling = false;

            // Set all animator float parameters to defaults
            Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentSpeed, 0f);
            Animator.SetFloat(BasisAvatarAnimatorHash.HashCrouchBlend, 1f);
            Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentHorizontalMovement, 0f);
            Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentVerticalMovement, 0f);

            // Update cached variables for float states
            BasisAnimatorVariables.cachedAnimSpeed = 0f;
            BasisAnimatorVariables.AnimationsCurrentSpeed = 0f;

            BasisAnimatorVariables.cachedCrouchBlend = 1f;
            BasisAnimatorVariables.CrouchBlend = 1f;

            BasisAnimatorVariables.cachedHorizontalMovement = 0f;
            BasisAnimatorVariables.cachedVerticalMovement = 0f;

            BasisAnimatorVariables.Velocity = Vector3.zero; // Assuming Velocity is a Vector3
            IsStopped = true;
            Animator.SetBool(BasisAvatarAnimatorHash.IsPaused, true);
        }
        public void LoadCachedAnimatorHashes(Animator animator)
        {
            Animator = animator;
            BasisAvatarAnimatorHash.HashCurrentHorizontalMovement = Animator.StringToHash("CurrentHorizontalMovement");
            BasisAvatarAnimatorHash.HashCurrentVerticalMovement = Animator.StringToHash("CurrentVerticalMovement");
            BasisAvatarAnimatorHash.HashCurrentSpeed = Animator.StringToHash("CurrentSpeed");
            BasisAvatarAnimatorHash.HashCrouchBlend = Animator.StringToHash("CrouchBlend");
            BasisAvatarAnimatorHash.HashCrouchedState = Animator.StringToHash("CrouchedState");
            BasisAvatarAnimatorHash.HashMovingState = Animator.StringToHash("MovingState");

            BasisAvatarAnimatorHash.IsPaused = Animator.StringToHash("IsPaused");

            BasisAvatarAnimatorHash.HashIsFalling = Animator.StringToHash("IsFalling");
            BasisAvatarAnimatorHash.HashIsLanding = Animator.StringToHash("IsLanding");
            BasisAvatarAnimatorHash.HashIsJumping = Animator.StringToHash("IsJumping");
        }
        public void UpdateIsLandingState()
        {
            Animator.SetTrigger(BasisAvatarAnimatorHash.HashIsLanding);
        }
    }
}
