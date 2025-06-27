using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
    [System.Serializable]
    public struct BasisAnimatorVariables
    {
        public float cachedAnimSpeed;
        public float cachedCrouchBlend;
        public float cachedHorizontalMovement;
        public float cachedVerticalMovement;

        public bool cachedIsMoving;
        public bool cachedIsJumping;
        public bool cachedIsFalling;
        public bool cachedIsCrouching;

        public bool IsJumping;
        public bool IsFalling;
        public bool IsCrouching;
        public float CrouchBlend;
        public float AnimationsCurrentSpeed;
        public bool isMoving;
        public Vector3 AngularVelocity;
        public Vector3 Velocity;
    }
}
