using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
    /// <summary>
    /// Holds cached and current animator state variables.
    /// Used to smooth updates, reduce redundant Animator calls,
    /// and track locomotion parameters across frames.
    /// </summary>
    [System.Serializable]
    public struct BasisAnimatorVariables
    {
        /// <summary>
        /// Cached playback speed of the animator.
        /// </summary>
        public float cachedAnimSpeed;

        /// <summary>
        /// Cached blend value for crouching (0 = standing, 1 = crouched).
        /// </summary>
        public float cachedCrouchBlend;

        /// <summary>
        /// Cached horizontal movement parameter (X axis).
        /// </summary>
        public float cachedHorizontalMovement;

        /// <summary>
        /// Cached vertical movement parameter (Y axis).
        /// </summary>
        public float cachedVerticalMovement;

        /// <summary>
        /// Cached flag for whether the avatar was moving last frame.
        /// </summary>
        public bool cachedIsMoving;

        /// <summary>
        /// Cached flag for whether the avatar was jumping last frame.
        /// </summary>
        public bool cachedIsJumping;

        /// <summary>
        /// Cached flag for whether the avatar was falling last frame.
        /// </summary>
        public bool cachedIsFalling;

        /// <summary>
        /// Cached flag for whether the avatar was crouching last frame.
        /// </summary>
        public bool cachedIsCrouching;

        /// <summary>
        /// Current flag indicating if the avatar is jumping this frame.
        /// </summary>
        public bool IsJumping;

        /// <summary>
        /// Current flag indicating if the avatar is falling this frame.
        /// </summary>
        public bool IsFalling;

        /// <summary>
        /// Current flag indicating if the avatar is crouching this frame.
        /// </summary>
        public bool IsCrouching;

        /// <summary>
        /// Current crouch blend value (0 = standing, 1 = crouched).
        /// </summary>
        public float CrouchBlend;

        /// <summary>
        /// Current animation playback speed.
        /// </summary>
        public float AnimationsCurrentSpeed;

        /// <summary>
        /// Current flag indicating if the avatar is moving.
        /// </summary>
        public bool isMoving;

        /// <summary>
        /// Angular velocity of the avatar (rotation change per second).
        /// </summary>
        public Vector3 AngularVelocity;

        /// <summary>
        /// Linear velocity of the avatar (movement change per second).
        /// </summary>
        public Vector3 Velocity;
    }
}
