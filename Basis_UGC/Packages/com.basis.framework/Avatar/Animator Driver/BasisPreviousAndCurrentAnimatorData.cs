using UnityEngine;

namespace Basis.Scripts.Animator_Driver
{
[System.Serializable]
public struct BasisPreviousAndCurrentAnimatorData
{
    public Vector3 LastPosition;
    public Vector2 Movement;
    public Vector2 SmoothedMovement;
    public float LastRotation;
    public float LastAngle;
}
}