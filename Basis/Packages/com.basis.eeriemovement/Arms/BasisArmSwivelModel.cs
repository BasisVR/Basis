using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Where the elbow goes for a user with NO elbow tracker -- FITTED to real humans, not guessed at.
    ///
    /// ================================================================================================
    /// THE REFRAME: the elbow is not free, so stop predicting a direction. Predict ONE ANGLE.
    ///
    /// With the shoulder and the hand both fixed, the elbow is confined to a CIRCLE (Korein's swivel angle;
    /// the standard result for a redundant limb). The limb's entire redundancy collapses to ONE SCALAR: the
    /// swivel about the shoulder->hand axis. Predicting the elbow IS predicting that angle.
    ///
    /// This is THE FIX FOR THE SNAP AT FULL EXTENSION. Any angle you predict lands ON the reachable circle by
    /// construction. A 3-vector "bend direction" does not -- so the solver needs projections, fades and pole
    /// guards to drag it back on, AND AS THE LIMB STRAIGHTENS THAT CIRCLE SHRINKS TO A POINT. The swivel
    /// becomes undefined, the fades switch the hint OFF, and the pole is handed to a fallback. That handoff is
    /// the snap users report past ~95% extension. Predict the angle and it cannot happen: the angle stays
    /// defined at any extension, and as the circle collapses the resulting POSITION change goes to zero on its
    /// own. Nothing to fade, nothing to hand off, nothing to snap.
    /// ================================================================================================
    ///
    /// THIS MODEL READS POSITIONS ONLY, AND THAT IS A SCAR RATHER THAN A SIMPLIFICATION.
    ///
    /// It briefly carried 27 more features describing the hands ORIENTATION (its rotation relative to its own
    /// T-pose, in the body frame). They measured beautifully -- 2.12 % against this model's 3.45 % -- and IN A
    /// HEADSET THEY PUT THE ELBOWS UP BY THE EARS, near-inverted, on almost every frame. Two things were wrong,
    /// and both were knowable in advance:
    ///
    ///   1. THE T-POSE THEY DIVIDED BY WAS NOT RELIABLY A T-POSE. BasisLocalAvatarDriver calls
    ///      ResetAvatarAnimator() -- literally commented "Exit T-Pose" -- BEFORE it builds the rig, so bone
    ///      rotations read at job-build time are not guaranteed to be the rest pose the model was fitted
    ///      against. Divide by the wrong rest and the orientation features are not noisy. They are CONFIDENTLY
    ///      WRONG, which is the only kind of wrong that survives a test suite.
    ///
    ///   2. THE CORPUS SAID SO, IN WRITING, BEFORE ANY OF THIS WAS BUILT. Tests/MocapCorpus~/NOTICE.md:
    ///      "A mocap hand is not a controller... A VR controller's rotation is a GRIP convention. Anything in
    ///      the IK that reads the hand's rotation is being fed a convention it was not designed for, and a
    ///      result that hinges on it MUST BE CONFIRMED IN A HEADSET BEFORE IT IS BELIEVED."
    ///
    /// Neither can touch a POSITION. A limb's geometry is anatomy and it transfers; a bone's rotation is a
    /// modelling convention and it does not. So this model needs no T-pose, reads no rotation, and has nothing
    /// left of that kind to get wrong. If the orientation block is ever revived it needs a rest pose taken from
    /// TposeBoneSnapshot (which is captured while the avatar is provably T-posed) and an in-headset A/B before
    /// one word of its accuracy is believed.
    ///
    /// ACCURACY -- elbow position error, % of limb length, measured in BasisMocapMotionQualityTests:
    ///     no hint at all ............... 21.74 %
    ///     what this replaced ........... the bend LOOKUP + chicken-wing flare: 6.62 %, 34 pops
    ///     THIS MODEL ................... 3.45 %   (4.76 % leave-one-CLIP-out, so it generalises)
    ///     a real elbow tracker ........ 1.06 %
    ///
    /// SMOOTH BY CONSTRUCTION. A polynomial is C-infinity. There is no fade to tune here and no discontinuity
    /// to fade -- the derivative simply exists, everywhere.
    ///
    /// The coefficients are fitted to the HARNESS'S OWN DUMPED FEATURES, and that is not an accident. The first
    /// attempt fitted in a separate pipeline and scored 3.77 % there and 31 % in the harness -- the two
    /// disagreed about the mirror, and a mismatch in any ONE of handedness / body frame / mirror silently
    /// poisons the model. The harness dumps the exact inputs it feeds this function, Python fits on those,
    /// codegen emits this file. NEVER RE-FIT IN A DIFFERENT FRAME FROM THE ONE YOU EVALUATE IN.
    /// DO NOT HAND-EDIT THE COEFFICIENTS -- re-fit and re-generate.
    /// </summary>
    [BurstCompile]
    public static class BasisArmSwivelModel
    {
        /// <summary>
        /// The elbows swivel angle, in radians, about the shoulder->hand axis.
        ///
        /// THE INPUT IS IN THE BODY FRAME AND MIRRORED, so both sides share one model:
        ///   tipLocal   (hand - shoulder), in the body frame, divided by limb length, with +x OUTWARD (negate x
        ///              for the LEFT limb), +y UP, +z FORWARD.
        ///
        /// The CALLER mirrors the result back: negate the returned angle for the left limb.
        /// </summary>
        public static float SwivelRad(in float3 tipLocal) => SwivelRad(tipLocal, out _);

        /// <summary>
        /// As above, and it also hands back HOW MUCH IT KNOWS. sin and cos are fitted as two independent
        /// polynomials, so nothing forces sqrt(s*s + c*c) to stay near 1. Least squares shrinks BOTH toward
        /// zero exactly where the true swivel is genuinely UNPREDICTABLE -- and atan2 near the origin does not
        /// fail, it SPINS. Across the corpus this magnitude falls under 0.2 on 0.004 % of frames, so the guard
        /// essentially never fires; it is here because the failure it prevents is a spinning elbow.
        /// </summary>
        public static float SwivelRad(in float3 tipLocal, out float confidence)
        {
            // =====================================================================================
            // THE DOMAIN CLAMP, AND IT IS LOAD-BEARING.
            //
            // This is a 3rd-order polynomial with coefficients up to 15. Outside the box it was fitted in it is
            // not "approximate" -- it is a random number generator.
            //
            // THE HARNESS COULD NOT HAVE CAUGHT THIS, and that is the whole lesson. In mocap the hand is ON the
            // limb, so |tipLocal| <= 1 on every frame it has ever seen. THE LIVE RIG IS HANDED THE RAW CONTROLLER
            // TARGET, which sails past the avatar's arm length constantly -- anyone whose arms are longer than
            // their avatar's is outside the fit domain on essentially every frame. r = 1.0 versus r = 1.3 does
            // not sound like much until you multiply it by these coefficients, and then it is the difference
            // between an elbow and a coin flip.
            //
            // The two-bone solver has always clamped its own reach. The MODEL never did. It does now.
            // =====================================================================================
            float len = math.length(tipLocal);
            float3 t = len > 1f ? tipLocal / len : tipLocal;

            float x = t.x, y = t.y, z = t.z;
            float r = math.min(len, 1f);

            float elev = math.asin(math.clamp(y / math.max(r, 1e-6f), -1f, 1f));
            float azim = math.atan2(x, z);

            float xx = x * x, yy = y * y, zz = z * z;

            // Straight-line, no array, no indirection: Burst folds the constants into the instruction stream
            // and fuses the multiply-adds.
            float sinPhi =
                (+4.85046596e+00f) * 1f +
                (+8.90283261e+00f) * x +
                (+3.07357926e+00f) * y +
                (-1.51330608e+01f) * z +
                (-2.31288634e+00f) * xx +
                (+7.11251880e-01f) * yy +
                (+2.33544551e+00f) * zz +
                (+6.87526493e+00f) * x*y +
                (-8.86284750e+00f) * x*z +
                (+3.55286082e+00f) * y*z +
                (-6.39415478e-01f) * xx*x +
                (+4.21283105e+00f) * yy*y +
                (-2.03854156e+01f) * zz*z +
                (+1.61805809e+00f) * xx*y +
                (-1.59594310e+01f) * xx*z +
                (+2.10802270e+00f) * yy*x +
                (-1.83086585e+01f) * yy*z +
                (+1.21524009e+00f) * zz*x +
                (+1.44646499e+00f) * zz*y +
                (-4.31874597e+00f) * x*y*z +
                (-7.28046688e+00f) * r +
                (+7.33811054e-01f) * r*r +
                (+1.09256348e+00f) * elev +
                (-3.36700072e+00f) * azim +
                (-1.53126360e-02f) * elev*elev +
                (-6.10252578e-02f) * azim*azim +
                (-1.86256383e+00f) * elev*azim +
                (-1.55587489e+00f) * r*elev +
                (+7.30125904e-01f) * r*azim +
                (+5.68798364e-01f) * r*x +
                (-7.90477911e+00f) * r*y +
                (+3.49703473e+01f) * r*z;

            float cosPhi =
                (+9.84537763e-01f) * 1f +
                (-3.16113670e+00f) * x +
                (-8.04111222e+00f) * y +
                (+7.34046089e-01f) * z +
                (+3.48689746e+00f) * xx +
                (-1.27489407e+00f) * yy +
                (+1.33228252e+00f) * zz +
                (-4.74369870e+00f) * x*y +
                (+1.03752079e+01f) * x*z +
                (-4.38160865e+00f) * y*z +
                (+7.84462892e-01f) * xx*x +
                (-7.18542834e+00f) * yy*y +
                (+8.47199334e-01f) * zz*z +
                (-5.22205922e+00f) * xx*y +
                (-3.17926897e+00f) * xx*z +
                (+1.08815818e+00f) * yy*x +
                (+5.85072394e-01f) * yy*z +
                (-4.47132338e+00f) * zz*x +
                (-4.12105900e+00f) * zz*y +
                (+4.73267686e+00f) * x*y*z +
                (-1.59196444e+00f) * r +
                (+3.54428591e+00f) * r*r +
                (+1.62177672e+00f) * elev +
                (+7.52910269e-01f) * azim +
                (+3.67391608e-01f) * elev*elev +
                (-2.08559102e-01f) * azim*azim +
                (+9.14122723e-01f) * elev*azim +
                (-9.76695565e-01f) * r*elev +
                (+5.77396398e-01f) * r*azim +
                (-4.87962903e+00f) * r*x +
                (+1.57662928e+01f) * r*y +
                (-4.91418674e+00f) * r*z;

            confidence = math.sqrt(sinPhi * sinPhi + cosPhi * cosPhi);
            return math.atan2(sinPhi, cosPhi);
        }

        /// <summary>
        /// The bend direction the two-bone solver wants: a unit vector PERPENDICULAR to the shoulder->hand axis,
        /// pointing at the swivel angle this model predicts. Perpendicular by construction -- the solver has
        /// nothing to project away and no near-singular projection to guard.
        /// </summary>
        public static float3 BendDirection(float3 rootToTip, float3 reference, float swivelRad)
        {
            float3 axis = math.normalizesafe(rootToTip, new float3(0f, -1f, 0f));
            float3 u = math.normalizesafe(reference - axis * math.dot(reference, axis), new float3(0f, 0f, -1f));
            float3 v = math.cross(axis, u);
            math.sincos(swivelRad, out float sn, out float cs);
            return u * cs + v * sn;
        }
    }
}
