using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Where the knee goes for a user with NO knee tracker -- FITTED to real humans, not guessed at.
    ///
    /// ================================================================================================
    /// THE REFRAME: the knee is not free, so stop predicting a direction. Predict ONE ANGLE.
    ///
    /// With the hip and the foot both fixed, the knee is confined to a CIRCLE (Korein's swivel angle;
    /// the standard result for a redundant limb). The limb's entire redundancy collapses to ONE SCALAR: the
    /// swivel about the hip->foot axis. Predicting the knee IS predicting that angle.
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
    /// It briefly carried 27 more features describing the foots ORIENTATION (its rotation relative to its own
    /// T-pose, in the body frame). They measured beautifully -- 1.69 % against this model's 2.26 % -- and IN A
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
    /// ACCURACY -- knee position error, % of limb length, measured in BasisMocapMotionQualityTests:
    ///     no hint at all ............... 3.17 %
    ///     what this replaced ........... a FIXED hips-right bend normal (no model at all): 3.17 %
    ///     THIS MODEL ................... 2.26 %   (3.26 % leave-one-CLIP-out, so it generalises)
    ///     a real knee tracker ........ 0.84 %
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
    public static class BasisLegSwivelModel
    {
        /// <summary>
        /// The knees swivel angle, in radians, about the hip->foot axis.
        ///
        /// THE INPUT IS IN THE BODY FRAME AND MIRRORED, so both sides share one model:
        ///   tipLocal   (foot - hip), in the body frame, divided by limb length, with +x OUTWARD (negate x
        ///              for the LEFT limb), +y UP, +z FORWARD.
        ///
        /// The CALLER mirrors the result back: negate the returned angle for the left limb.
        /// </summary>
        public static float SwivelRad(in float3 tipLocal) => SwivelRad(tipLocal, out _);

        /// <summary>
        /// As above, and it also hands back HOW MUCH IT KNOWS. sin and cos are fitted as two independent
        /// polynomials, so nothing forces sqrt(s*s + c*c) to stay near 1. Least squares shrinks BOTH toward
        /// zero exactly where the true swivel is genuinely UNPREDICTABLE -- and atan2 near the origin does not
        /// fail, it SPINS. Across the corpus this magnitude falls under 0.2 on 0.000 % of frames, so the guard
        /// essentially never fires; it is here because the failure it prevents is a spinning knee.
        /// </summary>
        public static float SwivelRad(in float3 tipLocal, out float confidence)
        {
            // =====================================================================================
            // THE DOMAIN CLAMP, AND IT IS LOAD-BEARING.
            //
            // This is a 3rd-order polynomial with coefficients up to 15. Outside the box it was fitted in it is
            // not "approximate" -- it is a random number generator.
            //
            // THE HARNESS COULD NOT HAVE CAUGHT THIS, and that is the whole lesson. In mocap the foot is ON the
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
                (-1.42401812e+00f) * 1f +
                (+1.31204548e+01f) * x +
                (-3.31436759e+00f) * y +
                (-5.13963438e-01f) * z +
                (+6.66022738e+00f) * xx +
                (-2.45005945e+00f) * yy +
                (-4.50550353e+00f) * zz +
                (-3.15498676e+00f) * x*y +
                (+5.80910259e+00f) * x*z +
                (+3.01777196e+00f) * y*z +
                (+3.05160822e+01f) * xx*x +
                (-3.93460279e+00f) * yy*y +
                (-1.49765399e+00f) * zz*z +
                (+1.03666819e+01f) * xx*y +
                (-4.68705092e+00f) * xx*z +
                (+2.35052945e+01f) * yy*x +
                (-1.50879292e-01f) * yy*z +
                (+2.00408935e+01f) * zz*x +
                (-6.37087268e+00f) * zz*y +
                (+5.61328337e+00f) * x*y*z +
                (+5.27662035e+00f) * r +
                (-2.95335598e-01f) * r*r +
                (-2.48469087e-01f) * elev +
                (+9.79810298e-02f) * azim +
                (-4.78069715e-01f) * elev*elev +
                (-3.18820034e-03f) * azim*azim +
                (+5.71968344e-02f) * elev*azim +
                (-1.29454485e+00f) * r*elev +
                (-2.69645049e-02f) * r*azim +
                (-3.86597683e+01f) * r*x +
                (+8.64175663e+00f) * r*y +
                (+3.61784115e+00f) * r*z;

            float cosPhi =
                (+1.85948590e+00f) * 1f +
                (-1.20585417e+01f) * x +
                (+6.32805003e+00f) * y +
                (+2.40511051e+00f) * z +
                (+4.65230731e+00f) * xx +
                (+1.41434861e+00f) * yy +
                (-8.91840913e-01f) * zz +
                (-6.32556301e-01f) * x*y +
                (-2.72370080e+00f) * x*z +
                (-3.00578998e+00f) * y*z +
                (-2.24048593e+01f) * xx*x +
                (+4.07902999e+00f) * yy*y +
                (+5.14898048e+00f) * zz*z +
                (+8.39313831e+00f) * xx*y +
                (+2.01133078e+00f) * xx*z +
                (-2.07737556e+01f) * yy*x +
                (+4.14159751e+00f) * yy*z +
                (-2.02538885e+01f) * zz*x +
                (+4.10522062e+00f) * zz*y +
                (-3.55015188e+00f) * x*y*z +
                (-4.05832768e+00f) * r +
                (+5.17481500e+00f) * r*r +
                (-1.21892424e+00f) * elev +
                (+8.97873668e-02f) * azim +
                (-9.45650285e-01f) * elev*elev +
                (+1.18024350e-02f) * azim*azim +
                (+8.21939459e-02f) * elev*azim +
                (-2.60449245e+00f) * r*elev +
                (+2.60171548e-02f) * r*azim +
                (+3.16231928e+01f) * r*x +
                (-2.39213873e+00f) * r*y +
                (-8.96357655e+00f) * r*z;

            confidence = math.sqrt(sinPhi * sinPhi + cosPhi * cosPhi);
            return math.atan2(sinPhi, cosPhi);
        }

        /// <summary>
        /// The bend direction the two-bone solver wants: a unit vector PERPENDICULAR to the hip->foot axis,
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
