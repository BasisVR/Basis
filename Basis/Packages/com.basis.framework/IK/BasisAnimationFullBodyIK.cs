using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationFullBodyIK
{
    public static void SolveHipsAndSpine(
        AnimationStream stream,

        // --- Hips minimal driver ---
        Vector3Property targetPositionHips,
        Vector4Property targetRotationHips,
        Vector4Property offsetRotationHips,

        // --- Head + Legs (classic TwoBone) ---
        BoolProperty EnableSpineIK,

        ReadWriteTransformHandle HandleHips,
     //   ReadWriteTransformHandle HandleSpine,
        ReadWriteTransformHandle HandleChest,
//        ReadWriteTransformHandle HandleUpperChest,
        ReadWriteTransformHandle HandleNeck,
        ReadWriteTransformHandle HandleHead,
        Vector3Property targetPositionHead,
        Vector4Property targetRotationHead,
        Vector3Property hintPositionHead,
        Vector4Property hintRotationHead,
        BoolProperty hintWeightChestArea,
        AffineTransform targetOffsetHead,
        Vector3Property bendNormalHead
    )
    {
        // Early out: pass-through if spine IK disabled
        if (!EnableSpineIK.Get(stream))
        {
            Pass(stream, HandleChest, HandleNeck, HandleHead);
            BasisAnimationRuntimeUtils.PassThrough(stream, HandleHips);
            return;
        }

        // Apply hips driver if valid
        ApplyHipsDriver(stream, HandleHips, targetPositionHips, targetRotationHips, offsetRotationHips);

        // Validate required upper chain handles (Burst-safe: no params/arrays)
        if (!AreValid3(stream, HandleChest, HandleNeck, HandleHead))
        {
            Pass(stream, HandleChest, HandleNeck, HandleHead);
            return;
        }

        // Build target + hint transforms
        var tRot = V4ToQuat(targetRotationHead.Get(stream));
        var hRot = V4ToQuat(hintRotationHead.Get(stream));

        var target = new AffineTransform(targetPositionHead.Get(stream), tRot);
        var hint = new AffineTransform(hintPositionHead.Get(stream), hRot);
        var bendNormal = bendNormalHead.Get(stream);

        SolveTwoBoneSpine(
            stream,
            HandleChest, HandleNeck, HandleHead,
            target,
            hint,
            hintWeightChestArea.Get(stream),
            targetOffsetHead,
            bendNormal
        );
    }

    public static float TriangleAngle(float aLen, float aLen1, float aLen2)
    {
        // Law of cosines with clamped domain to avoid NaNs
        float denom = 2.0f * aLen1 * aLen2;
        if (denom <= Mathf.Epsilon)
            return 0f;

        float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / denom, -1.0f, 1.0f);
        return Mathf.Acos(c);
    }

    private const float k_SqrEpsilon = 1e-8f;

    /// <summary>
    /// Two-bone IK specialized for a spine-like chain (root-mid-tip).
    /// </summary>
    public static void SolveTwoBoneSpine(
        AnimationStream stream,
        ReadWriteTransformHandle root,
        ReadWriteTransformHandle mid,
        ReadWriteTransformHandle tip,
        AffineTransform target,
        AffineTransform hint,
        bool hasHint,
        AffineTransform targetOffset,
        Vector3 bendNormal)
    {
        // Read current joint positions
        Vector3 aPos = root.GetPosition(stream);
        Vector3 bPos = mid.GetPosition(stream);
        Vector3 cPos = tip.GetPosition(stream);

        // Target with offset applied in target space
        Vector3 tPos = target.translation + targetOffset.translation;
        Quaternion tRot = target.rotation * targetOffset.rotation;

        // Current bone vectors
        Vector3 ab = bPos - aPos;
        Vector3 bc = cPos - bPos;
        Vector3 ac = cPos - aPos;
        Vector3 at = tPos - aPos;

        float abLen = ab.magnitude;
        float bcLen = bc.magnitude;
        float acLen = ac.magnitude;
        float atLen = at.magnitude;

        float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);

        // Compute rotation axis for mid joint bend
        Vector3 axis = ComputeIkAxis(aPos, bc, at, hint.translation, bendNormal, hasHint);

        // Rotate mid joint by half the angle delta (distributes motion)
        float halfAngle = 0.5f * (oldAbcAngle - newAbcAngle);
        float s = Mathf.Sin(halfAngle);
        float c = Mathf.Cos(halfAngle);
        Quaternion deltaMid = new Quaternion(axis.x * s, axis.y * s, axis.z * s, c);
        mid.SetRotation(stream, deltaMid * mid.GetRotation(stream));

        // Re-evaluate and swing root so AC aligns with AT
        cPos = tip.GetPosition(stream);
        ac = cPos - aPos;
        root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

        // Optional hint orientation to control elbow-like twist around AC
        if (hasHint)
        {
            float acSqrMag = ac.sqrMagnitude;
            if (acSqrMag > 0f)
            {
                bPos = mid.GetPosition(stream);
                cPos = tip.GetPosition(stream);
                ab = bPos - aPos;
                ac = cPos - aPos;

                Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                Vector3 ah = hint.translation - aPos;
                Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                float maxReach = abLen + bcLen;
                if (abProj.sqrMagnitude > (maxReach * maxReach * 0.001f) && ahProj.sqrMagnitude > 0f)
                {
                    Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                    hintR = QuaternionExt.NormalizeSafe(hintR);
                    root.SetRotation(stream, hintR * root.GetRotation(stream));
                }
            }
        }

        // Set tip rotation to match target orientation (+offset)
        tip.SetRotation(stream, tRot);
    }

    public static void Pass(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip)
    {
        if (root.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, root);
        if (mid.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, mid);
        if (tip.IsValid(stream)) BasisAnimationRuntimeUtils.PassThrough(stream, tip);
    }

    public static Quaternion V4ToQuat(Vector4 v) => new Quaternion(v.x, v.y, v.z, v.w);

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // Burst-safe fixed-arity validity check (no params/no arrays)
    private static bool AreValid3(
        AnimationStream stream,
        ReadWriteTransformHandle a,
        ReadWriteTransformHandle b,
        ReadWriteTransformHandle c)
    {
        // Single-bitwise & avoids short-circuiting; either is fine for Burst
        return a.IsValid(stream) & b.IsValid(stream) & c.IsValid(stream);
    }

    private static void ApplyHipsDriver(
        AnimationStream stream,
        ReadWriteTransformHandle hips,
        Vector3Property targetPos,
        Vector4Property targetRot,
        Vector4Property offsetRot)
    {
        if (!hips.IsValid(stream))
            return;

        Vector3 hipPos = targetPos.Get(stream);
        Quaternion hipRot = V4ToQuat(targetRot.Get(stream));
        Quaternion hipOff = V4ToQuat(offsetRot.Get(stream));

        hips.SetPosition(stream, hipPos);
        hips.SetRotation(stream, hipRot * hipOff); // apply offset in target space
    }

    private static Vector3 ComputeIkAxis(
        Vector3 rootPos,
        Vector3 bc,
        Vector3 at,
        Vector3 hintPos,
        Vector3 bendNormal,
        bool hasHint)
    {
        Vector3 axis;
        if (hasHint)
        {
            axis = Vector3.Cross(hintPos - rootPos, bc);
            if (axis.sqrMagnitude < k_SqrEpsilon)
                axis = Vector3.Cross(at, bc);
            if (axis.sqrMagnitude < k_SqrEpsilon)
                axis = bendNormal;
        }
        else
        {
            axis = bendNormal;
        }

        float mag2 = axis.sqrMagnitude;
        if (mag2 < k_SqrEpsilon)
        {
            // Deterministic fallback to avoid NaNs/garbage under Burst
            return Vector3.forward;
        }

        return axis / Mathf.Sqrt(mag2);
    }
}
