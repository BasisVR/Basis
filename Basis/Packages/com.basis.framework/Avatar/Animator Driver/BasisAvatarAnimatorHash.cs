namespace Basis.Scripts.Animator_Driver
{
[System.Serializable]
public struct BasisAvatarAnimatorHash
{
    public int HashCurrentHorizontalMovement;
    public int HashCurrentVerticalMovement;
    public int HashCurrentSpeed;
    public int HashCrouchedState;
    public int HashCrouchBlend;
    public int HashMovingState;

        public int IsPaused;

    public int HashIsJumping;
    public int HashIsFalling;
    public int HashIsLanding;
}
}
