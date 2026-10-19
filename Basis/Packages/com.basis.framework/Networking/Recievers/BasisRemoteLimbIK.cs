using Basis.IK;
using UnityEngine;
public struct BasisRemoteLimbPose
{
    public Vector3 Root, Joint, Tip;
    public Quaternion RootWorld, JointWorld, TipWorld, RootLocal, JointLocal, TipLocal, RootRest, JointRest, TipRest;
}
public static class BasisRemoteLimbIK
{
    public const float ElbowFromJointStart = 0.1f, ElbowFromJointFull = 0.3f, ArmReachHoldStart = 0.975f, ArmReachHoldFull = 0.995f, ArmReachHoldSlack = 0.01f;
    const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
    public static bool SolveArm(in BasisRemoteLimbPose limb, bool isLeft, Vector3 target, Quaternion targetRotation, in BasisSwivelFrame frame, out Quaternion upperLocal, out Quaternion lowerLocal, out Quaternion handLocal)
    {
        upperLocal = limb.RootLocal;
        lowerLocal = limb.JointLocal;
        handLocal = limb.TipLocal;
        if (!frame.Valid)
        {
            return false;
        }
        Rest(limb, out Quaternion parent, out Quaternion upperToLower, out Quaternion lowerToHand, out Quaternion restUpper, out Quaternion restLower, out Quaternion restHand, out Vector3 restElbow, out Vector3 restHandPos);
        target = HoldReach(limb, target, Vector3.Distance(limb.Root, restElbow) + Vector3.Distance(restElbow, restHandPos));
        BasisArmSolveInput input = default;
        input.Shoulder = limb.Root;
        input.RestElbow = restElbow;
        input.RestHand = restHandPos;
        input.RestHandRotation = restHand;
        input.TargetPosition = target;
        input.TargetRotation = targetRotation;
        input.TorsoUp = frame.Up;
        input.TorsoForward = frame.Forward;
        input.TorsoOut = isLeft ? -frame.Right : frame.Right;
        input.IsLeft = isLeft;
        input.HasTrackerHint = true;
        input.HintPosition = ElbowHint(limb, input, restUpper, restElbow);
        BasisArmState state = default;
        BasisArmSolveCore.Solve(input, ref state, out BasisArmSolveResult result);
        if (!result.Valid)
        {
            return false;
        }
        BasisArmSolveCore.Pose(input, result, restUpper, restLower, out Quaternion upper, out Quaternion lower, out float forearmRollDeg);
        Vector3 forearm = result.Hand - result.Elbow;
        if (forearm.sqrMagnitude > sqrEpsilon && (forearmRollDeg > 0.001f || forearmRollDeg < -0.001f))
        {
            lower = Quaternion.AngleAxis(forearmRollDeg, forearm.normalized) * lower;
        }
        Locals(parent, upperToLower, lowerToHand, upper, lower, targetRotation, out upperLocal, out lowerLocal, out handLocal);
        return true;
    }
    public static bool SolveLeg(in BasisRemoteLimbPose limb, Vector3 target, Quaternion targetRotation, Vector3 bendNormal, out Quaternion upperLocal, out Quaternion lowerLocal, out Quaternion footLocal)
    {
        upperLocal = limb.RootLocal;
        lowerLocal = limb.JointLocal;
        footLocal = limb.TipLocal;
        if (bendNormal.sqrMagnitude < sqrEpsilon)
        {
            return false;
        }
        Rest(limb, out Quaternion parent, out Quaternion upperToLower, out Quaternion lowerToFoot, out Quaternion restUpper, out Quaternion restLower, out _, out Vector3 restKnee, out Vector3 restFoot);
        BasisLegSolveInput input = default;
        input.Root = limb.Root;
        input.Mid = restKnee;
        input.Tip = restFoot;
        input.RootRotation = restUpper;
        input.MidRotation = restLower;
        input.TargetPosition = target;
        input.TargetRotation = targetRotation;
        input.TargetOffset = Quaternion.identity;
        input.HintPosition = KneeHint(limb, target);
        input.HintWeight = 1f;
        input.BendNormal = bendNormal;
        input.AnteriorNormal = bendNormal;
        BasisLegSolveCore.Solve(input, out BasisLegSolveResult result);
        if (!(result.UpperLength > epsilon) || !(result.LowerLength > epsilon))
        {
            return false;
        }
        Locals(parent, upperToLower, lowerToFoot, result.RootRotationSolved, result.MidRotationSolved, targetRotation, out upperLocal, out lowerLocal, out footLocal);
        return true;
    }
    public static float LegReachRatio(in BasisRemoteLimbPose limb, Vector3 target)
    {
        float chain = Vector3.Distance(limb.Root, limb.Joint) + Vector3.Distance(limb.Joint, limb.Tip);
        return Vector3.Distance(limb.Root, target) / Mathf.Max(chain, 1e-4f);
    }
    static Vector3 HoldReach(in BasisRemoteLimbPose limb, Vector3 target, float chain)
    {
        Vector3 aim = target - limb.Root;
        float reach = aim.magnitude;
        if (reach < epsilon || chain < epsilon)
        {
            return target;
        }
        float fkReach = Vector3.Distance(limb.Root, limb.Tip), slack = ArmReachHoldSlack * chain;
        float held = reach + Mathf.Clamp(fkReach - reach, -slack, slack) * Smoothstep(ArmReachHoldStart, ArmReachHoldFull, fkReach / chain);
        return limb.Root + aim * (held / reach);
    }
    static Vector3 ElbowHint(in BasisRemoteLimbPose limb, in BasisArmSolveInput input, Quaternion restUpper, Vector3 restElbow)
    {
        Vector3 axis = input.TargetPosition - limb.Root;
        float axisLength = axis.magnitude;
        if (axisLength < epsilon)
        {
            return limb.Joint;
        }
        axis /= axisLength;
        Vector3 upper = limb.Joint - limb.Root, reach = limb.Tip - limb.Root, u0 = restElbow - limb.Root;
        float upperLength = upper.magnitude, reachSqr = reach.sqrMagnitude;
        if (upperLength < epsilon || u0.sqrMagnitude < sqrEpsilon)
        {
            return limb.Joint;
        }
        u0.Normalize();
        Vector3 h0 = BasisQuaternionExt.FromToRotation(input.TorsoOut, u0) * input.TorsoUp;
        h0 -= u0 * Vector3.Dot(h0, u0);
        if (h0.sqrMagnitude < sqrEpsilon) h0 = input.TorsoUp;
        Vector3 hinge = limb.RootWorld * (Quaternion.Inverse(restUpper) * h0.normalized);
        Vector3 fromHinge = Vector3.Cross(axis, hinge) * (input.IsLeft ? 1f : -1f), fromJoint = upper - axis * Vector3.Dot(upper, axis);
        Vector3 bent = reachSqr > sqrEpsilon ? upper - reach * (Vector3.Dot(upper, reach) / reachSqr) : Vector3.zero;
        float w = Smoothstep(ElbowFromJointStart, ElbowFromJointFull, bent.magnitude / upperLength);
        Vector3 dir = Unit(fromHinge) * (1f - w) + Unit(fromJoint) * w;
        if (dir.sqrMagnitude < sqrEpsilon) dir = w >= 0.5f ? fromJoint : fromHinge;
        dir -= axis * Vector3.Dot(dir, axis);
        if (dir.sqrMagnitude < sqrEpsilon)
        {
            return limb.Joint;
        }
        return input.TargetPosition + dir.normalized * upperLength;
    }
    static Vector3 KneeHint(in BasisRemoteLimbPose limb, Vector3 target)
    {
        Vector3 axis = target - limb.Root;
        float axisSqr = axis.sqrMagnitude;
        if (axisSqr < sqrEpsilon)
        {
            return limb.Joint;
        }
        Vector3 thigh = limb.Joint - limb.Root, side = thigh - axis * (Vector3.Dot(thigh, axis) / axisSqr);
        if (side.sqrMagnitude < sqrEpsilon)
        {
            return limb.Joint;
        }
        return limb.Root + side.normalized * Mathf.Max(thigh.magnitude, 1e-3f);
    }
    static void Rest(in BasisRemoteLimbPose limb, out Quaternion parent, out Quaternion upperToLower, out Quaternion lowerToTip, out Quaternion restUpper, out Quaternion restLower, out Quaternion restTip, out Vector3 restJoint, out Vector3 restTipPos)
    {
        Quaternion rootInv = Quaternion.Inverse(limb.RootWorld), jointInv = Quaternion.Inverse(limb.JointWorld);
        parent = limb.RootWorld * Quaternion.Inverse(limb.RootLocal);
        upperToLower = rootInv * (limb.JointWorld * Quaternion.Inverse(limb.JointLocal));
        lowerToTip = jointInv * (limb.TipWorld * Quaternion.Inverse(limb.TipLocal));
        restUpper = parent * limb.RootRest;
        restLower = restUpper * upperToLower * limb.JointRest;
        restTip = restLower * lowerToTip * limb.TipRest;
        restJoint = limb.Root + restUpper * (rootInv * (limb.Joint - limb.Root));
        restTipPos = restJoint + restLower * (jointInv * (limb.Tip - limb.Joint));
    }
    static void Locals(Quaternion parent, Quaternion upperToLower, Quaternion lowerToTip, Quaternion upper, Quaternion lower, Quaternion tip, out Quaternion upperLocal, out Quaternion lowerLocal, out Quaternion tipLocal)
    {
        upperLocal = BasisQuaternionExt.NormalizeSafe(Quaternion.Inverse(parent) * upper);
        lowerLocal = BasisQuaternionExt.NormalizeSafe(Quaternion.Inverse(upper * upperToLower) * lower);
        tipLocal = BasisQuaternionExt.NormalizeSafe(Quaternion.Inverse(lower * lowerToTip) * tip);
    }
    static Vector3 Unit(Vector3 v)
    {
        float sqr = v.sqrMagnitude;
        return sqr > sqrEpsilon ? v / Mathf.Sqrt(sqr) : Vector3.zero;
    }
    static float Smoothstep(float a, float b, float v)
    {
        float t = Mathf.Clamp01((v - a) / (b - a));
        return t * t * (3f - 2f * t);
    }
}
