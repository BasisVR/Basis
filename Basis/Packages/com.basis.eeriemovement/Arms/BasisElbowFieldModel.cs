using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Where the elbow goes for a user with NO elbow tracker. It predicts the elbow's POSITION and
    /// projects it onto the reachable circle. It does not predict an angle, and that is the point.
    ///
    /// ================================================================================================
    /// WHY THE PREVIOUS MODEL (BasisArmSwivelModel) FLIPPED, AND WHY NO REFIT COULD HAVE SAVED IT.
    ///
    /// With the shoulder and hand fixed the elbow lies on a CIRCLE, so its redundancy is ONE SCALAR --
    /// the swivel angle. That reframe was right, and predicting the angle really does kill the snap at
    /// full extension. But AN ANGLE HAS TO BE MEASURED FROM SOMETHING, and that reference direction is
    /// where the whole thing came apart:
    ///
    ///     u = normalize(down - axis * dot(down, axis))
    ///
    /// |u| is the sine of the arm's angle off vertical. IT IS ZERO WHEN THE HAND IS DIRECTLY BELOW THE
    /// SHOULDER -- and measured across the 55,140-frame corpus, |u| < 0.2 on 29.7% OF REAL HUMAN FRAMES,
    /// with a minimum of 0.001. On nearly a third of poses the swivel was being measured against a
    /// direction that barely exists, and `normalizesafe` then hard-snapped it to a fallback.
    ///
    /// Instrumented, on the shipped model: the hand sways 3 cm fore-aft under the shoulder -- standing
    /// still, arms relaxed, the commonest pose in VR -- and the elbow swings 49 degrees, travelling
    /// 19.6 cm. That is the flip.
    ///
    /// AND IT IS NOT A CHOICE OF REFERENCE. The pole is a unit tangent vector on the sphere of hand
    /// directions, so by the HAIRY BALL THEOREM every continuous choice of it vanishes SOMEWHERE. (The
    /// same fact in coordinates: sin and cos fitted as independent affine functions have a common zero
    /// LINE -- two planes meet in a line.) The singularity cannot be deleted. It can only be MOVED.
    /// The shipped model moved it onto the rest pose.
    ///
    /// A POSITION IS NOT A TANGENT VECTOR, so it carries no such obstruction. Predict the elbow's
    /// position, project it onto the circle, and the only degeneracy left is "the predicted elbow lands
    /// ON the shoulder->hand axis" -- which is 0.036% of the reachable workspace.
    ///
    /// BUT THE PROJECTED BEND IS A TANGENT VECTOR AGAIN, and Poincare-Hopf says any bend field over the
    /// sphere of hand directions has total index 2: TWO zeros per reach shell, somewhere. This model's
    /// sit at hand ACROSS-THE-BODY-AND-UP (-0.88, 0.47, -0.09, deepest at 0.75 reach) and at a shallow
    /// valley DOWN-AND-BACK behind the hip. They cannot be deleted, only placed; everything below is
    /// about what happens NEAR them.
    ///
    /// ================================================================================================
    /// WHY THE FADE THIS MODEL SHIPPED WITH IS GONE (2026-07-17, "big arm swings flip drastically").
    ///
    /// It used to blend the projected bend toward a fixed rest pole below a conditioning of 0.10:
    ///
    ///     normalizesafe(primary * w + rest * (1 - w), rest)
    ///
    /// A LERP OF TWO UNIT DIRECTIONS PASSES THROUGH ZERO WHERE THEY ARE ANTIPODAL AND w CROSSES 0.5.
    /// "Fade, never gate" was the intent; an antipodal lerp IS a gate -- normalize(~0) flips the whole
    /// output the moment the blend crosses its cancellation. And that cancellation surface does not sit
    /// at the model's zeros, where distrust is deserved -- it cuts through HEALTHY workspace where the
    /// model still has a 3-6 cm lever, exactly where dot(primary, rest) = -1 happens to fall:
    ///
    ///   * hand swinging ACROSS the body at 0.45 reach: elbow teleports 18.4 then 28.7 cm in single
    ///     0.5-degree hand steps (hint direction jumps 73 degrees in one frame), at conditioning 0.06;
    ///   * the DOWN-AND-BACK follow-through of a big swing at 0.65 reach: same event, 108 degrees.
    ///
    /// That is the reported flip, reproduced deterministically -- and the fade also LOST its own
    /// bargain: under 2 mm of hand noise inside its band it moved the elbow up to 25.5 deg (p99 6.5)
    /// against 8.1 deg (p99 3.2) without it. The rest pole now serves only as the normalizesafe
    /// fallback AT the zeros themselves.
    ///
    /// MEASURED, full-sphere meridian sweeps at 0.5 deg of hand elevation per step, worst single-step
    /// bend rotation (flip = tens of degrees in one step):
    ///
    ///                                reach 0.45   reach 0.65   reach 0.85   noise p99 (2mm, old band)
    ///     with the fade ..........      78 deg      108 deg      114 deg      6.5 deg
    ///     projection only ........      21 deg      166 deg*      20 deg      3.2 deg
    ///
    ///     * only within ~1 degree of a zero core, where the fade construction was equally violent;
    ///       everywhere the lever is over 0.02 arm lengths the step now stays under ~21 degrees.
    /// ================================================================================================
    ///
    /// MEASURED, leave-one-CLIP-out over the corpus, elbow position error as % of arm length, and
    /// elbow travel per unit of HAND travel over the whole reachable workspace (a real elbow tracks its
    /// hand at 0.5-1.5x; above ~5x reads as a flip):
    ///
    ///                                 err     gain p99   >20x       idle-sway step
    ///     BasisArmSwivelModel ....  4.76 %      68 x     1.67 %        1.13 cm
    ///     THIS MODEL ............  4.19 %     5.2 x     0.065 %        0.19 cm
    ///
    /// It is more accurate out of sample with TWELVE coefficients against sixty-four. The old cubic's
    /// coefficients ran to 35 not because a human elbow needs a cubic, but because it was spending its
    /// capacity cancelling the singularity of its own reference frame.
    ///
    /// FITTED ON THE HARNESS'S OWN DUMPED FEATURES (BasisMocapAccuracy -> basis_swivel_train.csv), so
    /// the fit frame and the eval frame cannot disagree. A mismatch in any one of handedness / body
    /// frame / mirror does not degrade this kind of model, it produces CONFIDENT GARBAGE -- that has
    /// happened twice on this project. DO NOT HAND-EDIT THE COEFFICIENTS. Re-fit and re-generate.
    /// </summary>
    [BurstCompile]
    public static class BasisElbowFieldModel
    {
        /// <summary>
        /// Route the no-tracker elbow bend through <see cref="BasisElbowStereoModel"/> instead of this
        /// field's projected-position bend. It EXISTS TO KILL THE REACH-BEHIND SNAP -- this field's
        /// down-and-back topological core (azimuth ~130 deg, ~20 deg below level: hand out behind the hip,
        /// arm bent). Measured on 199,528 real frames: worst reach-behind hint rotation 33 deg/frame -> 0.3,
        /// worst reachable elbow gain ~106x -> 7x, elbow error vs humans 9.1% -> 7.7%. The two topological
        /// zeros this field cannot avoid become ONE, placed inside the torso where no hand can point.
        ///
        /// ⚠️ NOT VERIFIED IN A HEADSET. It is the default because it is measured-better everywhere the
        /// offline harness can see (the same reasoning that made VSpinePostureModel default ON), and it is
        /// STATELESS -- so it does not carry the live-vs-offline state gap that made the elbow-pole COAST
        /// clean offline and worse live.
        ///
        /// ⚠️⚠️ DEFAULT IS false: the stereo field ELIMINATES the reach-behind snap, but in a headset it
        /// posed the elbow ACROSS THE BODY (inward) reaching BEHIND THE BACK -- "does not look human" --
        /// and that inward pole, swivelled onto near full extension, reads as arm ROLL when over-stretching.
        /// Root cause is fundamental, not a tuning miss: the stereo field's ONE zero has to hide in the
        /// torso, and the torso is right next to the behind-the-back reach, so its base field combs the
        /// elbow inward there and no smooth theta pulls it back out without re-growing the gain (measured
        /// every which way -- order-1/2/3 theta, upweighting, an old-field teacher). This field (a global
        /// polynomial) poses the elbow correctly OUT-and-BACK reaching behind; its only fault is the az130
        /// snap. Reaching behind is a genuine stateless tradeoff: snap vs pole-side. The real "both" fix
        /// needs STATE (hysteresis through the reconfiguration, redone with an in-headset loop) or actual
        /// behind-the-back mocap (CMU is a desert there). See the elbow-field memory.
        ///
        /// `static readonly`, not a settable bool, ON PURPOSE: ArmHint runs inside the Burst job
        /// (BasisFullIKConstraintJob), and Burst forbids loading a MUTABLE static field (BC1040). A readonly
        /// static folds to a constant, so both branches compile. Flip to true + recompile to A/B the stereo.
        /// </summary>
        public static readonly bool UseStereoField = false;

        /// <summary>The anatomical rest pole, in the mirrored body frame (+x OUT, +y UP, +z FWD): an elbow
        /// hangs DOWN, a little OUT, and a little BACK. Consulted only where the projected bend has no
        /// length at all -- the exact zero cores -- as normalizesafe's fallback, never blended in.
        /// Magnitude is irrelevant; only the direction is used.</summary>
        static readonly float3 k_RestPole = new float3(0.35f, -1.0f, -0.15f);

        /// <summary>
        /// The elbow's position relative to the shoulder, in the mirrored body frame, in arm lengths.
        ///
        /// `tipLocal` is (hand - shoulder) in the same frame and units, with +x OUTWARD for both limbs so
        /// one model serves both. The caller un-mirrors x on the way back out.
        /// </summary>
        public static float3 Elbow(float3 tipLocal)
        {
            float len = math.length(tipLocal);
            float3 t = len > 1f ? tipLocal / len : tipLocal;
            float x = t.x, y = t.y, z = t.z;

            return new float3(
                (+0.25611932f) + (+0.23203308f) * x + (+0.23016090f) * y + (-0.03095514f) * z,
                (-0.16631846f) + (+0.09813791f) * x + (+0.35133371f) * y + (-0.10962090f) * z,
                (-0.03474265f) + (-0.06358632f) * x + (+0.12388336f) * y + (+0.45664834f) * z);
        }

        /// <summary>
        /// The bend direction the two-bone solver wants: a UNIT vector PERPENDICULAR to the shoulder->hand
        /// axis. Perpendicular by construction, so it lies on the elbow's reachable circle and the solver
        /// has nothing to project away and no near-singular projection to guard.
        ///
        /// THE PROJECTION IS THE WHOLE FUNCTION. No fade, no blend, no second opinion: blending this
        /// direction toward any fixed pole re-creates the flip (see the file header -- an antipodal lerp
        /// cancels, and its cancellation surface cut through healthy workspace; it is also LOUDER under
        /// noise than the raw projection, 25.5 deg vs 8.1 under 2 mm of hand jitter). The rest pole enters
        /// only as normalizesafe's fallback, on the measure-zero cores where the projection has no length.
        ///
        /// `elbowLocal` is this model's prediction, `tipLocal` the hand, both in the mirrored body frame.
        /// `conditioning` comes back as the pole's lever arm in arm lengths -- a purely GEOMETRIC number
        /// (how far the predicted elbow stands off the arm's own axis), not a regression's opinion of
        /// itself. It is reported for tests and gizmos.
        /// </summary>
        public static float3 BendDirection(float3 tipLocal, float3 elbowLocal, out float conditioning)
        {
            float3 axis = math.normalizesafe(tipLocal, new float3(0f, -1f, 0f));

            float3 perp = elbowLocal - axis * math.dot(elbowLocal, axis);
            conditioning = math.length(perp);

            float3 restPerp = k_RestPole - axis * math.dot(k_RestPole, axis);
            float3 rest = math.normalizesafe(restPerp, new float3(0f, 0f, -1f));

            return math.normalizesafe(perp, rest);
        }
    }
}
