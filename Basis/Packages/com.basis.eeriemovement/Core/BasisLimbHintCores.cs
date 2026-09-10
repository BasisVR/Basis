using Unity.Burst;
using UnityEngine;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisLegHintCore
    {
        public static bool Solve(in BasisSwivelFrame frame, Vector3 hip, Vector3 knee, Vector3 foot, Vector3 target, bool isLeft, out Vector3 hint, out float confidence, out float distrust)
        {
            float legLen = (knee - hip).magnitude + (foot - knee).magnitude;
            distrust = 0f;
            if (!BasisSwivelHintCore.LegHint(frame, hip, target, legLen, isLeft, out hint, out confidence))
            {
                return false;
            }
            distrust = 1f - BasisSwivelHintCore.LegModelTrust(confidence);
            return true;
        }
    }
}
