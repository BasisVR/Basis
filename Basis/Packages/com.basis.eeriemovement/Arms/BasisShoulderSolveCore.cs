using UnityEngine;
namespace Basis.IK
{
    public struct BasisShoulderSolveInput
    {
        public Vector3 ShoulderPos;
        public Vector3 HandTargetPos;
        public Vector3 ElbowPos;            // lower-arm hint / elbow tracker world position
        public bool HasElbow;              // elbow tracker present -> drive from the true upper-arm direction
        public bool HasShoulderTracker;    // real shoulder tracker present -> TrackerFinal is the base
        public Quaternion ChestRot;        // live chest world rotation (the girdle frame)
        public Quaternion TposeChestRot;   // rest chest world rotation
        public Quaternion TposeShoulderRot;// rest shoulder world rotation
        public Vector3 TposeArmDirWorld;   // rest upper-arm direction (shoulder->upperArm) at bind, world space
        public float TposeArmLength;       // rest shoulder->hand distance (reach normaliser)
        public float TposeClavicleLength;  // rest shoulder->upperArm distance; feeds the shrug's expected-reach geometry
        public float TposeElbowLength;     // rest shoulder->lowerArm distance; 0 (the default) disables the elbow-driven shrug
        public bool ShrugEnabled;          // the user's body-tracking toggle; false (the default) disables the shrug outright
        public float ElevationFactor;      // per-axis trim on the lift component of the coupled swing
        public float ProtractionFactor;    // per-axis trim on the horizontal (protraction / cross-body) component
        public float CoupleRatio;          // scapulohumeral coupling: girdle share of the humeral swing
        public float MaxShoulderDeg;       // clamp on the applied girdle rotation
        public Quaternion TrackerFinal;    // real shoulder tracker rotation
        public bool IsLeft;
    }

    public struct BasisShoulderSolveResult
    {
        public bool Apply;
        public Quaternion ShoulderRotation;

        public float RawReachRatio;
        public float ReachRatio;
        public float Elevation;
        public float Protraction;
        public float CrossBodyContrib;
        public float ComputedWeight;

        // Anatomical diagnostics (chest-frame).
        public float SwingAngleDeg;     // total humeral swing from rest
        public float AppliedAngleDeg;   // girdle rotation actually applied
        public float TwistLeakDeg;      // girdle rotation about the arm axis (must stay ~0)
        public bool DriverIsElbow;
        public float ShrugDeg;          // girdle elevation mined from the hanging hand's reach deficit (0 = not engaged)
    }

    // Scapulohumeral-rhythm shoulder pre-solve. The shoulder girdle (clavicle/scapula bone) follows a
    // coupled fraction of the upper-arm SWING away from rest, measured in the chest frame, with the
    // humeral axial twist removed -- a twist-following clavicle was tried and reverted. The upper-arm
    // direction comes from the elbow tracker when present (shoulder->elbow, flexion-independent), else
    // from the hand target. Stream-free so the offline sweep + NUnit gates exercise the exact math.
    public static class BasisShoulderSolveCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_ReachEngageThreshold = 0.7f;   // hand-fallback: shoulder stays at rest below this reach
        const float k_SetStartDeg = 8f;              // setting phase: girdle barely moves below this swing
        const float k_SetFullDeg = 95f;
        const float k_TrackerRefine = 0.35f;         // with a shoulder tracker, only a gentle anatomical nudge (tracker stays dominant)
        const float k_DepressionShare = 0.25f;       // lowering below bind moves the girdle far less than raising
        const float k_DepressionBand = 0.12f;        // smooth raise/lower crossover, in chest-up units

        // ── SHRUG, mined from the hands (or better, the elbows) ─────────────────────────────────────
        // Hanging at the side, a real shrug lifts the whole girdle: the controller RISES while the arm's
        // DIRECTION barely changes -- zero humeral swing, which is the one quantity everything above is
        // driven by, so the shoulder never moved. What survives is the REACH DEFICIT: the driver (hand, or
        // the elbow tracker when present) ends up measurably closer to the chest-anchored clavicle than a
        // straight hanging arm can reach. That deficit is the shrug, in metres.
        //
        // The expected reach is exact, not the T-pose scalar: clav->driver for a straight arm at the
        // current direction is sqrt(c^2 + seg^2 + 2*c*seg*cos), cos = dot(rest clavicle dir, arm dir).
        // Using the T-pose length raw would read a permanent ~18% deficit on every hanging arm, because
        // T-pose is the one pose where clavicle and arm are colinear.
        //
        // All windows are FRACTIONS OF TposeArmLength, so the gesture is avatar-scale-free and the hand
        // and elbow paths agree about centimetres. The HAND path fades back out at large deficits -- a
        // hand on a hip or a lap is an elbow BEND, not a shrug. The ELBOW path does not fade: an upper
        // arm cannot bend itself, so with a tracker the deficit can only BE girdle motion.
        const float k_ShrugHangStart = 0.75f;        // smoothstep on how DOWN the arm points (chest frame)
        const float k_ShrugHangFull = 0.92f;
        const float k_ShrugRiseStartFrac = 0.02f;    // deficit where the shrug starts; relaxed micro-flex stays silent
        const float k_ShrugRiseFullFrac = 0.065f;    // a committed shrug (~4.5 cm on a 0.7 m reach)
        const float k_ShrugBendFadeStartFrac = 0.09f;// hand path only: past here it is an elbow bend...
        const float k_ShrugBendFadeEndFrac = 0.14f;  // ...and the shrug lets go entirely
        const float k_ShrugMaxDeg = 44f;             // full-shrug girdle elevation, before sliders and the max clamp.
                                                     // 22 read at half strength in-headset (the Elevation slider
                                                     // defaults to 0.4); doubled on that verdict, 2026-07-16.
                                                     // NOTE: past an Elevation slider of ~0.57 the live 25 deg
                                                     // girdle clamp becomes the ceiling, not this.

