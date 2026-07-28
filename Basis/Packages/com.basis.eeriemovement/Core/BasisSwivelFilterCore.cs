using UnityEngine;
namespace Basis.IK
{
    public struct BasisSwivelFilterState
    {
        public float Raw;     // last raw swivel sample
        public float Vel;     // low-passed angular velocity (deg/s)
        public float Smooth;  // smoothed swivel output
    }

    // Stream-free One-Euro filter step for BasisFullIKConstraintJob.SmoothElbowSwivel. The job still
    // computes the raw swivel from the bone pose and applies the smoothed result via SwingElbowAroundAC;
    // this is the adaptive low-pass (velocity-dependent cutoff) factored out so the live rig and the
    // temporal sweep run the identical filter. Wraps angles with DeltaAngle so it is continuous at +/-180.
    public static class BasisSwivelFilterCore
    {
        public const float MinCutoffHz = 1.0f;   // held-still smoothing floor
        public const float DerivCutoffHz = 1.0f; // derivative low-pass (rejects jitter velocity)
        public const float Beta = 0.05f;         // how fast the cutoff opens with sustained swivel speed

        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-3f));
            return 1f / (1f + tau / dt);
        }

        public static BasisSwivelFilterState Seed(float curSwivel)
        {
            return new BasisSwivelFilterState { Raw = curSwivel, Vel = 0f, Smooth = curSwivel };
        }

        public static BasisSwivelFilterState Step(BasisSwivelFilterState s, float curSwivel, float dt)
        {
            return Step(s, curSwivel, dt, MinCutoffHz, Beta, DerivCutoffHz);
        }

        public static BasisSwivelFilterState Step(BasisSwivelFilterState s, float curSwivel, float dt, float minCutoffHz, float beta, float derivCutoffHz)
        {
            float vel = Mathf.DeltaAngle(s.Raw, curSwivel) / dt;
            float velHat = Mathf.Lerp(s.Vel, vel, Alpha(derivCutoffHz, dt));
            float cutoff = minCutoffHz + beta * Mathf.Abs(velHat);
            float smooth = s.Smooth + Mathf.DeltaAngle(s.Smooth, curSwivel) * Alpha(cutoff, dt);
            return new BasisSwivelFilterState { Raw = curSwivel, Vel = velHat, Smooth = smooth };
        }
    }
}
