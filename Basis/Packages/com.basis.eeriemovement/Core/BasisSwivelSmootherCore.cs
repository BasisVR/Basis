using UnityEngine;
namespace Basis.IK
{
    // Stream-free swivel smoother shared by BasisFullIKConstraintJob.SmoothElbowSwivel and SmoothKneeSwivel.
    // Measures the mid joint's roll about the root->tip axis, One-Euro low-passes it (BasisSwivelFilterCore),
    // and rebuilds the mid position on its circle at the smoothed angle. The caller swings the limb onto
    // DesiredMid and restores the tip, so the hand/foot stays exactly on target.
    //
    // The reference direction is expressed in the BODY frame (BodyRotation, the solved hips), never in world.
    // A swivel measured against a world axis is not invariant under a body turn: the pole co-rotates with the
    // player but a world reference does not, so a yaw registers as swivel CHANGE, the One-Euro lags it, and
    // the smoother then drags the limb toward a stale pole. Co-rotating the reference cancels bulk body
    // motion out of the measurement, which is what makes "damp jitter, don't lag a turn" actually hold. It
    // also cancels hips jitter, which the pole would otherwise inherit through the parent chain.
    //
    // No valid body frame => no smoothing. Fabricating one from a bare up-vector would put an arbitrary yaw
    // back into the reference, which is the defect this core exists to remove.
    public struct BasisSwivelSmootherInput
    {
        public Vector3 Root;             // shoulder / hip
        public Vector3 Mid;              // elbow / knee
        public Vector3 Tip;              // hand / foot
        public Quaternion BodyRotation;  // solved hips rotation; the frame the swivel is measured in
        public Vector3 ReferenceLocal;   // body-local reference dir (arm: down, leg: forward)
        public Vector3 FallbackLocal;    // body-local fallback when ReferenceLocal is colinear with the axis; zero = none

        // ⭐ Body-local axis to PARALLEL-TRANSPORT ReferenceLocal from, instead of PROJECTING it onto the
        // limb's swing plane. Zero (the struct default) keeps the legacy projection, so the elbow path and
        // every existing test are untouched. The leg passes body-DOWN. See the block in Solve.
        public Vector3 TransportHomeLocal;
        public float Dt;
        public float MinCutoffHz;
        public float Beta;
        public float DerivCutoffHz;
        public BasisSwivelFilterState State;
        public bool Seeded;

        // Scale the One-Euro's responsiveness by how much the swivel measurement is actually WORTH this frame
        // (see Conditioning below). Opt-in: false reproduces the legacy filter exactly, so the elbow path is
        // untouched. The knee turns it on -- a standing leg lives ON the pole singularity, which is where the
        // legacy behaviour is not merely useless but actively harmful.
        public bool ConditionOnPole;

        // The min-cutoff to fall back to at ZERO conditioning, i.e. when the limb is dead straight and the
        // swivel carries no information at all. Lerped toward MinCutoffHz as conditioning recovers. Ignored
        // unless ConditionOnPole. Pass BasisSwivelFilterCore.MinCutoffHz for the heavy standing floor.
        public float SingularMinCutoffHz;

        // Forbid the mid joint from crossing BEHIND the root->tip axis (see BasisLegSolveCore.ClampKneeSwivelDeg).
        //
        // KNEE ONLY, and opt-in: false reproduces the legacy filter exactly, so the ELBOW path is untouched. This is
        // not squeamishness about regressions -- it is that the constraint does not TRANSFER. The reference here is
        // ReferenceLocal, and for the leg that is body-FORWARD, which is genuinely the direction a knee bulges. The
        // arm's reference is body-DOWN, and an elbow does not point "down"; it points BACK. Applying a
        // forward-anterior half-space to the elbow would clamp it to the wrong hemisphere entirely. The arm has the
        // same inversion problem and deserves the same guard -- but with its own reference, as its own change.
        public bool GuardAnteriorHalfSpace;
        public float AnteriorSoftDeg;   // ignored unless GuardAnteriorHalfSpace
        public float AnteriorHardDeg;   // ignored unless GuardAnteriorHalfSpace