        public static void Solve(in BasisShoulderSolveInput i, out BasisShoulderSolveResult r)
        {
            r = default;

            float armLen = Mathf.Max(i.TposeArmLength, k_Epsilon);
            Vector3 driver = i.HasElbow ? i.ElbowPos : i.HandTargetPos;
            Vector3 armVec = driver - i.ShoulderPos;
            Vector3 handVec = i.HandTargetPos - i.ShoulderPos;
            if (armVec.sqrMagnitude < k_Epsilon || i.TposeArmDirWorld.sqrMagnitude < k_Epsilon)
            {
                r.Apply = false;
                return;
            }

            Quaternion invChest = Quaternion.Inverse(i.ChestRot);
            Vector3 restDirL = (Quaternion.Inverse(i.TposeChestRot) * i.TposeArmDirWorld).normalized;
            Vector3 armDirL = (invChest * armVec).normalized;

            // Twist-free by construction: FromToRotation's axis is perpendicular to the arm, so no roll.
            Quaternion swing = BasisQuaternionExt.FromToRotation(restDirL, armDirL);
            Vector3 rv = QuatToRotationVector(swing);   // chest-local rotation vector (rad), the humeral swing
            float swingDeg = rv.magnitude * Mathf.Rad2Deg;

            float rawReach = handVec.magnitude / (armLen * 1.1f);
            float reachFade = i.HasElbow ? 1f : ReachEngage(Mathf.Clamp01(rawReach));
            float setting = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_SetStartDeg, k_SetFullDeg, swingDeg));
            float engage = setting * reachFade;

            float couple = i.CoupleRatio * engage;
            // Chest-local axes: X=lateral, Y=up, Z=forward. Raising the arm swings about X/Z (elevation);
            // moving it across the front swings about Y (protraction / cross-body). Lowering the arm below
            // bind moves the girdle far less than raising it (scapular depression has a small range), which
            // also keeps idle / arms-down poses near the authored bind shoulder.
            float raise = armDirL.y - restDirL.y;
            float elevGain = Mathf.Lerp(k_DepressionShare, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-k_DepressionBand, k_DepressionBand, raise)));
            Vector3 girdleRv = new Vector3(rv.x * i.ElevationFactor * elevGain, rv.y * i.ProtractionFactor, rv.z * i.ElevationFactor * elevGain) * couple;

            // The shrug (see the constants block). Deliberately NOT scaled by `engage`: a shrug has zero
            // humeral swing, so the swing-based setting gate would erase exactly the gesture this exists
            // to detect. It carries its own gates instead, and joins the girdle before the twist-strip and
            // the max clamp so those bounds still own the outcome. Chest-local +Z lifts a +X (right)
            // clavicle tip; the left clavicle points -X, so its lift is -Z.
            float shrugRad = 0f;
            float bindLen = i.HasElbow ? i.TposeElbowLength : i.TposeArmLength;
            if (i.ShrugEnabled && bindLen > k_Epsilon)
            {
                float clav = Mathf.Clamp(i.TposeClavicleLength, 0f, bindLen * 0.45f);
                float seg = Mathf.Max(bindLen - clav, k_Epsilon);

                // Gate on the ARM SEGMENT's direction, not the clav->driver composite: subtracting the rest
                // clavicle tip removes the lateral offset that tilts the composite as the driver rises --
                // worst on the short elbow segment, where a real 5 cm shrug would otherwise read half-closed.
                // A shrug keeps the segment dead vertical at any rise, so the gate stays open for exactly
                // the gesture and still closes on a genuine swing.
                Vector3 segVecL = armDirL * armVec.magnitude - restDirL * clav;
                float hangDot = segVecL.sqrMagnitude > k_Epsilon ? -segVecL.normalized.y : 0f;
                if (hangDot > k_ShrugHangStart)
                {
                    float hang = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_ShrugHangStart, k_ShrugHangFull, hangDot));

                    // The expected reach for the OBSERVED direction is a ray-sphere intersection, not the
                    // law of cosines: the observed direction already CONTAINS the clavicle offset, so
                    // treating it as the arm's own direction over-expects by ~5% of the reach on a hanging
                    // arm -- a permanent false shrug at idle. Cast clav->driver along what we saw, against
                    // the sphere of straight-arm reaches around the rest clavicle tip; clav < seg (the
                    // 0.45 clamp), so the root term is positive structurally.
                    float cr = Vector3.Dot(restDirL, armDirL);
                    float expected = clav * cr + Mathf.Sqrt(clav * clav * (cr * cr - 1f) + seg * seg);
                    float deficitFrac = (expected - armVec.magnitude) / armLen;

                    float riseIn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_ShrugRiseStartFrac, k_ShrugRiseFullFrac, deficitFrac));
                    float bendOut = i.HasElbow ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_ShrugBendFadeStartFrac, k_ShrugBendFadeEndFrac, deficitFrac));
                    shrugRad = k_ShrugMaxDeg * Mathf.Deg2Rad * hang * riseIn * (1f - bendOut) * Mathf.Max(i.ElevationFactor, 0f);
                    girdleRv.z += i.IsLeft ? -shrugRad : shrugRad;
                }
            }

            // Enforce twist-free exactly: strip any girdle rotation about the arm axis the anisotropic
            // per-axis scaling introduced. The clavicle swings the arm root, it does not roll with it.
            girdleRv -= Vector3.Dot(girdleRv, armDirL) * armDirL;

            float maxRad = Mathf.Max(i.MaxShoulderDeg, 0f) * Mathf.Deg2Rad;
            float mag = girdleRv.magnitude;
            if (maxRad > 0f && mag > maxRad && mag > k_Epsilon)
            {
                girdleRv *= maxRad / mag;
            }

            Quaternion girdleL = BasisQuaternionExt.NormalizeSafe(RotationVectorToQuat(girdleRv));

            // shoulder world = live chest  *  girdle swing  *  rest shoulder-in-chest.
            Quaternion shoulderRestLocal = Quaternion.Inverse(i.TposeChestRot) * i.TposeShoulderRot;
            Quaternion anatomical = i.ChestRot * girdleL * shoulderRestLocal;

            Quaternion result;
            if (i.HasShoulderTracker)
            {
                Quaternion deltaWorld = i.ChestRot * girdleL * invChest;
                result = Quaternion.Slerp(i.TrackerFinal, deltaWorld * i.TrackerFinal, k_TrackerRefine * engage);
            }
            else
            {
                result = anatomical;
            }

            float elevRad = Mathf.Sqrt(girdleRv.x * girdleRv.x + girdleRv.z * girdleRv.z);
            float twistLeak = girdleRv.sqrMagnitude > k_Epsilon ? Vector3.Dot(girdleRv, armDirL) : 0f;

            r.Apply = true;
            r.ShoulderRotation = result;
            r.RawReachRatio = rawReach;
            r.ReachRatio = reachFade;
            r.Elevation = elevRad * Mathf.Rad2Deg;
            r.Protraction = Mathf.Abs(girdleRv.y) * Mathf.Rad2Deg;
            r.CrossBodyContrib = CrossBody(armDirL, restDirL, i.IsLeft) * Mathf.Abs(girdleRv.y) * Mathf.Rad2Deg;
            r.ComputedWeight = engage;
            r.SwingAngleDeg = swingDeg;
            r.AppliedAngleDeg = Quaternion.Angle(Quaternion.identity, girdleL);
            r.TwistLeakDeg = Mathf.Abs(twistLeak) * Mathf.Rad2Deg;
            r.DriverIsElbow = i.HasElbow;
            r.ShrugDeg = shrugRad * Mathf.Rad2Deg;
        }

        // Hand-fallback reach gate: keep the shoulder at rest until the arm genuinely extends, so a
        // folded arm near the body (hand close, no elbow tracker) doesn't drag the shoulder around.
        static float ReachEngage(float reach)
        {
            if (reach < k_ReachEngageThreshold)
            {
                return 0f;
            }
            float t = (reach - k_ReachEngageThreshold) / (1f - k_ReachEngageThreshold);
            return t * t;
        }

        // Signed cross-body amount of the current arm direction past the rest direction, in [0,1].
        static float CrossBody(Vector3 armDirL, Vector3 restDirL, bool isLeft)
        {
            float side = isLeft ? -armDirL.x : armDirL.x;   // arm crossed to the far side of the chest
            return Mathf.Clamp01(-side);
        }

        static Quaternion RotationVectorToQuat(Vector3 rv)
        {
            float ang = rv.magnitude;
            if (ang < k_Epsilon)
            {
                return Quaternion.identity;
            }
            return Quaternion.AngleAxis(ang * Mathf.Rad2Deg, rv / ang);
        }

        static Vector3 QuatToRotationVector(Quaternion q)
        {
            q.ToAngleAxis(out float deg, out Vector3 axis);
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x) || axis.sqrMagnitude < k_Epsilon)
            {
                return Vector3.zero;
            }
            if (deg > 180f)
            {
                deg -= 360f;
            }
            return axis.normalized * (deg * Mathf.Deg2Rad);
        }
    }
}
