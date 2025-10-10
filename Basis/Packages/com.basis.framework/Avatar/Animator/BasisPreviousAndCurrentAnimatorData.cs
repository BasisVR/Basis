using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
    /// <summary>
    /// Holds cached animator-related data from the previous and current frames.
    /// Used to calculate movement deltas, smooth transitions, and rotation changes.
    /// </summary>
    [System.Serializable]
    public struct BasisPreviousAndCurrentAnimatorData
    {
        /// <summary>
        /// The last recorded world position of the animated object.
        /// </summary>
        public Vector3 LastPosition;

        /// <summary>
        /// The current movement vector (typically input or velocity).
        /// </summary>
        public Vector2 Movement;

        /// <summary>
        /// Smoothed version of <see cref="Movement"/>, useful for blending in animations.
        /// </summary>
        public Vector2 SmoothedMovement;

        /// <summary>
        /// The last recorded Y-axis rotation of the animated object.
        /// </summary>
        public float LastRotation;

        /// <summary>
        /// The last recorded movement angle (in degrees).
        /// </summary>
        public float LastAngle;
    }
}