        // SINGULARITY HOLD (opt-in; see the block in Solve). Freezes the smoothed swivel where the knee's lever
        // arm has collapsed and the angle is undetermined, so a slow body-frame sway can no longer roll the leg.
        // false reproduces the legacy filter exactly (elbow path + every existing test untouched). Distinct from
        // ConditionOnPole: that stays a LOW-PASS (and lagged real shin motion); this is a HOLD, gated on the SAME
        // conditioning but touching only the near-straight band the knee cannot point out of anyway.
        public bool HoldWhenSingular;
        public float HoldCondLo;   // conditioning at/below which the swivel is fully HELD (frozen). Ignored unless HoldWhenSingular.
        public float HoldCondHi;   // conditioning at/above which the filter is fully RELEASED. Ignored unless HoldWhenSingular.
    }

    public struct BasisSwivelSmootherResult
    {
        public bool Valid;        // false => degenerate this frame, caller must not move the bone
        public bool WriteState;   // true => store State back (also true on the seed frame, which does not move the bone)
        public bool Seeded;
        public BasisSwivelFilterState State;
        public Vector3 DesiredMid;
        public float RawSwivelDeg;
        public float SmoothSwivelDeg;

        // sin(angle between the upper bone and the root->tip axis), 0..1. How much the swivel angle is worth:
        // 0 = the mid joint sits ON the axis (pole singularity, bend plane undefined), 1 = limb fully folded.
        // Always reported, whether or not ConditionOnPole is set, so it can be measured and gated in tests.
        public float Conditioning;

        // True when the anterior half-space guard actually had to pull the joint back this frame -- i.e. the
        // incoming swivel was trying to put the knee behind the leg. Reported so tests can prove the guard FIRES on
        // the inverting input, not merely that the output happens to look fine.
        public bool AnteriorGuardApplied;

        // The singularity-hold gate this frame: 1 = fully released (legacy One-Euro), 0 = fully HELD (swivel
        // frozen). Always reported so tests can prove the hold engages near the singularity and lets go once bent.
        public float HoldGate;
    }

    public static class BasisSwivelSmootherCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        // Default singularity-hold band, in conditioning (= sin of the upper bone off the root->tip axis). The
        // knee, capped at BasisLegSolveCore.MaxKneeInteriorDeg (176 deg), is PINNED at conditioning ~= 0.035 while
        // standing -- footHeightOffset is clamped so the leg fully extends. So hold fully below 0.05 (covers the
        // pinned standing value across body types with margin) and release by 0.12 (~14 deg of knee bend, where
        // the lever arm is back and a real shin motion carries information). One source of truth; callers pass these.
        public const float DefaultHoldCondLo = 0.05f;
        public const float DefaultHoldCondHi = 0.12f;

        const float k_HoldReseedDeg = 25f;

