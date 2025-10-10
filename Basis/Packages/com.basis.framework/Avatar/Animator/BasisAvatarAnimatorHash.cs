namespace Basis.Scripts.Animator_Driver
{
    /// <summary>
    /// Stores precomputed animator parameter hashes for performance.
    /// Instead of repeatedly using <c>Animator.StringToHash</c> at runtime,
    /// these values are cached and referenced directly.
    /// </summary>
    [System.Serializable]
    public struct BasisAvatarAnimatorHash
    {
        /// <summary>
        /// Animator hash for the current horizontal movement parameter (X axis).
        /// </summary>
        public int HashCurrentHorizontalMovement;

        /// <summary>
        /// Animator hash for the current vertical movement parameter (Y axis).
        /// </summary>
        public int HashCurrentVerticalMovement;

        /// <summary>
        /// Animator hash for the overall speed parameter.
        /// </summary>
        public int HashCurrentSpeed;

        /// <summary>
        /// Animator hash indicating whether the avatar is crouched.
        /// </summary>
        public int HashCrouchedState;

        /// <summary>
        /// Animator hash for the crouch blend parameter (used for smooth transitions).
        /// </summary>
        public int HashCrouchBlend;

        /// <summary>
        /// Animator hash for the moving state parameter (walking, running, etc.).
        /// </summary>
        public int HashMovingState;

        /// <summary>
        /// Animator hash used to check or toggle whether the avatar is paused.
        /// </summary>
        public int IsPaused;

        /// <summary>
        /// Animator hash indicating whether the avatar is jumping.
        /// </summary>
        public int HashIsJumping;

        /// <summary>
        /// Animator hash indicating whether the avatar is falling.
        /// </summary>
        public int HashIsFalling;

        /// <summary>
        /// Animator hash indicating whether the avatar is landing.
        /// </summary>
        public int HashIsLanding;
    }
}
