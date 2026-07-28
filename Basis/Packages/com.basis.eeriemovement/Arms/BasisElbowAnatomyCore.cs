using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// AN ELBOW CANNOT POINT AT THE SKY. This makes that unreachable rather than merely discouraged.
    ///
    /// ================================================================================================
    /// THE LAW, MEASURED, NOT INVENTED:
    ///
    ///     THE ELBOW NEVER RISES ABOVE THE SHOULDER, NOR ABOVE THE HAND -- WHICHEVER IS HIGHER.
    ///
    /// Across 55,140 frames of real human arm motion (the CMU corpus, both arms, every clip), the worst
    /// violation of that statement is +0.015 of an arm length -- nine millimetres. It is violated on 0.0000%
    /// of frames at a margin of 0.05, and a margin of 0.10 would clamp ZERO poses a human actually made.
    /// Broken out by where the hand is:
    ///
    ///     hand well below the shoulder .. 43,556 frames ... elbow above the shoulder on 0.000%
    ///     hand below .................... 4,232 frames .... 0.000%
    ///     hand just below ............... 2,186 frames .... 0.183%
    ///     hand at or above .............. 2,636 frames .... 15.3%
    ///     hand high ..................... 2,530 frames .... 71.9%
    ///
    /// You cannot lift your elbow above your shoulder while your hand hangs low. The humerus will not do it.
    /// So this is a HARD ANATOMICAL CONSTRAINT of exactly the same status as BasisLegSolveCore's anterior
    /// half-space guard ("a knee behind that axis is not unnatural, it is anatomically unrepresentable"), and
    /// it is not a tuning knob. The KNEE has had that guard for a long time. THE ARM HAD NOTHING -- nothing at
    /// all stopped the solver placing the elbow anywhere on its circle, including straight up, which is the
    /// "arms get into rotations that are not possible / the elbow points at the sky" report.
    /// ================================================================================================
    ///
    /// IT GUARDS THE ELBOW, NOT THE HINT, AND THAT IS DELIBERATE. A guard on the hint protects you from a bad
    /// hint; a guard on the OUTCOME protects you from everything -- a bad hint, a mis-strapped elbow tracker, a
    /// torso-collision push, the animation the solve started from. There is no path by which the arm can end a
    /// frame outside the envelope, because the last thing that happens is this.
    ///
    /// IT COSTS NO REACH, STRUCTURALLY. The correction is a swivel ABOUT THE SHOULDER->HAND AXIS, and the hand
    /// LIES on that axis -- a rotation about a line cannot move a point on that line. Reach preservation is not
    /// a tolerance here, it is geometry.
    ///
    /// AND IT IS THE IDENTITY WHERE IT SHOULD BE. Below the soft margin it returns exactly 0f, so a legal pose
    /// is not perturbed by a guard that has no business firing -- bit for bit the unguarded solver.
    /// </summary>
    public static class BasisElbowAnatomyCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        /// <summary>
        /// Everything below this above the ceiling passes through UNTOUCHED. The corpus's worst real human
        /// violation is 0.015, so 0.05 (3 cm on a 0.6 m arm) clears every frame a human has ever produced with
        /// room to spare -- and leaves headroom for the poses a VR user strikes that a CMU subject walking
        /// around a lab never did.
        /// </summary>
        public const float SoftMarginFracLimb = 0.05f;

        /// <summary>
        /// The asymptote. The elbow eases toward this and never reaches it, so the envelope is closed by
        /// construction rather than by a tolerance -- the same rational saturation, with the same slope-1
        /// handover, that BasisLegSolveCore.ClampKneeSwivelDeg uses on the knee. Slope-1 means NO derivative
        /// step: the elbow eases into the limit instead of hitting a wall, which is the difference between a
        /// guard and a pop.
        /// </summary>
        public const float HardMarginFracLimb = 0.15f;

        /// <summary>
        /// The corrective swivel, in radians, about the shoulder->hand axis, that brings the elbow back inside
        /// the anatomical envelope. Returns exactly 0f when the elbow is already legal.
        ///
        /// `totalLen` is the whole arm (upper + lower): the margins are fractions of it, so the guard is
        /// SCALE-FREE -- a child avatar and a giant get the same posture, not the same centimetres.
        /// </summary>
        public static float GuardSwivelRad(Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 playerUp, float totalLen)
        {
            Vector3 ac = hand - shoulder;
            float acSqr = ac.sqrMagnitude;
            // Reject-unless-good throughout: NaN fails every ordered comparison, so `!(x > eps)` catches it
            // where `x < eps` would wave it straight through into the bone -- and a NaN transform PERSISTS in
            // Unity, so the arm would never recover even once good data returned.
            if (!(acSqr > k_SqrEpsilon) || !(totalLen > k_Epsilon))
            {
                return 0f;
            }

            Vector3 up = playerUp;
            float upSqr = up.sqrMagnitude;
            if (!(upSqr > k_SqrEpsilon))
            {
                return 0f;
            }
            up /= Mathf.Sqrt(upSqr);

            Vector3 acN = ac / Mathf.Sqrt(acSqr);

            Vector3 ae = elbow - shoulder;
            Vector3 aeProj = ae - acN * Vector3.Dot(ae, acN);
            float radius = aeProj.magnitude;
            // The arm is straight: the elbow's circle has collapsed to a point, so there is no swivel that
            // could move it anywhere. Nothing to guard, and nothing that could go wrong.
            if (!(radius > k_Epsilon))
            {
                return 0f;
            }

            // The circle's plane is perpendicular to the arm axis. If the arm axis is VERTICAL, that plane is
            // horizontal: every point on the circle sits at the same height and the constraint cannot be
            // expressed as a swivel. (Physically: the arm is straight up or straight down, and swinging the
            // elbow around it does not raise or lower the elbow at all.)
            Vector3 upProj = up - acN * Vector3.Dot(up, acN);
            float upLen = upProj.magnitude;
            if (!(upLen > k_Epsilon))
            {
                return 0f;
            }

            Vector3 upN = upProj / upLen;
            Vector3 w = Vector3.Cross(acN, upN);   // the other in-plane basis vector; (upN, w) span the circle

            // THE CEILING: the higher of the shoulder (0, by construction -- everything is measured from it)
            // and the hand. This is the measured law.
            float handUp = Vector3.Dot(ac, up);
            float ceiling = handUp > 0f ? handUp : 0f;

            float hSoft = ceiling + SoftMarginFracLimb * totalLen;
            float hHard = ceiling + HardMarginFracLimb * totalLen;

            float h = Vector3.Dot(ae, up);   // the elbow's height above the shoulder, right now

            // Inside the envelope: the IDENTITY, exactly. No normalisation, no float drift, bit for bit the
            // unguarded solve. A guard that perturbs legal poses to fix illegal ones is the wrong trade.
            if (!(h > hSoft))
            {
                return 0f;
            }

            float M = hHard - hSoft;
            if (!(M > k_Epsilon))
            {
                return 0f;   // degenerate margins: decline rather than divide by ~0
            }

            // out = soft + M*e/(M + e).  Slope exactly 1 where it takes over, asymptotic to hard.
            float e = h - hSoft;
            float hGuarded = hSoft + M * e / (M + e);

            // Back-solve the pole. The elbow's height is AFFINE in the pole's up-component:
            //     h = dot(ae, acN)*dot(acN, up)  +  radius * upLen * c        where c = dot(poleDir, upN)
            float along = Vector3.Dot(ae, acN) * Vector3.Dot(acN, up);
            float denom = radius * upLen;
            float cG = (hGuarded - along) / denom;
            cG = cG > 1f ? 1f : (cG > -1f ? cG : -1f);   // this shape clamps NaN to -1 (Mathf.Clamp does not)

            Vector3 poleDir = aeProj / radius;
            float s = Vector3.Dot(poleDir, w);
            // Stay on the SAME SIDE of the circle. Crossing to the other side would be a 180 deg elbow swing
            // -- a pop -- when all that was asked for was "come down a bit".
            float sG = (s < 0f ? -1f : 1f) * Mathf.Sqrt(Mathf.Max(1f - cG * cG, 0f));

            Vector3 poleGuarded = upN * cG + w * sG;
            return SignedAngleRad(poleDir, poleGuarded, acN);
        }

        /// <summary>Signed angle from `from` to `to` about `axis`. Both inputs lie in the plane perpendicular
        /// to `axis`, so this is exact. Written the long way because it runs inside a Burst job.</summary>
        static float SignedAngleRad(Vector3 from, Vector3 to, Vector3 axis)
        {
            float denom = Mathf.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (!(denom > k_Epsilon))
            {
                return 0f;
            }

            float c = Vector3.Dot(from, to) / denom;
            c = c > 1f ? 1f : (c > -1f ? c : -1f);   // NaN -> -1, deliberately
            float angle = Mathf.Acos(c);
            return Vector3.Dot(axis, Vector3.Cross(from, to)) < 0f ? -angle : angle;
        }
    }
}
