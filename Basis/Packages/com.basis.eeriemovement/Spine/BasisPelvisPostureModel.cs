using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Where the PELVIS goes when a VR user's head gets low -- FITTED to real humans, not guessed at.
    ///
    /// ================================================================================================
    /// THE REFRAME: a low head is not one pose, it is TWO, and the rig could not tell them apart.
    ///
    ///     WAIST-BEND   the spine folds forward. The pelvis stays HIGH and slides back over the heels.
    ///     SQUAT / SIT  the spine stays stacked. The pelvis rides the head almost straight DOWN.
    ///
    /// A head-mounted display gives you a head and nothing else, so these two arrive looking identical on
    /// the one axis the old law looked at -- height. Measured on 44 CMU clips, the real pelvis-drop-per-
    /// head-drop coupling is 0.02-0.14 for a waist-bend (picking a ball off the floor, sweeping) and
    /// 0.78-0.99 for a squat or a sit. That is not a spread around a mean. It is two different behaviours,
    /// and no single constant is even approximately right for both.
    ///
    /// The rig shipped a single constant anyway:
    ///     hipsDrop = lerp(headDrop, 0.30 * (1 - exp(-headDrop / 0.30)), 0.85)
    /// an exponential saturation that holds the pelvis up. On a waist-bend that is roughly right and it is
    /// why it was added -- the rigid model buried the pelvis and folded the knees. On a SQUAT it is a
    /// disaster: at a 0.70 m head drop it delivers 0.335 m of pelvis, and a real human delivers 0.66 m.
    /// It holds the pelvis 32.8 cm ABOVE where it belongs.
    ///
    /// AND THE HEAD IS WELDED TO THE HMD. Every centimetre the body declines to travel is a centimetre the
    /// SPINE AND NECK have to find by rotating. That is the "gamer neck / tortoise" users report when they
    /// bend over, and it is arithmetic, not taste.
    /// ================================================================================================
    ///
    /// THE DISCRIMINATOR IS FORWARD LEAN, and it is the input the old law never had. A squat barely travels
    /// horizontally; a waist-bend travels a long way. Hand the model both and it separates them cleanly:
    ///
    ///     k(d,f)      f=0.0   f=0.2   f=0.4   f=0.6      (f = head's horizontal lean, fraction of height)
    ///       d=0.10     1.04    0.40    0.00    0.00      lean without dropping  -> pelvis stays put
    ///       d=0.30     1.11    0.73    0.24    0.00      bend over              -> pelvis stays put
    ///       d=0.50     0.78    0.67    0.76    0.67      squat / sit            -> pelvis rides it down
    ///
    /// ACCURACY -- pelvis height error against a real human, on the corpus, in cm for a 1.7 m user:
    ///     the shipped saturation law ..... 8.3 cm
    ///     THIS MODEL ..................... 4.5 cm   (5.2 cm leave-one-CLIP-out, so it generalises)
    ///
    /// ⭐ IT IS STRUCTURED SO THE DANGEROUS THINGS CANNOT HAPPEN.
    ///
    /// The output is not the pelvis height, it is the COUPLING k, and the caller multiplies by the head drop.
    /// Factoring the drop out is not cosmetic:
    ///   * k * 0 == 0, so a user STANDING STILL has a pelvis that has not moved. Exactly zero. As an
    ///     algebraic identity, not as something a 3rd-order polynomial has to be trusted to get right. The
    ///     fit is THROUGH THE ORIGIN for the same reason.
    ///   * k has a physical meaning and therefore a physical RANGE -- 0 (pelvis ignores the head) to 1
    ///     (pelvis follows it exactly) -- so clamping it is meaningful rather than arbitrary, and a
    ///     polynomial that runs away outside its fit domain simply saturates into a pose that is merely
    ///     conservative instead of into one that is insane. Raw k spans -3.09..+1.23 across the domain; the
    ///     clamp is doing real work on the waist-bend corner, where "less than zero" would mean the pelvis
    ///     RISES as you bend, which no human does.
    ///
    /// ⚠ NOT FITTED, AND DELIBERATELY SO: the pelvis's HORIZONTAL placement. I tried. The same features
    /// predict it with a leave-one-clip-out error of 11.6 cm against the shipped constant's 10.6 cm -- i.e.
    /// the fit does not generalise and is WORSE than doing nothing. Where a person puts their pelvis
    /// horizontally depends on their balance strategy and where their feet are, and (drop, lean) does not
    /// carry that. So no (drop,lean) FIT ships for horizontal placement: a model that loses to the baseline
    /// out-of-sample does not ship, however much one would like it to. (ComputeRealisticHipsXZBurst was later
    /// given an anisotropic forward/lateral follow — a geometric weight-shift argument, NOT a corpus fit —
    /// which is a different thing from the regression rejected here and leaves this warning intact.)
    ///
    /// Fitted by least squares on the harness's OWN dumped features (BasisPostureCorpusTests), leave-one-
    /// clip-out validated, 44 clips / 20,653 in-domain frames -- the corpus in Tests/MocapCorpus~/posture,
    /// exactly as it sits in the repo, so re-running the fit reproduces these numbers. NEVER RE-FIT IN A
    /// DIFFERENT FRAME FROM THE ONE YOU EVALUATE IN -- that has already cost this project a full cycle.
    /// DO NOT HAND-EDIT THE COEFFICIENTS -- re-fit and re-generate.
    /// </summary>
    [BurstCompile]
    public static class BasisPelvisPostureModel
    {
        /// <summary>The box the fit actually saw. A standing user cannot exceed these, and the caller clamps
        /// into them before evaluating, so the polynomial is never asked to extrapolate.</summary>
        public const float MaxDrop = 0.65f;   // head 65% of standing head height below standing = a deep squat
        public const float MaxLean = 0.60f;   // head 60% of body height horizontally off the support base

        /// <summary>
        /// How much of the head's drop the PELVIS should follow. 0 = the pelvis stays where it is and the
        /// spine folds (a waist-bend). 1 = the pelvis rides the head straight down (a squat or a sit).
        ///
        ///   drop   (standing head height - current head height) / standing head height.   >= 0
        ///   lean   horizontal distance from the head to the SUPPORT BASE (the feet, or the standing spot
        ///          when the feet are not tracked), over that same standing head height.   >= 0
        ///
        /// Both are fractions of the user's own height, so the model is SCALE-FREE: a child avatar and a
        /// giant get the same posture rather than the same centimetres.
        /// </summary>
        public static float Coupling(float drop, float lean)
        {
            // Reject-unless-good. NaN fails every ordered comparison, so `!(x >= 0)` catches it where
            // `x < 0` would wave it through -- and a NaN here becomes a NaN pelvis, which in Unity PERSISTS
            // in the transform and never recovers. 0 is the safe answer: the pelvis simply does not move.
            if (!(drop >= 0f) || !(lean >= 0f))
            {
                return 0f;
            }

            float d = math.min(drop, MaxDrop);
            float f = math.min(lean, MaxLean);

            float k =
                (+8.57863984e-01f) * 1f +
                (-3.02568994e+00f) * f +
                (+2.31442802e+00f) * d +
                (-2.59377618e+00f) * f*f +
                (+2.55773595e+00f) * d*f +
                (-4.91301461e+00f) * d*d +
                (-7.99234298e+00f) * f*f*f +
                (+1.99478947e+01f) * d*f*f;

            return math.clamp(k, 0f, 1f);
        }

        /// <summary>
        /// The pelvis drop, in the same units as `drop`. This is the whole model: k * the head's drop.
        /// Zero drop in, zero drop out -- always, exactly.
        /// </summary>
        public static float PelvisDrop(float drop, float lean) => Coupling(drop, lean) * math.max(drop, 0f);
    }
}
