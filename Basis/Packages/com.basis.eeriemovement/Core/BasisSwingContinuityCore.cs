using UnityEngine;
namespace Basis.IK
{
    public struct BasisSwingContinuityState
    {
        public Vector3 LastDir;
        public Vector3 LastAxis;
        public Vector3 LastTarget;
        public int SmoothState;   // last collision tag, or -1 while armed (easing a pop in)
        public bool Seeded;
    }

    public struct BasisSwingContinuityResult
    {
        public bool Valid;        // false on a degenerate pose -> caller no-ops and leaves state untouched
        public bool ApplySwing;   // caller swings the elbow toward (A + NewDir), then restores the hand
        public Vector3 NewDir;    // rate-limited swing direction (perpendicular to the root->tip axis)
        public BasisSwingContinuityState State; // persist when Valid
    }

    // Stream-free port of BasisFullIKConstraintJob.ApplySwingContinuity. Rate-limits a 3-bone chain's
    // mid-joint swing around the root->tip axis, engaging ONLY when the torso-collision tag changes and
    // easing the pop in at <= rateDegPerSec; free-air motion, pole flips and target teleports are accepted
    // instantly. The caller reads the bone positions, applies the swing via SwingElbowAroundAC and persists
    // the returned state. Change the continuity logic HERE so the job and the offline sweep stay in lock-step.
    public static class BasisSwingContinuityCore
    {
        const float k_SqrEpsilon = 1e-8f;
        const float k_Epsilon = 1e-5f;

        public static void Step(BasisSwingContinuityState s, Vector3 a, Vector3 b, Vector3 c,
            Vector3 targetPos, int collided, float rateDegPerSec, float dt, out BasisSwingContinuityResult r)
        {
            r = default;
            r.State = s;

            Vector3 ac = c - a;
            float acSqr = ac.sqrMagnitude;
            if (acSqr < k_SqrEpsilon)
            {
                return;
            }
            Vector3 axis = ac / Mathf.Sqrt(acSqr);

            Vector3 perp = b - a;
            perp -= axis * Vector3.Dot(perp, axis);
            float perpSqr = perp.sqrMagnitude;
            if (perpSqr < k_SqrEpsilon)
            {
                return; // chain near-straight: swing direction undefined this frame
            }
            Vector3 currentDir = perp / Mathf.Sqrt(perpSqr);
            r.Valid = true;

            bool armed = s.SmoothState < 0;
            bool collisionChanged = !armed && collided != s.SmoothState;

            bool seeded = s.Seeded;
            float chainLen = (b - a).magnitude + (c - b).magnitude;
            float teleThresh = 0.6f * chainLen;
            bool teleport = seeded && (targetPos - s.LastTarget).sqrMagnitude > teleThresh * teleThresh;
            if (rateDegPerSec <= 0f || !seeded || teleport || (!armed && !collisionChanged))
            {
                r.State = new BasisSwingContinuityState
                {
                    LastDir = currentDir,
                    LastAxis = axis,
                    LastTarget = targetPos,
                    SmoothState = collided,
                    Seeded = true,
                };
                return;
            }

            int smoothState = -1;

            // Carry the stored swing with the axis change so only the *extra* swing is limited.
            Vector3 carried = BasisQuaternionExt.FromToRotation(s.LastAxis, axis) * s.LastDir;
            carried -= axis * Vector3.Dot(carried, axis);
            float carriedSqr = carried.sqrMagnitude;
            bool easing = false;
            if (carriedSqr >= k_SqrEpsilon)
            {
                carried /= Mathf.Sqrt(carriedSqr);
                float angleDeg = Vector3.Angle(carried, currentDir);
                float maxStep = rateDegPerSec * dt;
                if (angleDeg > maxStep && angleDeg > k_Epsilon)
                {
                    Vector3 newDir = Vector3.Slerp(carried, currentDir, maxStep / angleDeg);
                    r.ApplySwing = true;
                    r.NewDir = newDir;
                    currentDir = newDir;
                    easing = true;
                }
            }

            if (!easing)
            {
                smoothState = collided;
            }

            r.State = new BasisSwingContinuityState
            {
                LastDir = currentDir,
                LastAxis = axis,
                LastTarget = targetPos,
                SmoothState = smoothState,
                Seeded = true,
            };
        }
    }
}
