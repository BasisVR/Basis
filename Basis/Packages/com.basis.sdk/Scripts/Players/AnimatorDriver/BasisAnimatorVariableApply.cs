using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
    /// <summary>
    /// Applies cached animator variables to an <see cref="Animator"/> efficiently.
    /// Only writes parameters when values have changed to avoid unnecessary
    /// Animator graph work and GC allocations.
    /// </summary>
    [System.Serializable]
    public class BasisAnimatorVariableApply
    {
        /// <summary>
        /// Target animator to update.
        /// </summary>
        public Animator Animator;

        /// <summary>
        /// Precomputed animator parameter hashes for fast access.
        /// </summary>
        [SerializeField]
        public BasisAvatarAnimatorHash BasisAvatarAnimatorHash = new BasisAvatarAnimatorHash();

        /// <summary>
        /// Cached and current animator state variables.
        /// </summary>
        [SerializeField]
        public BasisAnimatorVariables BasisAnimatorVariables = new BasisAnimatorVariables();

        /// <summary>
        /// Applies updated parameters to <see cref="Animator"/> if values changed.
        /// </summary>
        public void UpdateAnimator()
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

            float horizontalMovement = BasisAnimatorVariables.Velocity.x;
            if (BasisAnimatorVariables.cachedHorizontalMovement != horizontalMovement)
            {
                Animator.SetFloat(BasisAvatarAnimatorHash.HashCurrentHorizontalMovement, horizontalMovement);
                BasisAnimatorVariables.cachedHorizontalMovement = horizontalMovement;
            }

            float verticalMovement = BasisAnimatorVariables.Velocity.z;
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

            // Clear pause flag if we were previously stopped
            if (IsStopped != false)
            {
                IsStopped = false;
                Animator.SetBool(BasisAvatarAnimatorHash.IsPaused, false);
            }
        }

        /// <summary>
        /// True if the animator has been globally paused via <see cref="StopAll"/>.
        /// </summary>
        public bool IsStopped = false;

        /// <summary>
        /// Resets all known animator parameters to a safe default and pauses the animator.
        /// Also synchronizes cached variables to prevent redundant writes on resume.
        /// </summary>
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

        /// <summary>
        /// Computes and caches the integer hashes for all animator parameters used by this component.
        /// Must be called before updating parameters.
        /// </summary>
        /// <param name="animator">Animator whose parameters should be hashed.</param>
        public void LoadCachedAnimatorHashes(Animator animator)
        {
            Animator = animator;
            BasisAvatarAnimatorHash.HashCurrentHorizontalMovement = Animator.StringToHash("VelocityX");
            BasisAvatarAnimatorHash.HashCurrentVerticalMovement = Animator.StringToHash("VelocityZ");
            BasisAvatarAnimatorHash.HashCurrentSpeed = Animator.StringToHash("CurrentSpeed");
            BasisAvatarAnimatorHash.HashCrouchBlend = Animator.StringToHash("CrouchBlend");
            BasisAvatarAnimatorHash.HashCrouchedState = Animator.StringToHash("CrouchedState");
            BasisAvatarAnimatorHash.HashMovingState = Animator.StringToHash("MovingState");

            BasisAvatarAnimatorHash.IsPaused = Animator.StringToHash("IsPaused");

            BasisAvatarAnimatorHash.HashIsFalling = Animator.StringToHash("IsFalling");
            BasisAvatarAnimatorHash.HashIsLanding = Animator.StringToHash("IsLanding");
            BasisAvatarAnimatorHash.HashIsJumping = Animator.StringToHash("IsJumping");
        }

        /// <summary>
        /// Triggers the landing state on the animator (one-shot trigger).
        /// </summary>
        public void UpdateIsLandingState()
        {
            Animator.SetTrigger(BasisAvatarAnimatorHash.HashIsLanding);
        }
    }
}
