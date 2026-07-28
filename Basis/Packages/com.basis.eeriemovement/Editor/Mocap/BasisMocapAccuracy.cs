using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Mocap
{
    // How the elbow/knee pole is chosen. This is the whole question: with 3- or 6-point tracking the elbow is
    // NOT measured, so the solver has to invent it, and this is the only place we can find out how close that
    // invention lands to where a real human's elbow actually was.
    public enum BasisMocapHintSource
    {
        None,          // no hint at all -- the two-bone core's internal fallback
        Lookup,        // WHAT SHIPS for an untracked arm: ArmBendFrame -> BasisArmBendLookup -> chicken-wing flare
        LookupNoFlare, // the same lookup, with the chicken-wing flare switched off. Isolates what the flare COSTS.
        SwivelModel,   // THE CANDIDATE: BasisArmSwivelModel -- a polynomial fitted to this very corpus.
        // The same model with the elbow's One-Euro output filter (SmoothElbowSwivel) left ON.
        //
        // This row exists to ANSWER A SHIPPING QUESTION rather than guess at it. The filter was added to fight
        // the LOOKUP's jitter; the model is a polynomial and is already smoother than a real tracker, so the
        // filter may now be pure lag. Ship whichever of SwivelModel / SwivelModelSmoothed measures better --
        // and do not reason about it, because a One-Euro is speed-adaptive and its behaviour on a signal it was
        // not tuned for is not something anyone can predict from the armchair.
        SwivelModelSmoothed,
        // WHAT SHIPS NOW: BasisElbowFieldModel. Predicts the elbow's POSITION and projects it onto the
        // reachable circle -- no reference direction, no atan2, no confidence gate.
        //
        // SwivelModel above predicted the swivel ANGLE, which has to be measured FROM something, and every
        // choice of reference vanishes somewhere on the sphere of hand directions (hairy ball). Its choice --
        // body-DOWN -- vanished when the arm hangs down, which is 29.7% of real human frames and the commonest
        // pose in VR. Keep the row: it is the A/B that proves the difference is the frame and not the fit.
        ElbowField,
        // THE NEURAL CANDIDATE: small MLPs (3->24->16->2) fitted to the SAME dumped features as SwivelModel and
        // predicting the SAME (sin,cos) swivel, so they share SwivelModel's exact wiring and BendDirection --
        // this row isolates "MLP vs polynomial" with everything else held fixed. The ELBOW uses
        // BasisArmSwivelNeuralModel (A/B vs BasisElbowFieldModel, which ships); the KNEE uses
        // BasisLegSwivelNeuralModel (A/B vs BasisLegSwivelModel). See SolveArm / SolveLeg.
        NeuralSwivel,
        // The LIVE-RIG elbow path scored offline: BasisSwivelHintCore.ArmHint with useNeural=true (the neural
        // POSITION model, BasisArmElbowNeuralFieldModel). ElbowField is the SAME entry point with useNeural=false,
        // so NeuralField vs ElbowField is EXACTLY what UseNeuralPole flips on a real avatar's elbow. (The knee's
        // live path is the neural angle model, already the NeuralSwivel row's knee column.)
        NeuralField,
        TruthJoint,    // the elbow/knee tracker case: hand the solver the real joint. The accuracy CEILING.
    }

    public struct BasisMocapAccuracySummary
    {
        public bool Ok;
        public string Error;
        public string Clip;
        public string Path;
        public BasisMocapHintSource Hint;
        public int Frames;

        public float ElbowMeanM, ElbowP95M, ElbowMaxM;
        public float KneeMeanM, KneeP95M, KneeMaxM;
        public float ElbowMeanFracArm;   // scale-free: error / arm length
        public float KneeMeanFracLeg;

        // Sanity: we COMMAND the hand and foot, so the solver must hit them. If these are not ~0 the harness
        // is not driving the solver properly and every other number here is meaningless.
        public float HandMaxM, FootMaxM;

        // The cores report a solved pose BOTH as positions and as rotations. Rebuilding the joint from the
        // reported rotations over fixed bone lengths must reproduce the reported position, or the two answers
        // disagree and the temporal carry (which rides the rotations) is quietly solving a different limb.
        public float RigidityMaxM;

        // Pole flips measured on REAL human motion: the elbow jumps while the hand barely moves.
        public int ElbowPops, KneePops;
    }

    public static class BasisMocapAccuracy
    {
        // Mirrors the live defaults (BasisFullIKConstraintJob.SetDefaultValues + the driver's ApplyTuningSettings).
        const float k_FlareMaxDeg = 45f;
        const float k_FlareInwardGain = 1f;
        const float k_FlareFullRollDeg = 70f;
        const float k_HipSpringHz = 8f;
        const float k_HipSpringDamping = 1f;
        const float k_HintRateDegPerSec = 540f;

        // A pole flip: the joint jumps hard while the end effector is essentially still. Real human motion is
        // smooth, so any such jump is the solver's doing, not the human's.
        // Swivel-model diagnostics. Static because SolveArm is static and this is a temporary probe.
        /// <summary>
        /// Training-data dump: the EXACT features this harness feeds BasisArmSwivelModel, plus the true
        /// swivel it should have predicted, plus the circle radius (the weight).
        ///
        /// This exists because fitting in a Python pipeline and evaluating in this one is a bug factory: the
        /// two must agree on the handedness conversion, the body frame, the joint mapping and the left/right
        /// mirror, and a mismatch in ANY of them silently poisons the model. The first attempt did exactly
        /// that -- it scored 31% here and 3.77% in Python, and the probe showed the predicted swivel was
        /// 145 degrees off, i.e. the model was being asked about a different frame than it was fitted in.
        ///
        /// Dumping from HERE and fitting on THAT makes the question unaskable: the model is fitted to the
        /// literal inputs the runtime produces. Whatever the conventions are, both sides now share them.
        /// </summary>
        public static System.Text.StringBuilder s_swivelDump;
        public static System.Text.StringBuilder s_legDump;

        public static float s_swivelDiffSum, s_swivelSumSum;
        public static int s_swivelN;

        // Confidence window for a fitted swivel model. |sin,cos| is the model's own certainty; below the
        // low mark the angle is meaningless and the hint must carry no weight, above the high mark it is
        // trustworthy. Drawn from the measured distribution: the knee sits under 0.2 on ~0.4% of frames and
        // those frames were producing every extra pop.
        const float k_SwivelConfLo = 0.20f;
        const float k_SwivelConfHi = 0.50f;

        const float k_PopJointM = 0.05f;   // 5 cm of elbow/knee travel in one frame
        const float k_PopEffectorM = 0.01f; // while the hand/foot moved under 1 cm

        // The PRE-IK limb, modelled the way the runtime actually produces it: a fixed bind pose riding the parent
        // (chest for an arm, hips for a leg), rebuilt from scratch every frame, with IK layered on top. That is
        // what the Animator does -- it re-evaluates the base layer each frame, so the IK constraint never sees
        // its own previous output as input.
        //
        // Two wrong models were tried first and both corrupt the measurement:
        //   - Carrying the solved pose forward: one bad frame poisons every frame after it. A single knee
        //     inversion at full extension compounded into 74 cm of foot error by the end of a walk cycle.
        //   - Seeding from TRUTH each frame: with no hint the two-bone core preserves the CURRENT bend plane,
        //     so handing it the true elbow makes it trivially "right". A circular measurement that proves nothing.
        // Mirrors BasisFullIKConstraintJob's tracked-knee One-Euro constants (k_TrackedKneeSwivel*). The harness
        // always hands the leg a real foot target, so the runtime's `preserveTip` is false and it takes this
        // responsive branch rather than the heavy standing floor. A MIRROR, not a shared call: retune both.
        const float k_TrackedKneeMinCutoffHz = 1.5f;
        const float k_TrackedKneeBeta = 0.20f;
        const float k_TrackedKneeDerivCutoffHz = 1.0f;

        struct Limb
        {
            public Quaternion RootLocal, MidLocal;         // bind pose relative to the parent
            public Quaternion TipLocal;                    // tip (hand/foot) bind rotation relative to mid
            public Vector3 UpperDirLocal, LowerDirLocal;   // bone axis in its own bone's frame; a bone is rigid
            public float UpperLen, LowerLen;
            public bool Seeded;

            // The knee swivel One-Euro's state. Carried between frames -- unlike the bone POSE, which is
            // deliberately rebuilt from the bind pose every frame. The runtime re-evaluates its base layer each
            // frame so the constraint never sees its own previous pose, but its FILTERS keep their state. Model
            // both or the temporal behaviour is fiction.
            public BasisSwivelFilterState KneeSwivel;
            public bool KneeSwivelSeeded;
        }

        static void SeedLimb(ref Limb l, Quaternion parentRot, Vector3 root, Vector3 mid, Vector3 tip, Quaternion rootRot, Quaternion midRot, Quaternion tipRot)
        {
            l.RootLocal = Quaternion.Inverse(parentRot) * rootRot;
            l.MidLocal = Quaternion.Inverse(rootRot) * midRot;
            l.TipLocal = Quaternion.Inverse(midRot) * tipRot;
            l.UpperLen = Vector3.Distance(root, mid);
            l.LowerLen = Vector3.Distance(mid, tip);
            l.UpperDirLocal = Quaternion.Inverse(rootRot) * (mid - root).normalized;
            l.LowerDirLocal = Quaternion.Inverse(midRot) * (tip - mid).normalized;
            l.Seeded = true;
        }

        // The animated (pre-IK) pose for this frame.
        static void AnimatedPose(in Limb l, Quaternion parentRot, Vector3 root,
                                 out Quaternion rootRot, out Quaternion midRot, out Quaternion tipRot, out Vector3 mid, out Vector3 tip)
        {
            rootRot = parentRot * l.RootLocal;
            midRot = rootRot * l.MidLocal;
            tipRot = midRot * l.TipLocal;
            mid = root + (rootRot * l.UpperDirLocal) * l.UpperLen;
            tip = mid + (midRot * l.LowerDirLocal) * l.LowerLen;
        }

        // Rebuild a joint from a pair of solved rotations over the fixed bone lengths.
        static Vector3 RebuildMid(in Limb l, Vector3 root, Quaternion rootRot) => root + (rootRot * l.UpperDirLocal) * l.UpperLen;

        /// <summary>
        /// Per-frame joint tracks from a mocap run: what the human's joint did, and what the solver's did,
        /// frame by frame, in the same units.
        ///
        /// The accuracy layer above reduces those two tracks to a distance. That answers "is the pose
        /// right" and is deliberately blind to "is the MOTION right" -- a solved elbow can sit 2 cm from
        /// the truth on every single frame while buzzing at 30 Hz, plateauing, or arriving 150 ms late,
        /// and the mean error will not budge. Handing the raw tracks out lets BasisMocapMotionQuality
        /// score the thing the distance throws away.
        ///
        /// Left side only, matching the side the ElbowPops/KneePops detectors already sample.
        /// </summary>
        public sealed class BasisMocapTracks
        {
            public float Dt, ArmLen, LegLen;
            public Vector3[] TruthElbow, SolvedElbow;
            public Vector3[] TruthKnee, SolvedKnee;
            public Vector3[] TruthHand, TruthFoot;

            /// <summary>The elbow HINT the lookup path hands the solver, captured at two stages so the noise
            /// can be localised instead of guessed at. HintRaw is the lookup table's own output; HintFlared is
            /// that same bend after BasisElbowFlareCore.ApplyChickenWingFlare, which is what actually reaches
            /// the solver. Comparing their jitter says immediately WHICH stage invents the buzz -- the table,
            /// the flare, or (if both are clean) the two-bone solve amplifying a clean hint.
            /// Populated only when hint == Lookup. Left side.</summary>
            public Vector3[] HintRaw, HintFlared;

            /// <summary>Flare internals, for localising its noise: the engagement scalar it drives the bend with,
            /// and the down-pole projection the whole swivel angle is measured from (collapses when the forearm
            /// goes vertical -- i.e. an arm hanging at your side).</summary>
            public float[] FlareEngage, FlareDownProj;

            /// <summary>Per-frame LEG solve internals, so a knee pop can be LOCALISED instead of theorised
            /// about. KneeReach is hip->foot distance over max reach (the pole singularity lives at 1.0);
            /// KneeAxis is BasisLegSolveResult.AxisSource (0 plane-normal, 1 hint, 2 target, 3 bend-normal,
            /// 4 pole blended toward bend-normal, 5 pole clamped into the anterior half-space). Left side.</summary>
            public float[] KneeReach;
            public byte[] KneeAxis;
        }

        public static BasisMocapAccuracySummary Run(BasisMotionClip clip, BasisMocapHintSource hint, string csvPath)
            => Run(clip, hint, csvPath, null);

        public static BasisMocapAccuracySummary Run(BasisMotionClip clip, BasisMocapHintSource hint, string csvPath,
                                                    BasisMocapTracks tracks)
        {
            var s = new BasisMocapAccuracySummary { Hint = hint, Path = csvPath };

            if (clip == null || clip.FrameCount < 2) { s.Error = "clip too short"; return s; }
            if (!BasisBvhLoader.Validate(clip, out string why)) { s.Error = why; return s; }
            s.Clip = clip.Name;

            NativeArray<Vector3> lookup = default;
            try
            {
                // BOTH lookup modes need the table -- LookupNoFlare disables the FLARE, not the table. Miss this
                // and SampleTrilinear indexes a default (length-0) NativeArray.
                if (hint == BasisMocapHintSource.Lookup || hint == BasisMocapHintSource.LookupNoFlare)
                {
                    // Persistent, not Temp: Temp is frame-scoped and this table has to outlive a many-thousand
                    // frame sweep that never yields to a frame boundary.
                    Vector3[] table = BasisArmBendLookup.GenerateDefaultTable();
                    lookup = new NativeArray<Vector3>(table, Allocator.Persistent);
                }

                var elbowErr = new List<float>();
                var kneeErr = new List<float>();
                float armLen = 0f, legLen = 0f;
                float dt = Mathf.Max(clip.FrameTime, 1e-4f);
                var csv = new StringBuilder("clip,frame,limb,err_m,truth_x,truth_y,truth_z,solved_x,solved_y,solved_z,reach,eff_err_m,axis\n");

                if (tracks != null)
                {
                    int n = clip.FrameCount;
                    tracks.Dt = dt;
                    tracks.TruthElbow = new Vector3[n]; tracks.SolvedElbow = new Vector3[n];
                    tracks.TruthKnee = new Vector3[n]; tracks.SolvedKnee = new Vector3[n];
                    tracks.TruthHand = new Vector3[n]; tracks.TruthFoot = new Vector3[n];
                    tracks.HintRaw = new Vector3[n]; tracks.HintFlared = new Vector3[n];
                    tracks.FlareEngage = new float[n]; tracks.FlareDownProj = new float[n];
                    tracks.KneeReach = new float[n]; tracks.KneeAxis = new byte[n];
                }

                var arms = new Limb[2];
                var legs = new Limb[2];

                // Elbow-swivel One-Euro state, carried frame to frame exactly as the job's NativeArray slots
                // are. The BONE pose is never carried (see the header); the FILTER state always is.
                var armSwivel = new BasisSwivelFilterState[2];
                var armSwivelSeeded = new bool[2];
                Quaternion hipSpringRot = Quaternion.identity;
                Vector3 hipSpringVel = Vector3.zero;
                bool hipSpringSeeded = false;
                Vector3 prevElbow0 = Vector3.zero, prevKnee0 = Vector3.zero, prevHand0 = Vector3.zero, prevFoot0 = Vector3.zero;

                for (int f = 0; f < clip.FrameCount; f++)
                {
                    Quaternion hipsRot = clip.Get(f, BasisMocapJoint.Hips).Rotation;
                    Quaternion chestRot = clip.Get(f, BasisMocapJoint.Chest).Rotation;
                    Vector3 playerUp = hipsRot * Vector3.up;

                    // The live rig samples the elbow lookup in a spring-smoothed hips frame, so do the same --
                    // an unsmoothed frame would hand the solver a cleaner pole than it gets in the headset.
                    if (!hipSpringSeeded) { hipSpringRot = hipsRot; hipSpringVel = Vector3.zero; hipSpringSeeded = true; }
                    else
                    {
                        BasisHipFrameSpringCore.Step(hipSpringRot, hipSpringVel, hipsRot, dt, k_HipSpringHz, k_HipSpringDamping,
                            out hipSpringRot, out hipSpringVel);
                    }
                    Quaternion bendFrame = ArmBendFrame(hipSpringRot, chestRot);

                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        SolveArm(clip, f, isLeft, ref arms[side], bendFrame, chestRot, playerUp, hipsRot, hint, lookup, dt,
                                 ref armSwivel[side], ref armSwivelSeeded[side],
                                 out Vector3 truthElbow, out Vector3 solvedElbow, out float handErr, out float reach, out float aLen,
                                 out float armRigid, out byte armAxis, out Vector3 hintRaw, out Vector3 hintFlared,
                                 out float flareEngage, out float flareDownProj);
                        armLen = aLen;
                        s.RigidityMaxM = Mathf.Max(s.RigidityMaxM, armRigid);
                        float e = Vector3.Distance(truthElbow, solvedElbow);
                        elbowErr.Add(e);
                        s.HandMaxM = Mathf.Max(s.HandMaxM, handErr);
                        Append(csv, clip.Name, f, isLeft ? "leftArm" : "rightArm", e, truthElbow, solvedElbow, reach, handErr, armAxis);

                        if (side == 0 && f > 0)
                        {
                            Vector3 hand = clip.Get(f, BasisMocapJoint.LeftHand).Position;
                            if (Vector3.Distance(solvedElbow, prevElbow0) > k_PopJointM &&
                                Vector3.Distance(hand, prevHand0) < k_PopEffectorM) s.ElbowPops++;
                            prevHand0 = hand;
                        }
                        if (side == 0) { prevElbow0 = solvedElbow; if (f == 0) prevHand0 = clip.Get(0, BasisMocapJoint.LeftHand).Position; }

                        SolveLeg(clip, f, isLeft, ref legs[side], hipsRot, hint, dt,
                                 out Vector3 truthKnee, out Vector3 solvedKnee, out float footErr, out float lReach, out float lLen,
                                 out float legRigid, out byte legAxis);
                        legLen = lLen;
                        s.RigidityMaxM = Mathf.Max(s.RigidityMaxM, legRigid);
                        float k = Vector3.Distance(truthKnee, solvedKnee);
                        kneeErr.Add(k);
                        s.FootMaxM = Mathf.Max(s.FootMaxM, footErr);
                        Append(csv, clip.Name, f, isLeft ? "leftLeg" : "rightLeg", k, truthKnee, solvedKnee, lReach, footErr, legAxis);

                        if (side == 0 && f > 0)
                        {
                            Vector3 foot = clip.Get(f, BasisMocapJoint.LeftFoot).Position;
                            if (Vector3.Distance(solvedKnee, prevKnee0) > k_PopJointM &&
                                Vector3.Distance(foot, prevFoot0) < k_PopEffectorM) s.KneePops++;
                            prevFoot0 = foot;
                        }
                        if (side == 0) { prevKnee0 = solvedKnee; if (f == 0) prevFoot0 = clip.Get(0, BasisMocapJoint.LeftFoot).Position; }

                        if (side == 0 && tracks != null)
                        {
                            tracks.TruthElbow[f] = truthElbow; tracks.SolvedElbow[f] = solvedElbow;
                            tracks.TruthKnee[f] = truthKnee; tracks.SolvedKnee[f] = solvedKnee;
                            tracks.TruthHand[f] = clip.Get(f, BasisMocapJoint.LeftHand).Position;
                            tracks.TruthFoot[f] = clip.Get(f, BasisMocapJoint.LeftFoot).Position;
                            tracks.HintRaw[f] = hintRaw; tracks.HintFlared[f] = hintFlared;
                            tracks.FlareEngage[f] = flareEngage; tracks.FlareDownProj[f] = flareDownProj;
                            tracks.KneeReach[f] = lReach; tracks.KneeAxis[f] = legAxis;
                        }
                    }
                }

                if (tracks != null) { tracks.ArmLen = armLen; tracks.LegLen = legLen; }

                elbowErr.Sort();
                kneeErr.Sort();
                s.Frames = clip.FrameCount;
                s.ElbowMeanM = Mean(elbowErr); s.ElbowP95M = Pct(elbowErr, 0.95f); s.ElbowMaxM = elbowErr[elbowErr.Count - 1];
                s.KneeMeanM = Mean(kneeErr); s.KneeP95M = Pct(kneeErr, 0.95f); s.KneeMaxM = kneeErr[kneeErr.Count - 1];
                s.ElbowMeanFracArm = armLen > 1e-4f ? s.ElbowMeanM / armLen : float.NaN;
                s.KneeMeanFracLeg = legLen > 1e-4f ? s.KneeMeanM / legLen : float.NaN;
                s.Ok = true;

                if (!string.IsNullOrEmpty(csvPath))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(csvPath));
                    System.IO.File.WriteAllText(csvPath, csv.ToString());
                }
                return s;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
                return s;
            }
            finally
            {
                if (lookup.IsCreated) lookup.Dispose();
            }
        }

        // Mirror of BasisFullIKConstraintJob.ArmBendFrame: spring-smoothed hips, chest swing only (yaw dropped).
        // Kept in lock-step by hand until the job's hint path is extracted into a core -- that extraction is the
        // next stage and it will delete this method.
        static Quaternion ArmBendFrame(Quaternion hipsRot, Quaternion chestRot)
        {
            Quaternion chestRelative = Quaternion.Inverse(hipsRot) * chestRot;
            Quaternion chestYaw = BasisTwistSolveCore.ExtractTwist(chestRelative, Vector3.up);
            Quaternion chestSwing = chestRelative * Quaternion.Inverse(chestYaw);
            return hipsRot * chestSwing;
        }

        static void SolveArm(BasisMotionClip clip, int f, bool isLeft, ref Limb limb, Quaternion bendFrame, Quaternion chestRot,
                             Vector3 playerUp, Quaternion hipsRot, BasisMocapHintSource hint, NativeArray<Vector3> lookup, float dt,
                             ref BasisSwivelFilterState swivelState, ref bool swivelSeeded,
                             out Vector3 truthElbow, out Vector3 solvedElbow, out float handErr, out float reach, out float armLen,
                             out float rigidity, out byte axis, out Vector3 hintRaw, out Vector3 hintFlared,
                             out float flareEngage, out float flareDownProj)
        {
            hintRaw = Vector3.zero;
            hintFlared = Vector3.zero;
            flareEngage = 0f; flareDownProj = 1f;
            BasisMocapJoint jS = isLeft ? BasisMocapJoint.LeftUpperArm : BasisMocapJoint.RightUpperArm;
            BasisMocapJoint jE = isLeft ? BasisMocapJoint.LeftLowerArm : BasisMocapJoint.RightLowerArm;
            BasisMocapJoint jH = isLeft ? BasisMocapJoint.LeftHand : BasisMocapJoint.RightHand;

            Vector3 shoulder = clip.Get(f, jS).Position;
            truthElbow = clip.Get(f, jE).Position;
            Vector3 truthHand = clip.Get(f, jH).Position;
            Quaternion truthHandRot = clip.Get(f, jH).Rotation;
            armLen = Vector3.Distance(shoulder, truthElbow) + Vector3.Distance(truthElbow, truthHand);

            // The shoulder is handed to the solver from truth: the torso is not what is under test here, and
            // letting torso error leak in would confound the one number we actually want -- the elbow.
            if (!limb.Seeded) SeedLimb(ref limb, chestRot, shoulder, truthElbow, truthHand, clip.Get(f, jS).Rotation, clip.Get(f, jE).Rotation, clip.Get(f, jH).Rotation);
            AnimatedPose(limb, chestRot, shoulder, out Quaternion animRootRot, out Quaternion animMidRot, out Quaternion animTipRot, out Vector3 elbow, out Vector3 hand);

            BasisArmSolveInput i = default;
            i.Shoulder = shoulder;
            i.Elbow = elbow;
            i.Hand = hand;
            i.RootRotation = animRootRot;
            i.MidRotation = animMidRot;
            // The animated wrist feeds the wrist-roll relief, so it goes to the modes that MODEL THE LIVE
            // RIG: TruthJoint (the tracker path, where the relief stands down in-core -- fed anyway so the
            // gate sees it if that rule ever changes) and ElbowField (what ships). The legacy baselines
            // (Lookup, SwivelModel) stay frozen without it: they exist as fixed points of comparison, and a
            // baseline that accretes new features stops being a baseline.
            i.TipRotation = hint == BasisMocapHintSource.TruthJoint || hint == BasisMocapHintSource.ElbowField
                || hint == BasisMocapHintSource.NeuralField
                ? animTipRot : default;
            i.TargetPosition = truthHand;
            i.TargetRotation = truthHandRot;
            i.TargetOffset = Quaternion.identity;
            i.PlayerUp = playerUp;
            // Unclamped: the rate limiter exists to bound the swivel change from the PREVIOUS SOLVE, and this is
            // a memoryless solve off the bind pose. Applying it here would throttle the solver's reach to its own
            // hint and be read as pole error that the live rig does not have.
            i.HintMaxStepDeg = float.MaxValue;

            switch (hint)
            {
                case BasisMocapHintSource.TruthJoint:
                    i.HintPosition = truthElbow;
                    i.HintWeight = true;
                    i.HintIsTracker = true;
                    // A real tracker reports rotation too: the truth lower-arm plays it, so the tracker
                    // forearm roll is exercised against the corpus exactly as the live rig runs it.
                    i.HintRotation = clip.Get(f, jE).Rotation;
                    break;
                case BasisMocapHintSource.ElbowField:
                case BasisMocapHintSource.NeuralField:
                {
                    // WHAT SHIPS (ElbowField) vs the neural live path (NeuralField): the SAME entry point,
                    // BasisSwivelHintCore.ArmHint, with useNeural false/true -- rather than re-deriving the
                    // features here, so the harness and the rig cannot disagree about handedness, body frame or
                    // mirror. Every previous disagreement produced confident garbage a green test suite waved
                    // through (once by 145 degrees, once by putting the elbows up beside the ears in a headset).
                    // NeuralField is EXACTLY the elbow a real avatar gets under UseNeuralPole, everything else fixed.
                    //
                    // No confidence gate: both predict a POSITION and fade their one geometric degeneracy
                    // internally. See BasisElbowFieldModel / BasisArmElbowNeuralFieldModel.
                    BasisSwivelFrame sf = BasisSwivelHintCore.BuildFrame(
                        clip.Get(f, BasisMocapJoint.LeftUpperArm).Position,
                        clip.Get(f, BasisMocapJoint.RightUpperArm).Position,
                        clip.Get(f, BasisMocapJoint.Chest).Position,
                        clip.Get(f, BasisMocapJoint.Neck).Position);
                    if (BasisSwivelHintCore.ArmHint(sf, shoulder, truthHand, armLen, isLeft,
                                                    out Vector3 fieldHint, out _,
                                                    hint == BasisMocapHintSource.NeuralField))
                    {
                        i.HintPosition = fieldHint;
                        i.HintWeight = true;
                        i.HintIsTracker = false;
                    }
                    break;
                }
                case BasisMocapHintSource.Lookup:
                case BasisMocapHintSource.LookupNoFlare:
                {
                    // inwardGain = 0 is the flare's own documented off-switch (RollEngagement01 early-returns).
                    float flareGain = hint == BasisMocapHintSource.LookupNoFlare ? 0f : k_FlareInwardGain;
                    Vector3 bend = ComputeArmBendFromLookup(lookup, bendFrame, shoulder, truthHand, truthHandRot, armLen, isLeft, playerUp,
                                                            flareGain, out Vector3 preFlare, out flareEngage, out flareDownProj);
                    i.HintPosition = shoulder + 0.5f * armLen * bend;
                    i.HintWeight = true;
                    i.HintIsTracker = false;   // the job passes hintIsTracker = hasHint && !usedLookup
                    hintFlared = i.HintPosition;
                    hintRaw = shoulder + 0.5f * armLen * preFlare;
                    break;
                }
                case BasisMocapHintSource.SwivelModel:
                case BasisMocapHintSource.SwivelModelSmoothed:
                case BasisMocapHintSource.NeuralSwivel:
                {
                    // The elbow is confined to a CIRCLE, so predict the one angle that places it on that
                    // circle rather than a 3-vector that does not lie on it. See BasisArmSwivelModel.
                    //
                    // The body frame is built from POSITIONS on purpose. A bone's local axes are a rig
                    // convention (CMU's chest bone is not Unity's), so a frame taken from bone ROTATIONS
                    // would not transfer. Shoulder-line and spine-direction are anatomy, and they do.
                    Vector3 lSh = clip.Get(f, BasisMocapJoint.LeftUpperArm).Position;
                    Vector3 rSh = clip.Get(f, BasisMocapJoint.RightUpperArm).Position;
                    Vector3 neck = clip.Get(f, BasisMocapJoint.Neck).Position;
                    Vector3 chest = clip.Get(f, BasisMocapJoint.Chest).Position;

                    Vector3 bUp = (neck - chest).normalized;
                    Vector3 bRight = (rSh - lSh);
                    bRight = (bRight - bUp * Vector3.Dot(bRight, bUp)).normalized;
                    Vector3 bFwd = Vector3.Cross(bRight, bUp);
                    Vector3 bOut = isLeft ? -bRight : bRight;   // mirrored: +x is OUTWARD for both arms

                    Vector3 s2h = truthHand - shoulder;
                    var handLocal = new Unity.Mathematics.float3(
                        Vector3.Dot(s2h, bOut) / armLen,
                        Vector3.Dot(s2h, bUp) / armLen,
                        Vector3.Dot(s2h, bFwd) / armLen);

                    // POSITIONS ONLY. The hand's ROTATION used to be 27 more features here, divided by its own
                    // T-pose -- and it put the elbows up by the ears in a headset while every test stayed green.
                    // See BasisArmSwivelModel: a bone's rotation is a rig convention, and the live rig's T-pose
                    // bake was not reliably a T-pose. Anatomy transfers; conventions do not.
                    // NeuralSwivel swaps ONLY the model: same features, same (sin,cos) convention, same BendDirection.
                    float aconf;
                    float swivel = hint == BasisMocapHintSource.NeuralSwivel
                        ? BasisArmSwivelNeuralModel.SwivelRad(handLocal, out aconf)
                        : BasisArmSwivelModel.SwivelRad(handLocal, out aconf);
                    if (isLeft) swivel = -swivel;   // un-mirror

                    // DIAGNOSTIC: what IS the true swivel on this frame? A prediction that is right looks
                    // different from one that is sign-flipped, and both look different from garbage.
                    // NeuralSwivel skips the probe/dump: the training CSV must stay the SwivelModel frame only.
                    if (hint != BasisMocapHintSource.NeuralSwivel)
                    {
                        Vector3 ax = s2h.normalized;
                        Vector3 dn = -bUp;
                        Vector3 uu = (dn - ax * Vector3.Dot(dn, ax)).normalized;
                        Vector3 vv = Vector3.Cross(ax, uu);
                        Vector3 rp = (truthElbow - shoulder);
                        rp -= ax * Vector3.Dot(rp, ax);
                        float trueSw = Mathf.Atan2(Vector3.Dot(rp, vv), Vector3.Dot(rp, uu));
                        s_swivelDiffSum += Mathf.Abs(Mathf.DeltaAngle(swivel * Mathf.Rad2Deg, trueSw * Mathf.Rad2Deg));
                        s_swivelSumSum += Mathf.Abs(Mathf.DeltaAngle(-swivel * Mathf.Rad2Deg, trueSw * Mathf.Rad2Deg));
                        s_swivelN++;

                        if (s_swivelDump != null)
                        {
                            // The fit TARGET is the MIRRORED swivel, matching the mirrored features.
                            float phiFit = isLeft ? -trueSw : trueSw;

                            // The circle radius: position error ~= radius * angular error, so this is the
                            // weight that makes the regression minimise what we actually measure.
                            float dist = Mathf.Clamp(s2h.magnitude, 1e-6f, armLen - 1e-6f);
                            float aa = (limb.UpperLen * limb.UpperLen - limb.LowerLen * limb.LowerLen + dist * dist) / (2f * dist);
                            float rad = Mathf.Sqrt(Mathf.Max(limb.UpperLen * limb.UpperLen - aa * aa, 0f)) / armLen;

                            // The RAW elbow position in the same mirrored body frame (ex,ey,ez), for the
                            // position-target model (BasisElbowFieldModel style): predict a 3-vector and project.
                            // Taken DIRECTLY from truthElbow -- reconstructing it from phi would re-inject the
                            // (uu,vv) reference singularity the position representation exists to escape.
                            Vector3 e2s = truthElbow - shoulder;
                            float ex = Vector3.Dot(e2s, bOut) / armLen;
                            float ey = Vector3.Dot(e2s, bUp) / armLen;
                            float ez = Vector3.Dot(e2s, bFwd) / armLen;

                            var sb = s_swivelDump;
                            sb.Append(clip.Name).Append(',').Append(isLeft ? 'L' : 'R').Append(',');
                            sb.Append(F(handLocal.x)).Append(',').Append(F(handLocal.y)).Append(',').Append(F(handLocal.z));
                            sb.Append(',').Append(F(phiFit)).Append(',').Append(F(rad));
                            sb.Append(',').Append(F(ex)).Append(',').Append(F(ey)).Append(',').Append(F(ez)).AppendLine();
                        }
                    }

                    // -bUp: an elbow hangs DOWN, and BendDirection now takes the reference AS GIVEN rather
                    // than negating it internally. Passing bUp put the elbow above the shoulder (34.98% error).
                    Unity.Mathematics.float3 bend = BasisArmSwivelModel.BendDirection(
                        new Unity.Mathematics.float3(s2h.x, s2h.y, s2h.z),
                        new Unity.Mathematics.float3(-bUp.x, -bUp.y, -bUp.z), swivel);

                    i.HintPosition = shoulder + 0.5f * armLen * new Vector3(bend.x, bend.y, bend.z);
                    // The arm's confidence never collapses in practice (min 0.37 across the corpus, vs the
                    // knee's 0.03), but the guard is free and the failure it prevents is a spinning elbow.
                    i.HintWeight = aconf > k_SwivelConfLo;
                    i.HintIsTracker = false;
                    break;
                }
                default:
                    i.HintWeight = false;
                    break;
            }

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
            solvedElbow = r.ElbowSolved;
            handErr = Vector3.Distance(r.HandSolved, truthHand);
            reach = r.ReachRatio;
            axis = r.AxisSource;

            // Nothing is carried: next frame rebuilds the pre-IK arm from the bind pose again.
            rigidity = Vector3.Distance(RebuildMid(limb, shoulder, r.RootRotationSolved), r.ElbowSolved);

            // ⚠ THE LIVE RIG DOES NOT SHIP THE RAW LOOKUP. BasisFullBodyIK.SmoothElbowSwivel One-Euro-filters
            // the elbow SWIVEL whenever the pole came from the lookup table (gated on `usedLookup`, so an elbow
            // TRACKER bypasses it entirely). Leaving it out here would measure a system the runtime never
            // produces -- which is precisely the blind spot the foot sweep already paid for, where the sweep
            // seeded state differently from the runtime and therefore could not see the runtime's bug.
            //
            // Same core the job calls, same constants, same body-frame reference. The one thing that is a
            // MIRROR and not a shared call is the input wiring below; if SmoothElbowSwivel is retuned, retune
            // this too. (Same lock-step caveat as ArmBendFrame / ComputeArmBendFromLookup.)
            //
            // The swivel state is CARRIED between frames -- unlike the bone pose, which is deliberately not.
            // That distinction is the whole point: the runtime re-evaluates the base layer every frame so the
            // constraint never sees its own previous POSE, but its FILTERS keep their state. Model both or the
            // temporal behaviour is fiction.
            if (hint == BasisMocapHintSource.Lookup || hint == BasisMocapHintSource.LookupNoFlare
                || hint == BasisMocapHintSource.SwivelModelSmoothed)
            {
                BasisSwivelSmootherInput sw = default;
                sw.Root = shoulder;
                sw.Mid = solvedElbow;
                sw.Tip = r.HandSolved;
                sw.BodyRotation = hipsRot;
                sw.ReferenceLocal = Vector3.down;   // arm reference; the leg uses forward
                sw.FallbackLocal = Vector3.zero;
                sw.Dt = dt;
                sw.MinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
                sw.Beta = BasisSwivelFilterCore.Beta;
                sw.DerivCutoffHz = BasisSwivelFilterCore.DerivCutoffHz;
                sw.ConditionOnPole = false;         // the arm leaves this off; only the leg conditions on the pole
                sw.State = swivelState;
                sw.Seeded = swivelSeeded;

                BasisSwivelSmootherCore.Solve(sw, out BasisSwivelSmootherResult sr);
                if (sr.WriteState)
                {
                    swivelState = sr.State;
                    swivelSeeded = true;
                }
                // Degenerate frame => the job declines to move the bone, so neither do we.
                if (sr.Valid) solvedElbow = sr.DesiredMid;
            }
        }

        // Mirror of BasisFullIKConstraintJob.ComputeArmBendFromLookup. Same lock-step caveat as ArmBendFrame.
        // `preFlare` is the table's own output before the chicken-wing flare is applied -- captured purely so
        // the motion-quality layer can tell which stage of this path is inventing the elbow buzz.
        static Vector3 ComputeArmBendFromLookup(NativeArray<Vector3> lookup, Quaternion frameRot, Vector3 shoulder,
                                                Vector3 handTarget, Quaternion handTargetRot, float armLength, bool isLeft, Vector3 playerUp,
                                                float flareInwardGain, out Vector3 preFlare, out float engage01, out float downProj)
        {
            preFlare = isLeft ? Vector3.left : Vector3.right;
            engage01 = 0f; downProj = 1f;
            if (armLength < 1e-5f) return preFlare;

            Quaternion invFrame = Quaternion.Inverse(frameRot);
            Vector3 shoulderToHand = handTarget - shoulder;
            Vector3 localPos = invFrame * shoulderToHand / armLength;
            if (isLeft) localPos.x = -localPos.x;

            Vector3 localBend = BasisArmBendLookup.SampleTrilinear(lookup, localPos);
            if (isLeft) localBend.x = -localBend.x;

            Vector3 worldBend = (frameRot * localBend).normalized;
            preFlare = worldBend;
            Vector3 outward = frameRot * (isLeft ? Vector3.left : Vector3.right);

            // Diagnostics, so the flare's noise source can be LOCALISED rather than guessed at. Two hypotheses
            // have already been tested and killed by measurement (the trilinear cell-crossing discontinuity,
            // and the hand-up projection collapse in RollEngagement01) -- both changed the numbers by nothing.
            // These expose the remaining candidates: the engagement scalar itself, and the down-pole that the
            // whole swivel angle is measured FROM, which collapses when the forearm goes vertical (an arm
            // hanging at your side, which is not an edge case, it is the resting pose).
            Vector3 axis = shoulderToHand.sqrMagnitude > 1e-10f ? shoulderToHand.normalized : Vector3.down;
            downProj = Vector3.ProjectOnPlane(-playerUp, axis).magnitude;   // sin(angle of forearm off vertical)
            engage01 = BasisElbowFlareCore.RollEngagement01(handTargetRot, shoulderToHand, outward, playerUp,
                                                            flareInwardGain, k_FlareFullRollDeg);

            return BasisElbowFlareCore.ApplyChickenWingFlare(worldBend, shoulderToHand, outward, playerUp, handTargetRot,
                                                             flareInwardGain, k_FlareFullRollDeg, k_FlareMaxDeg);
        }

        static void SolveLeg(BasisMotionClip clip, int f, bool isLeft, ref Limb limb, Quaternion hipsRot,
                             BasisMocapHintSource hint, float dt,
                             out Vector3 truthKnee, out Vector3 solvedKnee, out float footErr, out float reach, out float legLen,
                             out float rigidity, out byte axis)
        {
            BasisMocapJoint jH = isLeft ? BasisMocapJoint.LeftUpperLeg : BasisMocapJoint.RightUpperLeg;
            BasisMocapJoint jK = isLeft ? BasisMocapJoint.LeftLowerLeg : BasisMocapJoint.RightLowerLeg;
            BasisMocapJoint jF = isLeft ? BasisMocapJoint.LeftFoot : BasisMocapJoint.RightFoot;

            Vector3 hip = clip.Get(f, jH).Position;
            truthKnee = clip.Get(f, jK).Position;
            Vector3 truthFoot = clip.Get(f, jF).Position;
            Quaternion truthFootRot = clip.Get(f, jF).Rotation;
            legLen = Vector3.Distance(hip, truthKnee) + Vector3.Distance(truthKnee, truthFoot);

            if (!limb.Seeded) SeedLimb(ref limb, hipsRot, hip, truthKnee, truthFoot, clip.Get(f, jH).Rotation, clip.Get(f, jK).Rotation, clip.Get(f, jF).Rotation);
            AnimatedPose(limb, hipsRot, hip, out Quaternion animRootRot, out Quaternion animMidRot, out _, out Vector3 knee, out Vector3 foot);

            BasisLegSolveInput i = default;
            i.Root = hip;
            i.Mid = knee;
            i.Tip = foot;
            i.RootRotation = animRootRot;
            i.MidRotation = animMidRot;
            i.TargetPosition = truthFoot;
            i.TargetRotation = truthFootRot;
            i.TargetOffset = Quaternion.identity;
            i.BendNormal = hipsRot * Vector3.right;   // the runtime's no-tracker knee bend normal

            // The body frame for the LEG hangs off the pelvis, not the chest. Built from POSITIONS, because a
            // bone's local axes are a rig convention and would not transfer; hip-line and pelvis-up are anatomy.
            Vector3 lHip = clip.Get(f, BasisMocapJoint.LeftUpperLeg).Position;
            Vector3 rHip = clip.Get(f, BasisMocapJoint.RightUpperLeg).Position;
            Vector3 hipsP = clip.Get(f, BasisMocapJoint.Hips).Position;
            Vector3 chestP = clip.Get(f, BasisMocapJoint.Chest).Position;

            Vector3 gUp = (chestP - hipsP).normalized;
            Vector3 gRight = (rHip - lHip);
            gRight = (gRight - gUp * Vector3.Dot(gRight, gUp)).normalized;
            Vector3 gFwd = Vector3.Cross(gRight, gUp);
            Vector3 gOut = isLeft ? -gRight : gRight;

            Vector3 h2f = truthFoot - hip;
            var legLocal = new Unity.Mathematics.float3(
                Vector3.Dot(h2f, gOut) / legLen, Vector3.Dot(h2f, gUp) / legLen, Vector3.Dot(h2f, gFwd) / legLen);

            // A knee points FORWARD, so that is the swivel's zero. (The arm's is body-down.)
            if (hint == BasisMocapHintSource.TruthJoint)
            {
                i.HintPosition = truthKnee;
                i.HintWeight = 1f;
            }
            else if (hint == BasisMocapHintSource.SwivelModel || hint == BasisMocapHintSource.SwivelModelSmoothed
                     || hint == BasisMocapHintSource.NeuralSwivel)
            {
                // SwivelModel/SwivelModelSmoothed share the polynomial knee (the smoothing varies only the ARM's
                // output filter); NeuralSwivel swaps in the neural knee. Same features, same (sin,cos), same
                // BendDirection -- so the knee column A/Bs BasisLegSwivelNeuralModel against BasisLegSwivelModel.
                float conf;
                float kneeSwivel = hint == BasisMocapHintSource.NeuralSwivel
                    ? BasisLegSwivelNeuralModel.SwivelRad(legLocal, out conf)   // POSITIONS ONLY -- see BasisLegSwivelNeuralModel
                    : BasisLegSwivelModel.SwivelRad(legLocal, out conf);        // POSITIONS ONLY -- see BasisLegSwivelModel
                if (isLeft) kneeSwivel = -kneeSwivel;
                Unity.Mathematics.float3 kb = BasisLegSwivelModel.BendDirection(
                    new Unity.Mathematics.float3(h2f.x, h2f.y, h2f.z),
                    new Unity.Mathematics.float3(gOut.x, gOut.y, gOut.z), kneeSwivel);
                i.HintPosition = hip + 0.5f * legLen * new Vector3(kb.x, kb.y, kb.z);

                // FULL WEIGHT, and the confidence fade is deliberately GONE.
                //
                // Fading the hint weight toward zero does not avoid a pop -- it CREATES one. The knee then
                // falls back to the solver's own BendNormal pole, which points somewhere completely
                // unrelated, and swinging between two unrelated poles over a few frames IS a pop. Measured:
                // the fade moved the knee from 70 pops to 65. It relocated the discontinuity, it did not
                // remove it.
                //
                // The real fix is upstream, in the model's constant term: the direction field is BIASED away
                // from the origin so (sin, cos) can never approach it, so atan2 can never spin, so there is
                // nothing to fade. See BasisLegSwivelModel. `conf` is still reported for diagnostics.
                i.HintWeight = 1f;
            }
            else
            {
                i.HintWeight = 0f;   // no knee tracker: the leg falls back to the bend normal
            }

            if (s_legDump != null && hint != BasisMocapHintSource.NeuralSwivel
                && hint != BasisMocapHintSource.NeuralField)
            {
                Vector3 ax = h2f.normalized;
                Vector3 uu = (gOut - ax * Vector3.Dot(gOut, ax)).normalized;
                Vector3 vv = Vector3.Cross(ax, uu);
                Vector3 rp = truthKnee - hip;
                rp -= ax * Vector3.Dot(rp, ax);
                float trueSw = Mathf.Atan2(Vector3.Dot(rp, vv), Vector3.Dot(rp, uu));
                float phiFit = isLeft ? -trueSw : trueSw;

                float dist = Mathf.Clamp(h2f.magnitude, 1e-6f, legLen - 1e-6f);
                float aa = (limb.UpperLen * limb.UpperLen - limb.LowerLen * limb.LowerLen + dist * dist) / (2f * dist);
                float rad = Mathf.Sqrt(Mathf.Max(limb.UpperLen * limb.UpperLen - aa * aa, 0f)) / legLen;

                // The RAW knee position in the same mirrored body frame (ex,ey,ez), for the position-target
                // model: predict a 3-vector and project onto the reachable circle. Taken DIRECTLY from
                // truthKnee -- reconstructing it from phi would re-inject the (uu,vv) reference singularity.
                Vector3 k2h = truthKnee - hip;
                float ex = Vector3.Dot(k2h, gOut) / legLen;
                float ey = Vector3.Dot(k2h, gUp) / legLen;
                float ez = Vector3.Dot(k2h, gFwd) / legLen;

                var sb = s_legDump;
                sb.Append(clip.Name).Append(',').Append(isLeft ? 'L' : 'R').Append(',');
                sb.Append(F(legLocal.x)).Append(',').Append(F(legLocal.y)).Append(',').Append(F(legLocal.z));
                sb.Append(',').Append(F(phiFit)).Append(',').Append(F(rad));
                sb.Append(',').Append(F(ex)).Append(',').Append(F(ey)).Append(',').Append(F(ez)).AppendLine();
            }

            BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);
            solvedKnee = r.KneeSolved;
            footErr = Vector3.Distance(r.FootSolved, truthFoot);
            reach = r.ReachRatio;
            axis = r.AxisSource;
            rigidity = Vector3.Distance(RebuildMid(limb, hip, r.RootRotationSolved), r.KneeSolved);

            // ⚠ THE LIVE RIG DOES NOT SHIP THE RAW LEG SOLVE, AND THIS HARNESS USED TO PRETEND IT DID.
            //
            // BasisFullIKConstraintJob.SolveLegs One-Euro-filters the knee SWIVEL on every no-knee-tracker path
            // (gated on the legSwivelSmoothing setting, which ships ON) and guards it into the anterior
            // half-space afterwards. Leaving that out measured a solver the runtime never produces -- and it
            // mattered: it was the difference between "the swivel model doubles the knee's pops" and the truth.
            // Exactly the blind spot the arm's SmoothElbowSwivel mirror above exists to avoid, and the same one
            // the foot sweep already paid for once.
            //
            // The harness always hands the leg a real foot target, so `preserveTip` is FALSE in the runtime and
            // it takes the RESPONSIVE (foot-tracked) branch, not the heavy standing floor. These are that
            // branch's constants. If SolveLegs is retuned, retune this too -- it is a MIRROR, not a shared call.
            if (hint != BasisMocapHintSource.TruthJoint)
            {
                BasisSwivelSmootherInput sw = default;
                sw.Root = hip;
                sw.Mid = solvedKnee;
                sw.Tip = r.FootSolved;
                sw.BodyRotation = hipsRot;
                sw.ReferenceLocal = Vector3.forward;   // the knee bulges forward; the arm references down
                sw.FallbackLocal = Vector3.right;
                sw.Dt = dt;
                sw.MinCutoffHz = k_TrackedKneeMinCutoffHz;
                sw.Beta = k_TrackedKneeBeta;
                sw.DerivCutoffHz = k_TrackedKneeDerivCutoffHz;
                sw.ConditionOnPole = true;
                sw.SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
                sw.GuardAnteriorHalfSpace = true;
                sw.AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg;
                sw.AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg;
                sw.State = limb.KneeSwivel;
                sw.Seeded = limb.KneeSwivelSeeded;

                BasisSwivelSmootherCore.Solve(sw, out BasisSwivelSmootherResult sr);
                if (sr.WriteState) { limb.KneeSwivel = sr.State; limb.KneeSwivelSeeded = true; }
                if (sr.Valid) solvedKnee = sr.DesiredMid;
            }
        }

        static void Append(StringBuilder csv, string clip, int f, string limb, float err, Vector3 truth, Vector3 solved, float reach, float effErr, byte axis)
        {
            csv.Append(clip).Append(',').Append(f).Append(',').Append(limb).Append(',')
               .Append(F(err)).Append(',')
               .Append(F(truth.x)).Append(',').Append(F(truth.y)).Append(',').Append(F(truth.z)).Append(',')
               .Append(F(solved.x)).Append(',').Append(F(solved.y)).Append(',').Append(F(solved.z)).Append(',')
               .Append(F(reach)).Append(',').Append(F(effErr)).Append(',').Append(axis).Append('\n');
        }

        static string F(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.######", CultureInfo.InvariantCulture);
        static float Mean(List<float> v) { float t = 0f; for (int i = 0; i < v.Count; i++) t += v[i]; return v.Count > 0 ? t / v.Count : float.NaN; }
        static float Pct(List<float> sorted, float p) => sorted.Count == 0 ? float.NaN : sorted[Mathf.Clamp(Mathf.RoundToInt(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

        public static (bool pass, string reason) Gate(in BasisMocapAccuracySummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Frames <= 0) return (false, "no frames");

            // The hand/foot are COMMANDED. If the solver cannot hit a target it was handed, nothing else here
            // means anything, so this is checked first and hard.
            // The ARM solve ends in a reach-preserving bisection, so it must always land on the hand target it
            // was handed. If it does not, the harness is not driving the solve and nothing below means anything.
            if (s.HandMaxM > 0.01f) return (false, $"hand missed its own target by {s.HandMaxM * 100f:F1} cm -- harness is not driving the solve");

            // The LEG has no such bisection: its hint is applied as a weight-scaled quaternion and is documented
            // as not reach-preserving. So foot slip is a SOLVER PROPERTY to be measured, not a harness fault --
            // but 5 cm of it would be a visible foot slide on a foot tracker, so it is still bounded.
            if (s.FootMaxM > 0.002f)
                return (false, $"the knee hint slid the foot {s.FootMaxM * 1000f:F1} mm off its target -- the leg hint is not reach-preserving");

            if (s.RigidityMaxM > 0.002f)
                return (false, $"the solved rotations rebuild the joint {s.RigidityMaxM * 1000f:F1} mm away from the solved position -- " +
                               "the core's rotation and position outputs disagree");

            if (s.Hint == BasisMocapHintSource.TruthJoint)
            {
                // Handed the real joint, the solver must essentially reproduce it. This is the ceiling, and it
                // also proves the harness is wired correctly.
                if (s.ElbowMeanM > 0.03f) return (false, $"elbow mean {s.ElbowMeanM * 100f:F1} cm even when handed the TRUE elbow -- wiring bug");
                if (s.KneeMeanM > 0.05f) return (false, $"knee mean {s.KneeMeanM * 100f:F1} cm even when handed the TRUE knee -- wiring bug");
            }

            return (true, $"{s.Clip} [{s.Hint}] elbow mean {s.ElbowMeanM * 100f:F1} cm (p95 {s.ElbowP95M * 100f:F1}, max {s.ElbowMaxM * 100f:F1}, " +
                          $"{s.ElbowMeanFracArm * 100f:F1}% of arm) | knee mean {s.KneeMeanM * 100f:F1} cm (p95 {s.KneeP95M * 100f:F1}) | " +
                          $"pops elbow {s.ElbowPops} knee {s.KneePops}");
        }
    }
}
