using UnityEngine;
namespace Basis.IK
{
    // Stream-free implicit-Euler step of BasisFullIKConstraintJob.ApplyChestSpring's critically-damped
    // spring. The caller owns the (pos, vel) state and its seeding/NaN re-seed; this is only the
    // unconditionally-stable integrator update, factored out so the live rig and the stability sweep run
    // the identical step. Implicit (not explicit) Euler: stable for any omega*dt, which the sweep proves.
    public static class BasisChestSpringCore
    {
        public static void Step(Vector3 pos, Vector3 vel, Vector3 target, float dt, float hz, float damping,
            out Vector3 newPos, out Vector3 newVel)
        {
            float omega = 2f * Mathf.PI * hz;
            float omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * Mathf.Max(0f, damping);

            // Implicit Euler: solve (vel_new, pos_new = pos + dt*vel_new) such that
            //   vel_new = vel + dt * (omega^2 * (target - pos_new) - 2*omega*damping * vel_new)
            // Substituting pos_new gives the closed-form denom below. Always stable.
            float denom = 1f + dt * twoOmegaDamping + dt * dt * omegaSq;
            newVel = (vel + dt * omegaSq * (target - pos)) / denom;
            newPos = pos + dt * newVel;
        }
    }
}