        public static void Solve(in BasisSwivelSmootherInput i, out BasisSwivelSmootherResult r)
        {
            r = default;
            r.DesiredMid = i.Mid;
            r.State = i.State;
            r.Seeded = i.Seeded;

            if (i.Dt <= 1e-6f)
            {
                return;
            }

            Quaternion body = i.BodyRotation;
            if (body.x * body.x + body.y * body.y + body.z * body.z + body.w * body.w < 0.5f)
            {
                return;
            }

            Vector3 ac = i.Tip - i.Root;
            float acSqr = ac.sqrMagnitude;
            if (acSqr < k_SqrEpsilon)
            {
                return;
            }
            Vector3 axis = ac / Mathf.Sqrt(acSqr);

            // =========================================================================================
            // ⭐ THE REFERENCE IS TRANSPORTED, NOT PROJECTED.
            //
            // A projection onto the swing plane does not merely SHRINK as the reference goes colinear with
            // the limb axis -- it REVERSES through it. ProjectOnPlane(forward, axis) = forward - axis*dot,
            // and as the hip->ankle axis sweeps through body-forward that residual passes through zero and
            // comes out pointing the other way. The measured swivel therefore jumps a full 180 deg for an
            // arbitrarily small motion of the leg, and every downstream consumer -- the One-Euro, the
            // anterior guard, the 25 deg reseed -- inherits the discontinuity.
            //
            // AND THE LEG REACHES IT. hip->ankle lies along body-forward whenever the legs are straight out
            // in front: sitting on the floor, a front kick, lying supine with the legs up. Measured on the
            // shipped core, sweeping the leg through that direction moved the guarded output 89.3 deg in a
            // single 0.5 deg step. The existing `sqrMagnitude < 1e-8` fallback is ~4 orders of magnitude too
            // small to catch it -- |refDir| is already down at 0.009 one step away, and the damage is the
            // REVERSAL, which happens at full magnitude either side of the crossing.
            //
            // So do not project. Take the minimal rotation that carries the body's HOME axis (body-down for a
            // leg -- the direction the limb hangs in) onto the actual limb axis, and carry the reference along
            // it. The transported vector is perpendicular to the axis by construction, because the reference
            // is perpendicular to home and rotations preserve angles. There is no projection left to collapse.
            //
            // ⭐ EXACT NO-OP FOR EVERY SAGITTAL POSE. Whenever the limb axis lies in the plane spanned by home
            // and the reference -- standing, walking, squatting, sitting in a chair, sitting on the floor with
            // the legs out, kneeling, lunging -- the transport and the projection produce the SAME unit vector
            // to 0.00 deg, so none of the leg tuning moves.
            //
            // They DO differ for out-of-sagittal poses (measured: 13 deg on a butterfly/cobbler sit). That is
            // not a regression, and the reason is worth stating, because it is the opposite of what you would
            // assume: BasisLegSolveCore uses a THIRD convention for anterior -- Cross(acNorm, AnteriorNormal)
            // -- and that is the frame that actually PLACES the knee, so it is the one this smoother has to
            // agree with or it drags the knee off the solve's own pole. Measured against it across a pose set
            // (standing / walking / squat / chair / floor / kneel / lunge / butterfly / cross-legged / abducted
            // / side split / stairs / supine):
            //
            //      projected reference (shipping):  worst disagreement 180.00 deg
            //      transported reference (this):    worst disagreement  16.05 deg
            //
            // The transport is closer to the solver on every pose in the set and never further. The 180 is the
            // supine-legs-raised row, where the solve core says the knee is dead anterior and the PROJECTION
            // says it is dead posterior -- the reversal, seen from the other side. And no legal pose is pushed
            // past the guard's soft edge by the change: butterfly reads 39 deg where the free band ends at 85.
            //
            // Same doctrine as the torso-yaw swing-twist fix: pick a formulation whose singularity is outside
            // the reachable workspace rather than damping one that is inside it. The only singularity left is
            // axis == -home, the thigh pointing straight up out of the pelvis, which a hip cannot do; if it
            // somehow arrives, fall back to the projection rather than freezing the bone.
            // =========================================================================================
            Vector3 refDir = Vector3.zero;
            bool transported = false;
            if (i.TransportHomeLocal.sqrMagnitude > k_SqrEpsilon)
            {
                Vector3 home = body * i.TransportHomeLocal;
                float homeSqr = home.sqrMagnitude;
                if (homeSqr > k_SqrEpsilon)
                {
                    home /= Mathf.Sqrt(homeSqr);

                    // Minimal (swing) rotation home -> axis as an unnormalized quaternion: (cross, 1 + dot).
                    // Its w vanishes only when the two are antipodal, which is the unreachable pole above.
                    Vector3 swingXyz = Vector3.Cross(home, axis);
                    float swingW = 1f + Vector3.Dot(home, axis);
                    float swingSqr = swingXyz.sqrMagnitude + swingW * swingW;
                    if (swingW > 1e-4f && swingSqr > k_SqrEpsilon)
                    {
                        float inv = 1f / Mathf.Sqrt(swingSqr);
                        Quaternion swing = new Quaternion(swingXyz.x * inv, swingXyz.y * inv, swingXyz.z * inv, swingW * inv);
                        refDir = swing * (body * i.ReferenceLocal);
                        refDir -= axis * Vector3.Dot(refDir, axis);   // float tidy-up; exact in theory
                        transported = refDir.sqrMagnitude > k_SqrEpsilon;
                    }
                }
            }

            if (!transported)
            {
                refDir = Vector3.ProjectOnPlane(body * i.ReferenceLocal, axis);
                if (refDir.sqrMagnitude < k_SqrEpsilon && i.FallbackLocal.sqrMagnitude > k_SqrEpsilon)
                {
                    refDir = Vector3.ProjectOnPlane(body * i.FallbackLocal, axis);
                }
            }
            Vector3 upper = i.Mid - i.Root;
            Vector3 pole = Vector3.ProjectOnPlane(upper, axis);
            if (refDir.sqrMagnitude < k_SqrEpsilon || pole.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }
            refDir.Normalize();

            // How much is this swivel measurement actually worth?
            //
            // `pole` is the mid joint's PERPENDICULAR LEVER ARM from the root->tip axis, so
            //     |pole| / |upper| == sin(angle between the upper bone and that axis)
            // which is exactly the conditioning of the swivel: it goes to ZERO when the mid joint sits on the
            // axis -- the pole singularity, where the bend plane is undefined and SignedAngle below is reading
            // the direction of a vanishing vector, i.e. pure noise.
            //
            // The existing guard (pole.sqrMagnitude < 1e-8, so |pole| < 0.1 mm) only rejects the EXACT
            // singularity. Everything in the ill-conditioned band -- a millimetre to a few centimetres of lever
            // arm, which is precisely where a standing leg lives, because footHeightOffset is clamped so the
            // legs fully extend -- sails through and gets measured as if it were signal.
            float upperLen = upper.magnitude;
            float conditioning = upperLen > k_Epsilon ? Mathf.Clamp01(pole.magnitude / upperLen) : 0f;
            r.Conditioning = conditioning;

            float curSwivel = Vector3.SignedAngle(refDir, pole, axis);
            r.RawSwivelDeg = curSwivel;   // the TRUE measurement, unguarded -- diagnostics must not lie

            // ANTERIOR HALF-SPACE GUARD. The knee is REBUILT below at `refDir` rotated by the smoothed swivel, so
            // bounding that angle bounds where the knee can physically end up: the posterior half-space becomes
            // unreachable through this core, not merely unlikely.
            //
            // Guard the value going INTO the filter, not just the one coming out. A One-Euro is a low-pass, and a
            // low-pass of values inside [-hard, hard] is a convex blend of them and therefore stays inside -- so
            // clamping the input is what makes the bound hold structurally. Clamp only the output and the filter's
            // STATE still winds up posterior, then fights the clamp every frame and lags the release.
            //
            // Free side-effect worth knowing about: SignedAngle wraps at +-180, and this filter low-passes a LINEAR
            // angle, so an unguarded swivel sweeping past the back of the leg jumps 179 -> -179 and the filter
            // crawls the LONG way round through zero. Clamping the input to +-85 makes that wrap unreachable.
            float guardedSwivel = curSwivel;
            if (i.GuardAnteriorHalfSpace)
            {
                guardedSwivel = BasisLegSolveCore.ClampKneeSwivelDeg(curSwivel, i.AnteriorSoftDeg, i.AnteriorHardDeg);
                r.AnteriorGuardApplied = guardedSwivel != curSwivel;
            }

            if (!i.Seeded)
            {
                bool deferSeedWhileSingular = i.HoldWhenSingular && conditioning < i.HoldCondHi;
                r.State = BasisSwivelFilterCore.Seed(guardedSwivel);
                r.Seeded = !deferSeedWhileSingular;
                r.WriteState = !deferSeedWhileSingular;
                r.SmoothSwivelDeg = guardedSwivel;
                r.HoldGate = 1f;   // seeding passes the value straight through -- nothing is held on the first frame
                return;
            }

            float minCutoffHz = i.MinCutoffHz;
            float beta = i.Beta;
            if (i.ConditionOnPole)
            {
                // A One-Euro is SPEED-ADAPTIVE: `cutoff = minCutoff + beta * |velocity|`. Its whole premise is
                // "a fast-moving signal is a signal the user MEANT, so stop filtering and track it". At the pole
                // singularity that premise inverts. The measured swivel is noise thrashing through large angles,
                // so |velocity| is huge -- and the filter reads that as intent, throws the cutoff wide open, and
                // passes the garbage straight through. With beta 0.20 and a 500 deg/s noise velocity the cutoff
                // lands at ~101 Hz, i.e. alpha ~= 1: effectively no filtering, exactly when there is nothing but
                // noise to filter. **A speed-adaptive filter is actively harmful at a singularity** -- it
                // amplifies the very thing it was added to suppress.
                //
                // So scale the adaptivity by the conditioning:
                //   beta -> 0 as the limb straightens, so the cutoff STOPS opening on noise velocity;
                //   minCutoff falls back to the heavy standing floor, since there is nothing worth tracking.
                // Away from the singularity both return to their full values, so deliberate shin motion at a
                // genuinely bent knee is NOT lagged. This is principled rather than a fudge: an ill-conditioned
                // measurement should be trusted less, in exact proportion to how ill-conditioned it is.
                //
                // The snap the user feels is the payoff of this: while the leg is straight the filter chases
                // noise, so the swivel state is wherever the jitter last flung it -- and the moment the leg bends
                // and the lever arm grows, that accumulated garbage becomes VISIBLE and the knee whips to it.
                // Holding the swivel steady while straight means it is already sane when the bend arrives.
                beta *= conditioning;
                minCutoffHz = Mathf.Lerp(i.SingularMinCutoffHz, i.MinCutoffHz, conditioning);
            }

            BasisSwivelFilterState state = BasisSwivelFilterCore.Step(i.State, guardedSwivel, i.Dt, minCutoffHz, beta, i.DerivCutoffHz);

            // ⭐ SINGULARITY HOLD (opt-in). Near full extension the knee sits ON the pole singularity: its lever
            // arm off the hip->ankle axis collapses (conditioning -> 0), the swivel angle is undetermined, and any
            // residual body-frame micro-motion -- postural sway pivoting the leg over a planted foot, which is
            // NON-rigid so the body-frame reference does not cancel it -- maps to a large, SLOW thigh roll. A
            // One-Euro cannot remove that: a low-pass attenuates FAST jitter, but a ~0.3 Hz sway sails through a
            // 1-1.5 Hz floor almost untouched. The only thing with zero passband at EVERY frequency is a HOLD.
            //
            // So gate the filter's INNOVATION by the conditioning: freeze the swivel where it carries no
            // information, release it the instant the knee bends far enough to have a lever arm again (HoldCondHi).
            // Raw/Vel keep tracking (the release is seeded with a live velocity), only Smooth is frozen. This is
            // NOT the ConditionOnPole beta-strangle: that stays a low-pass and lagged genuine shin motion (the
            // "knee trackers too slow" bug). The hold touches ONLY the near-straight band where the knee cannot
            // point anywhere anyway (its circle has shrunk to a point), and is the exact identity above HoldCondHi,
            // so a genuinely bent, tracker-driven knee is byte-for-byte unchanged. Off by default => elbow path and
            // every existing test are untouched.
            float holdGate = 1f;
            if (i.HoldWhenSingular)
            {
                // The hold exists to reject a slow postural sway the low-pass cannot, so it must only ever be
                // asked to sit on a value that is already right. A held value can be WRONG -- seeded from a
                // degenerate frame during load, for instance -- and then the freeze is what prevents it from
                // ever recovering: the knee stays parked at the stale angle until something bends it out of
                // the band. Divergence that large is not the sway this is built for, so re-seed instead of
                // defending it. Well above any real sway, well below the 85 deg clamp a bad seed lands on.
                if (Mathf.Abs(Mathf.DeltaAngle(i.State.Smooth, guardedSwivel)) > k_HoldReseedDeg)
                {
                    state = BasisSwivelFilterCore.Seed(guardedSwivel);
                }
                else
                {
                    holdGate = Smoothstep(i.HoldCondLo, i.HoldCondHi, conditioning);
                    float innovation = Mathf.DeltaAngle(i.State.Smooth, state.Smooth);
                    state.Smooth = i.State.Smooth + innovation * holdGate;   // gate 0 => frozen, gate 1 => full One-Euro
                }
            }
            r.HoldGate = holdGate;
            r.State = state;
            r.Seeded = true;
            r.WriteState = true;

            // Belt and braces. Clamping the input already bounds this (a low-pass of bounded values is bounded), so
            // in a correct filter this is a no-op -- which is the point: the anatomical invariant is then guaranteed
            // by the ANGLE THAT BUILDS THE BONE, not by trusting an upstream filter to have stayed in range. Seeded
            // state restored from a previous frame, or any future change to the filter, cannot smuggle the knee
            // through the joint.
            float outSwivel = state.Smooth;
            if (i.GuardAnteriorHalfSpace)
            {
                outSwivel = BasisLegSolveCore.ClampKneeSwivelDeg(outSwivel, i.AnteriorSoftDeg, i.AnteriorHardDeg);
            }
            r.SmoothSwivelDeg = outSwivel;

            Vector3 center = i.Root + axis * Vector3.Dot(i.Mid - i.Root, axis);
            float radius = (i.Mid - center).magnitude;
            if (radius < k_Epsilon)
            {
                return;
            }

            r.DesiredMid = center + (Quaternion.AngleAxis(outSwivel, axis) * refDir) * radius;
            r.Valid = true;
        }

        // Hermite smoothstep, clamped. a==b degenerates to a step at a.
        static float Smoothstep(float a, float b, float v)
        {
            if (b <= a) return v >= b ? 1f : 0f;
            float t = (v - a) / (b - a);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return t * t * (3f - 2f * t);
        }
    }
}
