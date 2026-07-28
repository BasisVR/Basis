using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// The range of motion of the human spine, per segment, per axis.
    ///
    /// ================================================================================================
    /// THE TABLE. Degrees; lateral and axial are PER SIDE.
    ///
    ///                                        flex   ext   lat   AXIAL      corpus p99 (real humans)
    ///   Lumbar         L1-S1   Unity Spine     60    20    25      12      49.2 / 10.7 / 12.5 / 10.2
    ///   LowerThoracic  T7-T12  Unity Chest     25    15    20      20      18.2 / 11.1 / 10.4 / 14.2
    ///   UpperThoracic  T1-T6   Unity UpprCh    15    30    25      15       5.4 / 18.4 / 13.1 /  6.6
    ///   Cervical       C1-C7   Unity Neck      50    60    45      75      23.8 / 35.7 / 29.5 / 20.8
    ///
    /// EVERY LIMIT IS ABOVE THE p99 OF REAL HUMAN MOTION. That is the acceptance test, and it is the right
    /// way round: the limits are max-voluntary-ROM from the clinical literature (what a spine CAN do), and
    /// the corpus only gets a veto (what people DID do). Fit the limits TO the corpus and you encode
    /// "CMU subjects rarely side-bend" as "humans cannot side-bend", and then you clip the first VR user who
    /// leans over to look at something. Measured across 35,081 frames the whole table fires on under 0.6% of
    /// frames, and only on poses the literature says are impossible.
    ///
    /// SOURCES, and why the axial column is the one that matters:
    ///
    ///   LUMBAR AXIAL = 12 deg/side. Fujii 2007, Eur Spine J 16(11):1867 -- in-vivo 3D MRI, at 45 deg of
    ///   trunk rotation L1-S1 sums to 7.7 deg/side (1.2-1.7 deg per segment), age-invariant. The lumbar
    ///   facets are near-sagittal: they interlock and simply will not permit twist. 12 leaves headroom over
    ///   both Fujii and the corpus's 10.2 p99. THE SHIPPED RIG ALLOWED 25, AND THEN, PAST THE CCD, ANY VALUE
    ///   AT ALL -- the corpus contains a 34.9 deg lumbar twist and the rig would reproduce it.
    ///
    ///   THORACIC AXIAL = 15-20 deg/side. Thoracic in-vivo total ~30 deg/side (Ichikawa 2024), split across
    ///   the two halves, with the upper thorax stiffer because its ribs anchor to the sternum. The ribcage
    ///   roughly HALVES thoracic rotation (Liebsch 2017) -- so do not use rib-stripped in-vitro per-segment
    ///   numbers, which is where the folklore "the spine twists freely" comes from.
    ///
    ///   THORACIC FLEXION = 15-25 deg. The thorax barely flexes; the ribs are in the way. The corpus is
    ///   blunt about it -- during a real forward bend the upper thorax contributes NEGATIVE 9.3% of the
    ///   total, i.e. it EXTENDS slightly, to keep the head up. It pitched 5.4 deg at p99 and the shipped rig
    ///   allowed it 60. That gap is the user's "looking down forces the chest to rotate".
    ///
    ///   CERVICAL = the freest segment in the body, and the numbers are unremarkable: 50/60/45/75.
    ///
    ///   Standing max trunk twist is ~70% PELVIS, ~30% SPINE (Taniguchi 2017, PLoS ONE: thorax ~92 deg/side,
    ///   pelvis ~65, spine-relative-to-pelvis only ~26.5). A rig with an unbounded chest-vs-pelvis twist is
    ///   wrong by 2-3x, which is what this table exists to stop.
    /// ================================================================================================
    ///
    /// Related: BasisSpineAnatomyCore (the guard), BasisElbowAnatomyCore + BasisLegSolveCore (the same
    /// doctrine on the limbs -- guard the OUTCOME, run LAST, be the identity where the pose is legal).
    /// </summary>
    public static class BasisSpineAnatomy
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        public static BasisSpineRom Rom(BasisSpineSegment segment)
        {
            switch (segment)
            {
                case BasisSpineSegment.Lumbar:        return new BasisSpineRom(60f, 20f, 25f, 12f);
                case BasisSpineSegment.LowerThoracic: return new BasisSpineRom(25f, 15f, 20f, 20f);
                case BasisSpineSegment.UpperThoracic: return new BasisSpineRom(15f, 30f, 25f, 15f);
                case BasisSpineSegment.Cervical:      return new BasisSpineRom(50f, 60f, 45f, 75f);
                default:                              return new BasisSpineRom(60f, 20f, 25f, 12f);
            }
        }

        /// <summary>
        /// Build a bone's anatomical rest frame from the T-POSE, using POSITIONS for the two axes that
        /// matter and the bone's own rest rotation only as the neutral.
        ///
        /// THE AXES COME FROM POSITIONS BECAUSE A BONE'S LOCAL AXES ARE A RIG CONVENTION AND DO NOT TRANSFER.
        /// This project has been bitten by that repeatedly -- it is why the arm swivel model is position-only
        /// and why BasisFootSimParams carries a rest-basis map. `up` is the bone's own long axis (joint to
        /// child); `right` is the subject's right, taken from the hips, which is a body-wide fact and not a
        /// property of any one bone. Everything is then expressed in the PARENT's rest frame, which is the
        /// frame the joint's local rotation already lives in.
        ///
        /// `boneWorld`/`childWorld` are the bone and its next bone down the chain; `parentWorldRot` is the
        /// bone's parent. `hipsRightWorld` is the subject's right, from the hips.
        /// </summary>
        public static BasisSpineRestFrame BuildRestFrame(
            Vector3 boneWorldPos, Vector3 childWorldPos,
            Quaternion boneWorldRot, Quaternion parentWorldRot,
            Vector3 hipsRightWorld)
        {
            BasisSpineRestFrame f = default;
            f.Valid = false;

            Vector3 upW = childWorldPos - boneWorldPos;
            float upSqr = upW.sqrMagnitude;
            // Reject-unless-good: a NaN or a zero-length bone must leave `Valid` false, and `!(x > eps)`
            // catches NaN where `x < eps` would wave it through.
            if (!(upSqr > k_SqrEpsilon) || !(hipsRightWorld.sqrMagnitude > k_SqrEpsilon))
            {
                return f;
            }
            upW /= Mathf.Sqrt(upSqr);

            // Orthogonalise the body's right against THIS bone's axis. The spine's segments are not parallel
            // (the thorax leans back off the lumbar), so a shared world `right` is not perpendicular to any
            // given bone -- and a non-orthogonal frame would leak flexion into the lateral read-out.
            Vector3 rightW = hipsRightWorld - upW * Vector3.Dot(hipsRightWorld, upW);
            float rSqr = rightW.sqrMagnitude;
            if (!(rSqr > k_SqrEpsilon))
            {
                // The bone points along the body's right: it is not a spine bone. Decline rather than invent.
                return f;
            }
            rightW /= Mathf.Sqrt(rSqr);

            Vector3 fwdW = Vector3.Cross(rightW, upW);   // Unity: cross(right, up) == forward

            // Into the PARENT's frame -- the frame the local rotation, and therefore `delta`, lives in.
            Quaternion invParent = BasisSpineAnatomyCore.Conj(parentWorldRot);
            f.Right = invParent * rightW;
            f.Up = invParent * upW;
            f.Forward = invParent * fwdW;
            f.RestLocalRot = invParent * boneWorldRot;
            f.Valid = true;
            return f;
        }
    }
}
