using UnityEngine;
namespace Basis.IK
{
    // Rotational analogue of BasisChestSpringCore: an implicit-Euler, critically-damped angular spring.
    // Smooths a target rotation so high-frequency input (hip jitter / micro-sway) is rejected while a
    // sustained turn is followed with a small bounded lag. The caller owns the (rot, angVel) state and its
    // seeding / NaN re-seed; this is only the unconditionally-stable integrator step, factored out so the
    // live rig and any stability sweep run the identical update.
    //
    // It drives the hips rotation that feeds the no-elbow-tracker bend frame (BasisFullIKConstraintJob's
    // ArmBendFrame), so a wobble at the hips no longer wobbles the DERIVED elbow pole -- "more spring around
    // the hip so its movements don't affect the connecting bones as much" for users without elbow trackers.
    //
    // angVel is a WORLD-space angular velocity (a rotation vector, axis*radians/sec). The proportional term
    // is the shortest-arc rotation-vector error from the current rotation to the target, recomputed each step
    // (not a linearised accumulation), so it is robust for large angles. Implicit Euler: stable for any
    // omega*dt, exactly like the positional spring.
    public static class BasisHipFrameSpringCore
    {
        public static void Step(Quaternion rot, Vector3 angVel, Quaternion target, float dt, float hz, float damping,
            out Quaternion newRot, out Vector3 newAngVel)
        {
            // Shortest-arc error current->target as a world-space rotation vector (axis * angleRad).
            Quaternion errQ = target * Quaternion.Inverse(rot);
            if (errQ.w < 0f) errQ = new Quaternion(-errQ.x, -errQ.y, -errQ.z, -errQ.w); // pick the <=180 deg arc
            Vector3 ev = new Vector3(errQ.x, errQ.y, errQ.z);
            float evLen = Mathf.Sqrt(ev.x * ev.x + ev.y * ev.y + ev.z * ev.z);
            Vector3 e;
            if (evLen > 1e-6f)
            {
                float angle = 2f * Mathf.Atan2(evLen, errQ.w); // [0, pi], robust near 180 deg
                e = ev * (angle / evLen);
            }
            else
            {
                e = 2f * ev; // small-angle limit: angle ~= 2*evLen, axis = ev/evLen
            }

            float omega = 2f * Mathf.PI * hz;
            float omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * Mathf.Max(0f, damping);

            // Same implicit-Euler closed form as BasisChestSpringCore, applied to the angular velocity.
            float denom = 1f + dt * twoOmegaDamping + dt * dt * omegaSq;
            newAngVel = (angVel + dt * omegaSq * e) / denom;

            // Integrate: world-space body step exp(dt * newAngVel) applied on the left.
            Vector3 dTheta = dt * newAngVel;
            float ang = Mathf.Sqrt(dTheta.x * dTheta.x + dTheta.y * dTheta.y + dTheta.z * dTheta.z);
            Quaternion step;
            if (ang > 1e-8f)
            {
                float half = 0.5f * ang;
                float s = Mathf.Sin(half) / ang; // sin(half) * (axis = dTheta/ang)
                step = new Quaternion(dTheta.x * s, dTheta.y * s, dTheta.z * s, Mathf.Cos(half));
            }
            else
            {
                step = Quaternion.identity;
            }

            Quaternion outRot = step * rot;
            float n = Mathf.Sqrt(outRot.x * outRot.x + outRot.y * outRot.y + outRot.z * outRot.z + outRot.w * outRot.w);
            newRot = n > 1e-8f
                ? new Quaternion(outRot.x / n, outRot.y / n, outRot.z / n, outRot.w / n)
                : Quaternion.identity;
        }
    }
}
