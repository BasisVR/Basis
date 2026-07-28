using UnityEngine;
namespace Basis.IK
{
    public struct BasisTrunkCounterbalanceInput
    {
        /// <summary>The hips target as the lock-mode stage left it.</summary>
        public Vector3 HipsPos;
        /// <summary>
        /// Gaze-invariant neck estimate: headTargetPos + headWorldRot * tposeHeadToNeckLocal, the SAME cue
        /// DistributeSpineBend uses. NOT the HMD. The HMD sits forward of the neck pivot, so a pure look-down
        /// swings it forward; feed that here and merely glancing down shoves the pelvis backward -- the exact
        /// mirror image of the bug this exists to fix.
        /// </summary>
        public Vector3 NeckCue;
        public Vector3 PlayerUp;
        /// <summary>Posterior shift as a fraction of how far the neck has travelled forward of the hips. 0 = off.</summary>
        public float Gain;
        /// <summary>Hard ceiling on the shift in metres, eased into (never a step). 0 = off.</summary>
        public float MaxShift;
    }

    public struct BasisTrunkCounterbalanceResult
    {
        public Vector3 HipsPos;
        public bool Applied;
        /// <summary>
        /// sin(trunk flexion): 0 upright, 1 folded to horizontal. The crouch sit-back should be weighted by
        /// (1 - this) so a waist-fold and a squat do not both claim the same pelvis travel.
        /// Left at 0 when the term is disabled (Gain or MaxShift 0), so the crouch fade switches off with it
        /// and "gain 0" is a true no-op for the whole feature rather than only for the shift.
        /// </summary>
        public float FlexionFrac;
        public float ShiftMeters;
    }

    // ================================================================================================
    // POSTURAL COUNTERBALANCE -- the pelvis translates POSTERIORLY as the trunk folds forward.
    //
    // This is the third leg of a human forward-bend, and the one the solver was missing:
    //   1. pelvis anterior TILT (lumbopelvic rhythm)          -- BasisHipHingeCore
    //   2. pelvis posterior TRANSLATION (this file)           -- the counterweight
    //   3. spinal flexion spread over the segments            -- DistributeSpineBend + the ROM guard
    // With (2) absent the whole fold happens around a pinned pelvis, so the torso drives forward and down
    // into itself: the "chest rotates into the body when I look down" report. It is not a stylistic nicety
    // -- a standing human whose trunk pitches forward without the pelvis coming back has put their centre of
    // mass outside their base of support, which is to say they have started falling over.
    //
    // WHY IT REDUCES TO ONE MULTIPLY. Keep the whole-body COM over the mid-foot. Above-hip mass (head+neck
    // 8.1%, thorax 21.6%, abdomen 13.9%, arms 10.0%; Winter, Biomechanics and Motor Control of Human
    // Movement) is m_up ~ 0.54, its COM sits d ~ 0.55*L above the hip joint for a hips->neck chain of L.
    // The legs pivot about the ankle, so their COM travels ~x/2 when the pelvis travels x:
    //
    //     m_up*(d*sin0 - x) + m_low*(-x/2) = 0   =>   x = m_up*d*sin0 / (m_up + m_low/2) ~ 0.70*d*sin0
    //                                            =>   x ~ 0.38 * L * sin0
    //
    // and L*sin0 is EXACTLY the horizontal projection of the trunk. So the whole model is: the pelvis moves
    // back by a fixed fraction of how far the neck has travelled forward. No trig, no bone axes, no bind to
    // cancel. For L = 0.5 m that is 13 cm at 45 deg and 19 cm at 90 deg, against a measured 15-25 cm for a
    // real full forward bend.
    //
    // IT SATURATES BY CONSTRUCTION. sin0 peaks at horizontal and eases off past it, which is what a real
    // toe-touch does -- the trunk ends up hanging nearly straight down, so its forward lever arm shrinks back
    // toward zero even though the fold got deeper. The crouch sit-back it composes with (BasisCrouchOffsetCore)
    // has the opposite failure: it is driven by head HEIGHT, so it cannot tell a squat from a waist-fold and
    // runs away linearly at depth. Weighting one by FlexionFrac and the other by (1 - FlexionFrac) lets each
    // own the posture it actually describes.
    //
    // THE DIRECTION IS THE LEAN'S OWN, not the pelvis' facing. Counterbalance opposes the direction the mass
    // actually went, so a forward-left fold is answered back-right. Using a bone axis would only handle pure
    // sagittal bends AND would drag in the bind-frame problem that has bitten this file repeatedly (a bone's
    // local axes are a rig convention). Derived from positions alone, this is equivariant by construction.
    //
    // GATING lives in the job: no hip tracker. A tracked pelvis is measured and must feed straight through --
    // same contract as ApplyHipHinge and the hip-bob synthesis.
    // ================================================================================================
    public static class BasisTrunkCounterbalanceCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        /// <summary>
        /// Fraction of the neck's forward travel the pelvis answers with. Derived above from segment masses
        /// (~0.38); BasisTrunkCounterbalanceTests re-measures it against the mocap corpus.
        /// </summary>
        public const float DerivedGain = 0.38f;

        public static void Solve(in BasisTrunkCounterbalanceInput i, out BasisTrunkCounterbalanceResult r)
        {
            r = default;
            r.HipsPos = i.HipsPos;

            if (i.Gain <= 0f || i.MaxShift <= 0f)
            {
                return;
            }

            Vector3 up = i.PlayerUp.sqrMagnitude < k_SqrEpsilon ? Vector3.up : i.PlayerUp.normalized;
            Vector3 trunk = i.NeckCue - i.HipsPos;
            float len = trunk.magnitude;
            if (len < k_Epsilon)
            {
                return;
            }

            Vector3 horizontal = trunk - up * Vector3.Dot(trunk, up);
            float horizMag = horizontal.magnitude;
            // == sin(flexion), since horizMag == len * sin(flexion). Reported whenever the term is live, even
            // on the degenerate paths below, because the caller fades the crouch sit-back by (1 - this).
            r.FlexionFrac = Mathf.Clamp01(horizMag / len);
            if (horizMag < k_Epsilon)
            {
                return;
            }

            float shift = Saturate(i.Gain * horizMag, i.MaxShift);
            if (shift <= 0f)
            {
                return;
            }

            r.Applied = true;
            r.ShiftMeters = shift;
            r.HipsPos = i.HipsPos - (horizontal / horizMag) * shift;
        }

        /// <summary>
        /// Rational ease into the cap: linear until 0.8*cap, then slope-1 handover onto an asymptote, so the
        /// derivative never steps (a hard clamp steps 1 -> 0, and that step IS the pop). Same shape as
        /// BasisHipHingeCore.Saturate and the joint ROM guards.
        /// </summary>
        public static float Saturate(float x, float cap)
        {
            if (!(cap > k_Epsilon))
            {
                return 0f;
            }
            float soft = cap * 0.8f;
            if (!(x > soft))
            {
                return x < 0f ? 0f : x;
            }
            float m = cap - soft;
            float e = x - soft;
            return soft + m * e / (m + e);
        }
    }
}
