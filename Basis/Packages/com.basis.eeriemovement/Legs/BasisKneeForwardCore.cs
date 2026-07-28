using UnityEngine;
namespace Basis.IK
{
    // Inputs for one leg's knee-forward (azimuth) solve. World space unless noted.
    public struct BasisKneeForwardInput
    {
        public Vector3 HipPosition;     // leg root (upper-leg joint = two-bone Root)
        public Vector3 FootPosition;    // tracked ankle/foot position
        public Vector3 FootForwardDir;  // tracked foot TOE direction: FootRotation * Vector3.forward
        public Vector3 BodyForwardDir;  // hips forward (belly): the sagittal default the knee bulges toward
        public Vector3 PlayerUp;        // world / playspace up: gates the follow off as the leg goes horizontal (lying)
        public float UpperLength;       // thigh length (hint radius)
        public float Coupling;          // 0..1 how far the UPRIGHT knee azimuth follows the toe vs body-forward
        public float Strength;          // external enable / scale 0..1
    }

    public struct BasisKneeForwardResult
    {
        public Vector3 KneeHint;    // world pole position for the leg solver's knee hint
        public Vector3 BendDir;     // unit sagittal bend dir (perp to hip->foot axis) -- also feeds butterfly's default bend
        public float HintWeight;    // 0..1
        public float Upright01;     // leg verticality gate: 1 = leg upright (standing, follow on), 0 = leg horizontal (lying, follow off)
        public float FollowDeg;     // knee azimuth swing off body-forward toward the toe (diagnostic)
    }

    // Which horizontal direction the knee POINTS (its sagittal bend azimuth) for a user with FOOT trackers but no
    // KNEE tracker. The two-bone solver treats the knee hint as a pure POLE DIRECTION -- it projects (hint - hip)
    // perpendicular to the hip->ankle axis and normalises, discarding distance -- so this core's whole job is to
    // choose that azimuth. The default the solve falls back to is the pelvis' sagittal plane (BendNormal =
    // hips-right => knee bulges body-forward). That ignores where the FOOT points: turn a tracked foot out and the
    // knee stays aimed straight ahead. This makes the knee FOLLOW THE FOOT, damped by posture.
    //
    // ================================================================================================
    // STANDING AND LAYING ARE DIFFERENT REGIMES, AND REAL HUMAN DATA SAYS SO.
    //
    //  * STANDING (weight-bearing, upright): the body actively regulates the tibia's fore-aft axis to sit ~perpendicular
    //    to the pelvis -- knee points body-forward REGARDLESS of femoral anteversion (~11 deg) or external tibial
    //    torsion (~20 deg). Measured tibial AP axis vs the anterior pelvic plane: 1.65 +/- 5.58 deg (Kobayashi 2018,
    //    J Orthop Surg Res / PMC5930825). But that number is a RELAXED, NEUTRAL stance, where the ~5-7 deg toe-out
    //    (foot progression angle; Chen 2016 / PMC4886808) is mostly DISTAL (tibial torsion + subtalar). When a VR
    //    user DELIBERATELY points a tracked foot, that yaw comes from HIP external rotation -- proximal -- which
    //    swings the femur and therefore the knee with it. So the follow is strong (default 0.75); the knee tracks
    //    most of the way to the foot and only stays a little inboard.
    //
    //  * SUPINE / RECLINING (relaxed, non-weight-bearing): the whole LEG goes horizontal, and the toe azimuth stops
    //    being the right control -- a supine foot points up or rolls out every which way, and at 0.75 authority that
    //    would yank the knee sideways. So the follow is gated on LEG POSTURE: Upright01 = smoothstep(|dot(hip->ankle
    //    axis, playerUp)|) -- 1 when the leg is vertical (standing), 0 once it is near horizontal (lying) -- and the
    //    follow fades out with it. Supine, the knee's opening is then owned by the foot's INSTEP roll
    //    (BasisButterflyKneeCore), not this toe azimuth. (An earlier version gated on the toe-vs-leg-axis angle
    //    instead; that fails, because a supine toe pointing up/out is PERPENDICULAR to the flat leg, so the gate
    //    stayed wide open and the knee got thrown around. Gate on the leg, not the toe.)
    //
    // Foot-progression / tibial-torsion "knee is a few degrees inboard of the foot" falls out for free: Coupling < 1
    // keeps BendDir between body-forward and the toe, so the knee is always LESS toed-out than the foot, in both
    // regimes, with no explicit torsion term.
    // ================================================================================================
    //
    // Pure + stream-free for edit-mode sweeps (BasisKneeForwardTests). Live wiring: BasisLocalRigDriver's
    // foot-tracked / no-knee-tracker branch feeds BendDir as BasisButterflyKneeCore's DefaultBendDir (so the lateral
    // splay opens from the foot-corrected azimuth) and, when butterfly is not engaged, emits KneeHint/HintWeight on
    // the existing PositionLeftLowerLeg / EnableLeftLowerLeg channel. Sibling of BasisButterflyKneeCore.
    public static class BasisKneeForwardCore
    {
        // How far the knee follows the foot's toe with no knee tracker. FULL follow by default: a deliberate foot
        // yaw is proximal (hip external rotation), which swings the femur and therefore the knee with it, so the
        // knee tracks the toe the whole way. Tunable (settings slider, 0.1-1); the whole feature is gated off by
        // Strength <= 0.
        //
        // ⚠️ 1.0 GIVES UP THE FREE "KNEE SITS INBOARD OF THE FOOT" TERM. Below 1 the knee lands between
        // body-forward and the toe, which reproduces foot-progression / tibial torsion (a real knee is a few
        // degrees inboard of the foot) with no explicit model. At 1.0 the knee points exactly where the toe does,
        // which is what a user asking for maximum foot authority is asking for -- but it is the reason this is a
        // slider and not a constant. Drop toward 0.85 to get the inboard bias back.
        public const float DefaultUprightCoupling = 1.0f;

