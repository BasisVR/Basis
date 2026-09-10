using Unity.Burst;
using UnityEngine;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisArmHintCore
    {
        public static bool Solve(in BasisSwivelFrame frame, Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 target, bool isLeft, Quaternion bodyRotFallback, bool hasState, ref BasisArmSlotState state, bool drag, float dragHz, float dt, out Vector3 hint)
        {
            float armLen = (elbow - shoulder).magnitude + (hand - elbow).magnitude;
            if (!BasisSwivelHintCore.ArmHint(frame, shoulder, target, armLen, isLeft, out hint, out float poleConditioning))
            {
                return false;
            }
            Vector3 curAxisV = target - shoulder, rawBendV = hint - shoulder;
            float axLen = curAxisV.magnitude, rbLen = rawBendV.magnitude;
            if (!(axLen > 1e-5f && rbLen > 1e-5f && hasState))
            {
                return true;
            }
            Vector3 curAxis = curAxisV / axLen, rawBend = rawBendV / rbLen;
            bool seeded = state.HintSeeded;
            float curReach = axLen / armLen, armDt = Mathf.Min(dt, BasisElbowSwingCapCore.MaxSlewBudgetDt);
            Quaternion bodyRot = frame.Valid ? Quaternion.LookRotation(frame.Forward, frame.Up) : bodyRotFallback, bodyDelta = seeded ? bodyRot * Quaternion.Inverse(state.HintBodyRot) : Quaternion.identity;
            Vector3 cappedBend = seeded ? (Vector3)BasisElbowSwingCapCore.Apply(bodyDelta * state.HintBend, bodyDelta * state.HintAxis, curAxis, rawBend, BasisElbowSwingCapCore.MaxGain, curReach - state.HintReach, poleConditioning, BasisElbowSwingCapCore.SlewCapRad(dt)) : rawBend;
            state.HintBend = cappedBend;
            state.HintAxis = curAxis;
            state.HintReach = curReach;
            Vector3 outBend = cappedBend;
            if (drag && seeded)
            {
                outBend = (Vector3)BasisElbowDragCore.Apply(state.HintDrag, bodyDelta, curAxis, cappedBend, BasisElbowDragCore.Alpha(dragHz, armDt));
            }
            state.HintDrag = outBend;
            state.HintBodyRot = bodyRot;
            state.HintSeeded = true;
            hint = shoulder + 0.5f * armLen * outBend;
            return true;
        }
    }
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
