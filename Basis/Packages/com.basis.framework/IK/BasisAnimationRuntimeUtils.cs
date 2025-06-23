using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationRuntimeUtils
{
    const float k_SqrEpsilon = 1e-8f;
    /// <summary>
    /// Evaluates the Two-Bone IK algorithm.
    /// </summary>
    /// <param name="stream">The animation stream to work on.</param>
    /// <param name="root">The transform handle for the root transform.</param>
    /// <param name="mid">The transform handle for the mid transform.</param>
    /// <param name="tip">The transform handle for the tip transform.</param>
    /// <param name="target">The transform handle for the target transform.</param>
    /// <param name="hint">The transform handle for the hint transform.</param>
    /// <param name="HasHint">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="targetOffset">The offset applied to the target transform.</param>
    public static void SolveTwoBoneIKLegsAndTorso(AnimationStream stream, ReadWriteTransformHandle root,   ReadWriteTransformHandle mid,ReadWriteTransformHandle tip,AffineTransform target,AffineTransform hint,bool HasHint,AffineTransform targetOffset, Vector3 BendNormal )
    {
        Vector3 aPosition = root.GetPosition(stream);
        Vector3 bPosition = mid.GetPosition(stream);
        Vector3 cPosition = tip.GetPosition(stream);

        Vector3 targetPos = target.translation;
        Quaternion targetRot = target.rotation;

        Vector3 tPosition = targetPos + targetOffset.translation;
        Quaternion tRotation = targetRot * targetOffset.rotation;

        Vector3 ab = bPosition - aPosition;
        Vector3 bc = cPosition - bPosition;
        Vector3 ac = cPosition - aPosition;
        Vector3 at = tPosition - aPosition;

        float abLen = ab.magnitude;
        float bcLen = bc.magnitude;
        float acLen = ac.magnitude;
        float atLen = at.magnitude;

        float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);
        Vector3 axis;
        if (HasHint)
        {
            axis = Vector3.Cross(hint.translation - aPosition, bc);

            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                axis = Vector3.Cross(at, bc);
            }

            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                axis = BendNormal;
            }
        }
        else
        {
            axis = BendNormal;
        }

        axis = Vector3.Normalize(axis);

        float halfAngle = 0.5f * (oldAbcAngle - newAbcAngle);
        float sin = Mathf.Sin(halfAngle);
        float cos = Mathf.Cos(halfAngle);
        Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
        mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

        cPosition = tip.GetPosition(stream);
        ac = cPosition - aPosition;
        root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

        if (HasHint)
        {
            float acSqrMag = ac.sqrMagnitude;
            if (acSqrMag > 0f)
            {
                bPosition = mid.GetPosition(stream);
                cPosition = tip.GetPosition(stream);
                ab = bPosition - aPosition;
                ac = cPosition - aPosition;

                Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                Vector3 ah = hint.translation - aPosition;
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

        tip.SetRotation(stream, tRotation);
    }
    /// <summary>
    /// Evaluates the Two-Bone IK algorithm.
    /// </summary>
    /// <param name="stream">The animation stream to work on.</param>
    /// <param name="root">The transform handle for the root transform.</param>
    /// <param name="mid">The transform handle for the mid transform.</param>
    /// <param name="tip">The transform handle for the tip transform.</param>
    /// <param name="target">The transform handle for the target transform.</param>
    /// <param name="hint">The transform handle for the hint transform.</param>
    /// <param name="hintWeight">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="targetOffset">The offset applied to the target transform.</param>
    public static void SolveTwoBoneIKArms(
        AnimationStream stream,
        ReadWriteTransformHandle root,
        ReadWriteTransformHandle mid,
        ReadWriteTransformHandle tip,
        AffineTransform target,
        AffineTransform hint,
        bool hintWeight
        )
    {
        Vector3 aPosition = root.GetPosition(stream);
        Vector3 bPosition = mid.GetPosition(stream);
        Vector3 cPosition = tip.GetPosition(stream);

        Vector3 targetPos = target.translation;
        Quaternion targetRot = target.rotation;

        Vector3 tPosition = targetPos;
        Quaternion tRotation = targetRot;
        Vector3 ab = bPosition - aPosition;
        Vector3 bc = cPosition - bPosition;
        Vector3 ac = cPosition - aPosition;
        Vector3 at = tPosition - aPosition;

        float abLen = ab.magnitude;
        float bcLen = bc.magnitude;
        float acLen = ac.magnitude;
        float atLen = at.magnitude;

        float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);

        // Bend normal strategy is to take whatever has been provided in the animation
        // stream to minimize configuration changes, however if this is collinear
        // try computing a bend normal given the desired target position.
        // If this also fails, try resolving axis using hint if provided.
        Vector3 axis = Vector3.Cross(ab, bc);
        if (axis.sqrMagnitude < k_SqrEpsilon)
        {
            axis = hintWeight ? Vector3.Cross(hint.translation - aPosition, bc) : Vector3.zero;

            if (axis.sqrMagnitude < k_SqrEpsilon)
                axis = Vector3.Cross(at, bc);

            if (axis.sqrMagnitude < k_SqrEpsilon)
                axis = Vector3.up;
        }
        axis = Vector3.Normalize(axis);

        float a = 0.5f * (oldAbcAngle - newAbcAngle);
        float sin = Mathf.Sin(a);
        float cos = Mathf.Cos(a);
        Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
        mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

        cPosition = tip.GetPosition(stream);
        ac = cPosition - aPosition;
        root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

        if (hintWeight)
        {
            float acSqrMag = ac.sqrMagnitude;
            if (acSqrMag > 0f)
            {
                bPosition = mid.GetPosition(stream);
                cPosition = tip.GetPosition(stream);
                ab = bPosition - aPosition;
                ac = cPosition - aPosition;

                Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                Vector3 ah = hint.translation - aPosition;
                Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                float maxReach = abLen + bcLen;
                if (abProj.sqrMagnitude > (maxReach * maxReach * 0.001f) && ahProj.sqrMagnitude > 0f)
                {
                    Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                   // hintR.x *= hintWeight;
                  //  hintR.y *= hintWeight;
                   // hintR.z *= hintWeight;
                    hintR = QuaternionExt.NormalizeSafe(hintR);
                    root.SetRotation(stream, hintR * root.GetRotation(stream));
                }
            }
        }

        tip.SetRotation(stream, tRotation);
    }

    /// <summary>
    /// Sets the position for a hint and target given bone positions.
    /// </summary>
    /// <param name="stream">The animation stream to work on.</param>
    /// <param name="root">The transform handle for the root transform.</param>
    /// <param name="mid">The transform handle for the mid transform.</param>
    /// <param name="tip">The transform handle for the tip transform.</param>
    /// <param name="target">The transform handle for the target transform.</param>
    /// <param name="hint">The transform handle for the hint transform.</param>
    /// <param name="posWeight">The weight for which target position has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="rotWeight">The weight for which target rotation has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="hintWeight">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="targetOffset">The offset applied to the target transform.</param>
    public static void InverseSolveTwoBoneIK(
        AnimationStream stream,
        ReadOnlyTransformHandle root,
        ReadOnlyTransformHandle mid,
        ReadOnlyTransformHandle tip,
        ReadWriteTransformHandle target,
        ReadWriteTransformHandle hint,
        float posWeight,
        float rotWeight,
        float hintWeight,
        AffineTransform targetOffset
    )
    {
        Vector3 rootPosition = root.GetPosition(stream);
        Vector3 midPosition = mid.GetPosition(stream);
        tip.GetGlobalTR(stream, out var tipPosition, out var tipRotation);
        target.GetGlobalTR(stream, out var targetPosition, out var targetRotation);
        bool isHintValid = hint.IsValid(stream);
        Vector3 hintPosition = Vector3.zero;
        if (isHintValid)
            hintPosition = hint.GetPosition(stream);

        InverseSolveTwoBoneIK(rootPosition, midPosition, tipPosition, tipRotation, ref targetPosition,
            ref targetRotation, ref hintPosition, isHintValid, posWeight, rotWeight, hintWeight, targetOffset);

        target.SetPosition(stream, targetPosition);
        target.SetRotation(stream, targetRotation);
        hint.SetPosition(stream, hintPosition);
    }

    /// <summary>
    /// Sets the position for a hint and target for given bone positions.
    /// </summary>
    /// <param name="rootPosition">The position of the root bone.</param>
    /// <param name="midPosition">The position of the mid bone.</param>
    /// <param name="tipPosition">The position of the tip bone.</param>
    /// <param name="tipRotation">The rotation of the tip bone.</param>
    /// <param name="targetPosition">The position of the target.</param>
    /// <param name="targetRotation">The rotation of the target.</param>
    /// <param name="hintPosition">The position of the hint.</param>
    /// <param name="isHintValid">Whether the hint position should be set.</param>
    /// <param name="posWeight">The weight for which target position has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="rotWeight">The weight for which target rotation has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="hintWeight">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
    /// <param name="targetOffset">The offset applied to the target transform.</param>
    public static void InverseSolveTwoBoneIK(
        Vector3 rootPosition,
        Vector3 midPosition,
        Vector3 tipPosition, Quaternion tipRotation,
        ref Vector3 targetPosition, ref Quaternion targetRotation,
        ref Vector3 hintPosition, bool isHintValid,
        float posWeight,
        float rotWeight,
        float hintWeight,
        AffineTransform targetOffset
    )
    {
        targetPosition = (posWeight > 0f) ? tipPosition + targetOffset.translation : targetPosition;
        targetRotation = (rotWeight > 0f) ? tipRotation * targetOffset.rotation : targetRotation;

        if (isHintValid)
        {
            var ac = tipPosition - rootPosition;
            var ab = midPosition - rootPosition;
            var bc = tipPosition - midPosition;

            float abLen = ab.magnitude;
            float bcLen = bc.magnitude;

            var acSqrMag = Vector3.Dot(ac, ac);
            var projectionPoint = rootPosition;
            if (acSqrMag > k_SqrEpsilon)
                projectionPoint += Vector3.Dot(ab / acSqrMag, ac) * ac;
            var poleVectorDirection = midPosition - projectionPoint;

            var scale = abLen + bcLen;
            hintPosition = (hintWeight > 0f) ? projectionPoint + (poleVectorDirection.normalized * scale) : hintPosition;
        }
    }

    static float TriangleAngle(float aLen, float aLen1, float aLen2)
    {
        float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (aLen1 * aLen2) / 2.0f, -1.0f, 1.0f);
        return Mathf.Acos(c);
    }

    /// <summary>
    /// Evaluates the FABRIK ChainIK algorithm.
    /// </summary>
    /// <param name="linkPositions">Uninitialized buffer of positions. linkPositions and linkLengths must have the same size.</param>
    /// <param name="linkLengths">Array of distances in between positions. linkPositions and linkLenghts must have the same size.</param>
    /// <param name="target">Target position.</param>
    /// <param name="tolerance">The maximum distance the resulting position and initial target are allowed to have in between them.</param>
    /// <param name="maxReach">The maximum distance the Transform chain can reach.</param>
    /// <param name="maxIterations">The maximum number of iterations allowed for the ChainIK algorithm to converge to a solution.</param>
    /// <returns>Returns true if ChainIK calculations were successful. False otherwise.</returns>
    /// <remarks>
    /// Implementation of unconstrained FABRIK solver : Forward and Backward Reaching Inverse Kinematic
    /// Aristidou A, Lasenby J. FABRIK: a fast, iterative solver for the inverse kinematics problem. Graphical Models 2011; 73(5): 243–260.
    /// </remarks>
    public static bool SolveFABRIK(
        ref NativeArray<Vector3> linkPositions,
        ref NativeArray<float> linkLengths,
        Vector3 target,
        float tolerance,
        float maxReach,
        int maxIterations
        )
    {
        // If the target is unreachable
        var rootToTargetDir = target - linkPositions[0];
        if (rootToTargetDir.sqrMagnitude > Square(maxReach))
        {
            // Line up chain towards target
            var dir = rootToTargetDir.normalized;
            for (int i = 1; i < linkPositions.Length; ++i)
                linkPositions[i] = linkPositions[i - 1] + dir * linkLengths[i - 1];

            return true;
        }
        else
        {
            int tipIndex = linkPositions.Length - 1;
            float sqrTolerance = Square(tolerance);
            if (SqrDistance(linkPositions[tipIndex], target) > sqrTolerance)
            {
                var rootPos = linkPositions[0];
                int iteration = 0;

                do
                {
                    // Forward reaching phase
                    // Set tip to target and propagate displacement to rest of chain
                    linkPositions[tipIndex] = target;
                    for (int i = tipIndex - 1; i > -1; --i)
                        linkPositions[i] = linkPositions[i + 1] + ((linkPositions[i] - linkPositions[i + 1]).normalized * linkLengths[i]);

                    // Backward reaching phase
                    // Set root back at it's original position and propagate displacement to rest of chain
                    linkPositions[0] = rootPos;
                    for (int i = 1; i < linkPositions.Length; ++i)
                        linkPositions[i] = linkPositions[i - 1] + ((linkPositions[i] - linkPositions[i - 1]).normalized * linkLengths[i - 1]);
                }
                while ((SqrDistance(linkPositions[tipIndex], target) > sqrTolerance) && (++iteration < maxIterations));

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the square length between two vectors.
    /// </summary>
    /// <param name="lhs">Vector3 value.</param>
    /// <param name="rhs">Vector3 value.</param>
    /// <returns>Square length between lhs and rhs.</returns>
    public static float SqrDistance(Vector3 lhs, Vector3 rhs)
    {
        return (rhs - lhs).sqrMagnitude;
    }

    /// <summary>
    /// Returns the square value of a float.
    /// </summary>
    /// <param name="value">Float value.</param>
    /// <returns>Squared value.</returns>
    public static float Square(float value)
    {
        return value * value;
    }

    /// <summary>
    /// Linearly interpolates between two vectors using a vector interpolant.
    /// </summary>
    /// <param name="a">Start Vector3 value.</param>
    /// <param name="b">End Vector3 value.</param>
    /// <param name="t">Interpolant Vector3 value.</param>
    /// <returns>Interpolated value.</returns>
    public static Vector3 Lerp(Vector3 a, Vector3 b, Vector3 t)
    {
        return Vector3.Scale(a, Vector3.one - t) + Vector3.Scale(b, t);
    }

    /// <summary>
    /// Returns b if c is greater than zero, a otherwise.
    /// </summary>
    /// <param name="a">First float value.</param>
    /// <param name="b">Second float value.</param>
    /// <param name="c">Comparator float value.</param>
    /// <returns>Selected float value.</returns>
    public static float Select(float a, float b, float c)
    {
        return (c > 0f) ? b : a;
    }

    /// <summary>
    /// Returns a componentwise selection between two vectors a and b based on a vector selection mask c.
    /// Per component, the component from b is selected when c is greater than zero, otherwise the component from a is selected.
    /// </summary>
    /// <param name="a">First Vector3 value.</param>
    /// <param name="b">Second Vector3 value.</param>
    /// <param name="c">Comparator Vector3 value.</param>
    /// <returns>Selected Vector3 value.</returns>
    public static Vector3 Select(Vector3 a, Vector3 b, Vector3 c)
    {
        return new Vector3(Select(a.x, b.x, c.x), Select(a.y, b.y, c.y), Select(a.z, b.z, c.z));
    }

    /// <summary>
    /// Projects a vector onto a plane defined by a normal orthogonal to the plane.
    /// </summary>
    /// <param name="vector">The location of the vector above the plane.</param>
    /// <param name="planeNormal">The direction from the vector towards the plane.</param>
    /// <returns>The location of the vector on the plane.</returns>
    public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
    {
        float sqrMag = Vector3.Dot(planeNormal, planeNormal);
        var dot = Vector3.Dot(vector, planeNormal);
        return new Vector3(vector.x - planeNormal.x * dot / sqrMag,
                vector.y - planeNormal.y * dot / sqrMag,
                vector.z - planeNormal.z * dot / sqrMag);
    }

    internal static float Sum(AnimationJobCache cache, UnityEngine.Animations.Rigging.CacheIndex index, int count)
    {
        if (count == 0)
            return 0f;

        float sum = 0f;
        for (int i = 0; i < count; ++i)
            sum += cache.GetRaw(index, i);

        return sum;
    }

    /// <summary>
    /// Calculates the sum of all float elements in the array.
    /// </summary>
    /// <param name="floatBuffer">An array of float elements.</param>
    /// <returns>Sum of all float elements.</returns>
    public static float Sum(NativeArray<float> floatBuffer)
    {
        if (floatBuffer.Length == 0)
            return 0f;

        float sum = 0f;
        for (int i = 0; i < floatBuffer.Length; ++i)
        {
            sum += floatBuffer[i];
        }

        return sum;
    }

    /// <summary>
    /// Copies translation, rotation and scale values from specified Transform handle to stream.
    /// </summary>
    /// <param name="stream">The animation stream to work on.</param>
    /// <param name="handle">The transform handle to copy.</param>
    public static void PassThrough(AnimationStream stream, ReadWriteTransformHandle handle)
    {
        handle.GetLocalTRS(stream, out Vector3 position, out Quaternion rotation, out Vector3 scale);
        handle.SetLocalTRS(stream, position, rotation, scale);
    }
}