        // Where the follow starts fading out as the toe swings toward pointing back down the body. Below this the
        // fade is the exact identity, so every ordinary foot angle is untouched. See the block in Solve.
        public const float FollowFadeStartDeg = 120f;

        // A safety cap on the azimuth swing off body-forward. The knee should never chase a wildly-yawed foot past the
        // hip's own external-rotation range; the leg solve's anterior half-space guard is the hard limit, this is the
        // soft one. ~60 deg ~ the far edge of hip external rotation.
        public const float MaxFollowDeg = 60f;

        // Leg-posture gate on |dot(hip->ankle axis, playerUp)|. Full follow while the leg is upright enough to be
        // weight-bearing (standing / walking / sitting / crouching); faded OFF once it is near horizontal (lying),
        // where the butterfly instep-splay owns the knee instead. FadeStart is ~75 deg off vertical, FadeFull ~57 deg.
        public const float LegUprightFadeStartDot = 0.25f;
        public const float LegUprightFadeFullDot = 0.55f;

        public const float RefCondSinFadeStart = 0.15f;
        public const float RefCondSinFadeFull = 0.35f;

        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-10f;

        public static void Solve(in BasisKneeForwardInput i, out BasisKneeForwardResult r)
        {
            r = default;

            Vector3 hipToFoot = i.FootPosition - i.HipPosition;
            float axisSqr = hipToFoot.sqrMagnitude;
            float radius = i.UpperLength > k_Epsilon ? i.UpperLength : 0.4f;
            Vector3 mid = (i.HipPosition + i.FootPosition) * 0.5f;

            // Degenerate leg (foot ~ hip) or no body reference: emit nothing, the solver keeps its own bend pole.
            if (axisSqr < k_SqrEpsilon)
            {
                r.BendDir = i.BodyForwardDir.sqrMagnitude > k_SqrEpsilon ? i.BodyForwardDir.normalized : Vector3.forward;
                r.KneeHint = mid + r.BendDir * radius;
                r.HintWeight = 0f;
                return;
            }
            Vector3 axis = hipToFoot / Mathf.Sqrt(axisSqr);
            Vector3 up = i.PlayerUp.sqrMagnitude > k_SqrEpsilon ? i.PlayerUp.normalized : Vector3.up;

            Vector3 bodyPerp = Vector3.ProjectOnPlane(i.BodyForwardDir, axis);
            if (bodyPerp.sqrMagnitude < k_SqrEpsilon)
            {
                // Body-forward runs along the leg (rare): no sagittal reference to steer from.
                Vector3 fallback = Vector3.ProjectOnPlane(up, axis);
                if (fallback.sqrMagnitude < k_SqrEpsilon)
                {
                    fallback = Vector3.Cross(axis, Vector3.right);
                }
                if (fallback.sqrMagnitude < k_SqrEpsilon)
                {
                    fallback = Vector3.Cross(axis, Vector3.up);
                }
                r.BendDir = fallback.normalized;
                r.KneeHint = mid + r.BendDir * radius;
                r.HintWeight = 0f;
                return;
            }
            Vector3 bodyPerpN = bodyPerp.normalized;
            float fwdMag = i.BodyForwardDir.magnitude;
            float refConditioning = Smoothstep(RefCondSinFadeStart, RefCondSinFadeFull,
                fwdMag > k_Epsilon ? Mathf.Sqrt(bodyPerp.sqrMagnitude) / fwdMag : 0f);

            // Leg-posture gate: 1 while the leg is upright (standing), fading to 0 as it goes horizontal (lying), so a
            // supine foot -- which points up/out, PERPENDICULAR to the flat leg -- can no longer drive the azimuth.
            float legVertical01 = Smoothstep(LegUprightFadeStartDot, LegUprightFadeFullDot, Mathf.Abs(Vector3.Dot(axis, up)));
            r.Upright01 = legVertical01;

            float strength = Saturate(i.Strength);

            Vector3 footPerp = Vector3.ProjectOnPlane(i.FootForwardDir, axis);
            Vector3 bendDir;
            float followDeg;
            if (footPerp.sqrMagnitude < k_SqrEpsilon || legVertical01 <= 0f)
            {
                // Toe aligned with the leg (no azimuth), or the leg is too reclined to steer: hold the sagittal
                // default. This is the supine hand-off to the butterfly splay.
                bendDir = bodyPerpN;
                followDeg = 0f;
            }
            else
            {
                Vector3 footPerpN = footPerp.normalized;

                // ⭐ NAME THE AXIS. bodyPerpN and footPerpN are BOTH perpendicular to `axis` by construction, so
                // the rotation between them IS about `axis`, exactly. Vector3.RotateTowards instead DISCOVERS its
                // axis from Cross(from, to) -- and that vanishes when the toe points opposite body-forward, where
                // Unity then falls back to an arbitrary one. Measured on this core, sweeping the toe around the
                // leg: 480 deg of bend direction per degree of toe motion at a toe azimuth of 180, at EVERY
                // coupling. A body turn over a planted foot reaches that every time. Same fix, and the same
                // reason, as the swivel axis in BasisLegSolveCore: the axis is known, so do not go looking for it.
                float signedDeg = Vector3.SignedAngle(bodyPerpN, footPerpN, axis);
                float rawAngle = signedDeg < 0f ? -signedDeg : signedDeg;

                followDeg = Mathf.Min(Saturate(i.Coupling) * legVertical01 * refConditioning * rawAngle, MaxFollowDeg);

                // ⭐ CLOSE THE CIRCLE. Naming the axis fixes WHICH WAY the rotation goes but not the cap: clamping
                // to MaxFollowDeg holds the follow at full magnitude right up to the posterior ray, so it flips
                // sign the instant the toe crosses it -- +60 to -60, a 120 deg knee swing for a fraction of a
                // degree of foot motion.
                //
                // The honest reading of a toe pointing back down the body is that it is NOT steering the knee: it
                // is far past anything a hip can follow (external rotation tops out ~45-60 deg), and at exactly
                // 180 the side it should pick is genuinely undefined -- mirror symmetry means any continuous
                // answer there has to be zero. So fade the follow out over the last stretch and let the sagittal
                // default take back over. Below FollowFadeStartDeg this is the EXACT identity, so every ordinary
                // foot angle -- and the entire range a hip can actually follow -- is untouched.
                if (rawAngle > FollowFadeStartDeg)
                {
                    float u = (rawAngle - FollowFadeStartDeg) / (180f - FollowFadeStartDeg);
                    if (u > 1f) u = 1f;
                    followDeg *= 1f - u * u * (3f - 2f * u);
                }

                bendDir = Quaternion.AngleAxis(signedDeg < 0f ? -followDeg : followDeg, axis) * bodyPerpN;
                bendDir = Vector3.ProjectOnPlane(bendDir, axis);
                bendDir = bendDir.sqrMagnitude > k_SqrEpsilon ? bendDir.normalized : bodyPerpN;
            }

            r.BendDir = bendDir;
            r.FollowDeg = followDeg;
            r.KneeHint = mid + bendDir * radius;
            r.HintWeight = strength * refConditioning;
        }

        static float Saturate(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        static float Smoothstep(float a, float b, float v)
        {
            float t = Mathf.Approximately(a, b) ? (v >= b ? 1f : 0f) : Saturate((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
