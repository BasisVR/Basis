using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisSwivelHintCore
    {
        const float sqrEpsilon = 1e-10f;
        public static BasisSwivelFrame BuildFrame(Vector3 leftAnchor, Vector3 rightAnchor, Vector3 upFrom, Vector3 upTo)
        {
            BasisSwivelFrame f = default;
            Vector3 up = upTo - upFrom;
            float upSqr = up.sqrMagnitude;

            if (!(upSqr > sqrEpsilon))
            {
                return f;
            }
            up /= Mathf.Sqrt(upSqr);

            Vector3 right = rightAnchor - leftAnchor;
            right -= up * Vector3.Dot(right, up);
            float rightSqr = right.sqrMagnitude;
            if (!(rightSqr > sqrEpsilon))
            {
                return f;
            }
            right /= Mathf.Sqrt(rightSqr);

            f.Right = right;
            f.Up = up;
            f.Forward = Vector3.Cross(right, up);
            f.Valid = true;
            return f;
        }
    }
}
