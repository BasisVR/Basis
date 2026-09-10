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
    public struct BasisMocapAccuracySummary
    {
        public bool Ok;
        public string Error, Clip, Path;
        public BasisMocapHintSource Hint;
        public int Frames;
        public float ElbowMeanM, ElbowP95M, ElbowMaxM;
        public float KneeMeanM, KneeP95M, KneeMaxM;
        public float ElbowMeanFracArm;   // scale-free: error / arm length
        public float KneeMeanFracLeg;
        // Sanity: we COMMAND the hand and foot, so the solver must hit them. If these are not ~0 the harness
        // is not driving the solver properly and every other number here is meaningless.
        public float HandMaxM, HandInReachMaxM, FootMaxM;
        // The cores report a solved pose BOTH as positions and as rotations. Rebuilding the joint from the
        // reported rotations over fixed bone lengths must reproduce the reported position, or the two answers
        // disagree and the temporal carry (which rides the rotations) is quietly solving a different limb.
        public float RigidityMaxM;
        // Pole flips measured on REAL human motion: the elbow jumps while the hand barely moves.
        public int ElbowPops, KneePops;
    }
    public static class BasisMocapAccuracy
    {
        // A pole flip: the joint jumps hard while the end effector is essentially still. Real human motion is
        // smooth, so any such jump is the solver's doing, not the human's.
        const float hipSpringHz = 8f, hipSpringDamping = 1f, popJointM = 0.05f;
        public static System.Text.StringBuilder legDump;
        const float popEffectorM = 0.01f; // while the hand/foot moved under 1 cm
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
        const float trackedKneeMinCutoffHz = 1.5f, trackedKneeBeta = 0.20f, trackedKneeDerivCutoffHz = 1.0f;
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
        static void AnimatedPose(in Limb l, Quaternion parentRot, Vector3 root, out Quaternion rootRot, out Quaternion midRot, out Quaternion tipRot, out Vector3 mid, out Vector3 tip)
        {
            rootRot = parentRot * l.RootLocal;
            midRot = rootRot * l.MidLocal;
            tipRot = midRot * l.TipLocal;
            mid = root + (rootRot * l.UpperDirLocal) * l.UpperLen;
            tip = mid + (midRot * l.LowerDirLocal) * l.LowerLen;
        }
        // Rebuild a joint from a pair of solved rotations over the fixed bone lengths.
        static Vector3 RebuildMid(in Limb l, Vector3 root, Quaternion rootRot) => root + (rootRot * l.UpperDirLocal) * l.UpperLen;
        public sealed class BasisMocapTracks
        {
            public float Dt, ArmLen, LegLen;
            public Vector3[] TruthElbow, SolvedElbow;
            public Vector3[] TruthKnee, SolvedKnee;
            public Vector3[] TruthHand, TruthFoot;
            public float[] KneeReach;
            public byte[] KneeAxis;
        }
        public static BasisMocapAccuracySummary Run(BasisMotionClip clip, BasisMocapHintSource hint, string csvPath) => Run(clip, hint, csvPath, null);
        public static BasisMocapAccuracySummary Run(BasisMotionClip clip, BasisMocapHintSource hint, string csvPath, BasisMocapTracks tracks)
        {
            var s = new BasisMocapAccuracySummary { Hint = hint, Path = csvPath };

            if (clip == null || clip.FrameCount < 2) { s.Error = "clip too short"; return s; }
            if (!BasisBvhLoader.Validate(clip, out string why)) { s.Error = why; return s; }
            s.Clip = clip.Name;

            try
            {
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
                    tracks.KneeReach = new float[n]; tracks.KneeAxis = new byte[n];
                }

                var arms = new Limb[2];
                var legs = new Limb[2];

                var armStates = new BasisArmState[2];
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
                        BasisHipFrameSpringCore.Step(hipSpringRot, hipSpringVel, hipsRot, dt, hipSpringHz, hipSpringDamping, out hipSpringRot, out hipSpringVel);
                    }

                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        SolveArm(clip, f, isLeft, ref arms[side], chestRot, hint, dt, ref armStates[side], out Vector3 truthElbow, out Vector3 solvedElbow, out float handErr, out float reach, out float aLen, out float armRigid, out byte armAxis, out bool inReach);
                        if (inReach) s.HandInReachMaxM = Mathf.Max(s.HandInReachMaxM, handErr);
                        armLen = aLen;
                        s.RigidityMaxM = Mathf.Max(s.RigidityMaxM, armRigid);
                        float e = Vector3.Distance(truthElbow, solvedElbow);
                        elbowErr.Add(e);
                        s.HandMaxM = Mathf.Max(s.HandMaxM, handErr);
                        Append(csv, clip.Name, f, isLeft ? "leftArm" : "rightArm", e, truthElbow, solvedElbow, reach, handErr, armAxis);

                        if (side == 0 && f > 0)
                        {
                            Vector3 hand = clip.Get(f, BasisMocapJoint.LeftHand).Position;
                            if (Vector3.Distance(solvedElbow, prevElbow0) > popJointM && Vector3.Distance(hand, prevHand0) < popEffectorM) s.ElbowPops++;
                            prevHand0 = hand;
                        }
                        if (side == 0) { prevElbow0 = solvedElbow; if (f == 0) prevHand0 = clip.Get(0, BasisMocapJoint.LeftHand).Position; }

                        SolveLeg(clip, f, isLeft, ref legs[side], hipsRot, hint, dt, out Vector3 truthKnee, out Vector3 solvedKnee, out float footErr, out float lReach, out float lLen, out float legRigid, out byte legAxis);
                        legLen = lLen;
                        s.RigidityMaxM = Mathf.Max(s.RigidityMaxM, legRigid);
                        float k = Vector3.Distance(truthKnee, solvedKnee);
                        kneeErr.Add(k);
                        s.FootMaxM = Mathf.Max(s.FootMaxM, footErr);
                        Append(csv, clip.Name, f, isLeft ? "leftLeg" : "rightLeg", k, truthKnee, solvedKnee, lReach, footErr, legAxis);

                        if (side == 0 && f > 0)
                        {
                            Vector3 foot = clip.Get(f, BasisMocapJoint.LeftFoot).Position;
                            if (Vector3.Distance(solvedKnee, prevKnee0) > popJointM && Vector3.Distance(foot, prevFoot0) < popEffectorM) s.KneePops++;
                            prevFoot0 = foot;
                        }
                        if (side == 0) { prevKnee0 = solvedKnee; if (f == 0) prevFoot0 = clip.Get(0, BasisMocapJoint.LeftFoot).Position; }

                        if (side == 0 && tracks != null)
                        {
                            tracks.TruthElbow[f] = truthElbow; tracks.SolvedElbow[f] = solvedElbow;
                            tracks.TruthKnee[f] = truthKnee; tracks.SolvedKnee[f] = solvedKnee;
                            tracks.TruthHand[f] = clip.Get(f, BasisMocapJoint.LeftHand).Position;
                            tracks.TruthFoot[f] = clip.Get(f, BasisMocapJoint.LeftFoot).Position;
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
        }
        static void SolveArm(BasisMotionClip clip, int f, bool isLeft, ref Limb limb, Quaternion chestRot, BasisMocapHintSource hint, float dt, ref BasisArmState state, out Vector3 truthElbow, out Vector3 solvedElbow, out float handErr, out float reach, out float armLen, out float rigidity, out byte axis, out bool inReach)
        {
            BasisMocapJoint jS = isLeft ? BasisMocapJoint.LeftUpperArm : BasisMocapJoint.RightUpperArm;
            BasisMocapJoint jE = isLeft ? BasisMocapJoint.LeftLowerArm : BasisMocapJoint.RightLowerArm;
            BasisMocapJoint jH = isLeft ? BasisMocapJoint.LeftHand : BasisMocapJoint.RightHand;
            Vector3 shoulder = clip.Get(f, jS).Position;
            truthElbow = clip.Get(f, jE).Position;
            Vector3 truthHand = clip.Get(f, jH).Position;
            Quaternion truthHandRot = clip.Get(f, jH).Rotation;
            armLen = Vector3.Distance(shoulder, truthElbow) + Vector3.Distance(truthElbow, truthHand);
            if (!limb.Seeded) SeedLimb(ref limb, chestRot, shoulder, truthElbow, truthHand, clip.Get(f, jS).Rotation, clip.Get(f, jE).Rotation, clip.Get(f, jH).Rotation);
            AnimatedPose(limb, chestRot, shoulder, out Quaternion animRootRot, out Quaternion animMidRot, out Quaternion animTipRot, out Vector3 elbow, out Vector3 hand);
            Vector3 lSh = clip.Get(f, BasisMocapJoint.LeftUpperArm).Position, rSh = clip.Get(f, BasisMocapJoint.RightUpperArm).Position;
            Vector3 hipsP = clip.Get(f, BasisMocapJoint.Hips).Position, neckP = clip.Get(f, BasisMocapJoint.Neck).Position;
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(lSh, rSh, hipsP, neckP);
            BasisArmSolveInput i = BasisArmSolveInput.Defaults(isLeft);
            i.Shoulder = shoulder;
            i.RestElbow = elbow;
            i.RestHand = hand;
            i.RestHandRotation = animTipRot;
            i.TargetPosition = truthHand;
            i.TargetRotation = truthHandRot;
            i.HasHead = true;
            i.HeadPosition = clip.Get(f, BasisMocapJoint.Head).Position;
            if (frame.Valid)
            {
                i.TorsoUp = frame.Up;
                i.TorsoForward = frame.Forward;
                i.TorsoOut = isLeft ? -frame.Right : frame.Right;
            }
            else
            {
                i.TorsoUp = chestRot * Vector3.up;
                i.TorsoForward = chestRot * Vector3.forward;
                i.TorsoOut = chestRot * (isLeft ? Vector3.left : Vector3.right);
            }
            i.Dt = dt;
            if (hint == BasisMocapHintSource.TruthJoint)
            {
                i.HasHint = true;
                i.HintPosition = truthElbow;
                i.HasHintRotation = true;
                i.HintRotation = clip.Get(f, jE).Rotation;
            }
            BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
            solvedElbow = r.Valid ? r.Elbow : elbow;
            handErr = r.Valid ? Vector3.Distance(r.Hand, truthHand) : Vector3.Distance(hand, truthHand);
            reach = r.ReachRatio;
            inReach = r.ReachRatio < 0.9f;
            axis = 0;
            rigidity = 0f;
            if (r.Valid)
            {
                BasisArmSolveCore.Pose(i, r, animRootRot, animMidRot, out Quaternion upperRot, out _);
                rigidity = Vector3.Distance(RebuildMid(limb, shoulder, upperRot), r.Elbow);
            }
        }
        static void SolveLeg(BasisMotionClip clip, int f, bool isLeft, ref Limb limb, Quaternion hipsRot, BasisMocapHintSource hint, float dt, out Vector3 truthKnee, out Vector3 solvedKnee, out float footErr, out float reach, out float legLen, out float rigidity, out byte axis)
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
            Vector3 chestP = clip.Get(f, BasisMocapJoint.Chest).Position, gUp = (chestP - hipsP).normalized;
            Vector3 gRight = (rHip - lHip);
            gRight = (gRight - gUp * Vector3.Dot(gRight, gUp)).normalized;
            Vector3 gFwd = Vector3.Cross(gRight, gUp), gOut = isLeft ? -gRight : gRight, h2f = truthFoot - hip;
            var legLocal = new Unity.Mathematics.float3(Vector3.Dot(h2f, gOut) / legLen, Vector3.Dot(h2f, gUp) / legLen, Vector3.Dot(h2f, gFwd) / legLen);

            // A knee points FORWARD, so that is the swivel's zero. (The arm's is body-down.)
            if (hint == BasisMocapHintSource.TruthJoint)
            {
                i.HintPosition = truthKnee;
                i.HintWeight = 1f;
            }
            else if (hint == BasisMocapHintSource.Model || hint == BasisMocapHintSource.NeuralSwivel)
            {
                // SwivelModel/SwivelModelSmoothed share the polynomial knee (the smoothing varies only the ARM's
                // output filter); NeuralSwivel swaps in the neural knee. Same features, same (sin,cos), same
                // BendDirection -- so the knee column A/Bs BasisLegSwivelNeuralModel against BasisLegSwivelModel.
                float conf;
                float kneeSwivel = hint == BasisMocapHintSource.NeuralSwivel ? BasisLegSwivelNeuralModel.SwivelRad(legLocal, out conf)   // POSITIONS ONLY -- see BasisLegSwivelNeuralModel
                    : BasisLegSwivelModel.SwivelRad(legLocal, out conf);        // POSITIONS ONLY -- see BasisLegSwivelModel
                if (isLeft) kneeSwivel = -kneeSwivel;
                Unity.Mathematics.float3 kb = BasisLegSwivelModel.BendDirection(new Unity.Mathematics.float3(h2f.x, h2f.y, h2f.z), new Unity.Mathematics.float3(gOut.x, gOut.y, gOut.z), kneeSwivel);
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

            if (legDump != null && hint != BasisMocapHintSource.NeuralSwivel)
            {
                Vector3 ax = h2f.normalized, uu = (gOut - ax * Vector3.Dot(gOut, ax)).normalized;
                Vector3 vv = Vector3.Cross(ax, uu), rp = truthKnee - hip;
                rp -= ax * Vector3.Dot(rp, ax);
                float trueSw = Mathf.Atan2(Vector3.Dot(rp, vv), Vector3.Dot(rp, uu));
                float phiFit = isLeft ? -trueSw : trueSw, dist = Mathf.Clamp(h2f.magnitude, 1e-6f, legLen - 1e-6f);
                float aa = (limb.UpperLen * limb.UpperLen - limb.LowerLen * limb.LowerLen + dist * dist) / (2f * dist);
                float rad = Mathf.Sqrt(Mathf.Max(limb.UpperLen * limb.UpperLen - aa * aa, 0f)) / legLen;

                // The RAW knee position in the same mirrored body frame (ex,ey,ez), for the position-target
                // model: predict a 3-vector and project onto the reachable circle. Taken DIRECTLY from
                // truthKnee -- reconstructing it from phi would re-inject the (uu,vv) reference singularity.
                Vector3 k2h = truthKnee - hip;
                float ex = Vector3.Dot(k2h, gOut) / legLen, ey = Vector3.Dot(k2h, gUp) / legLen;
                float ez = Vector3.Dot(k2h, gFwd) / legLen;

                var sb = legDump;
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
                sw.MinCutoffHz = trackedKneeMinCutoffHz;
                sw.Beta = trackedKneeBeta;
                sw.DerivCutoffHz = trackedKneeDerivCutoffHz;
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
            csv.Append(clip).Append(',').Append(f).Append(',').Append(limb).Append(',').Append(F(err)).Append(',').Append(F(truth.x)).Append(',').Append(F(truth.y)).Append(',').Append(F(truth.z)).Append(',').Append(F(solved.x)).Append(',').Append(F(solved.y)).Append(',').Append(F(solved.z)).Append(',').Append(F(reach)).Append(',').Append(F(effErr)).Append(',').Append(axis).Append('\n');
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
            if (s.HandInReachMaxM > 0.002f) return (false, $"hand missed its own target by {s.HandInReachMaxM * 1000f:F1} mm inside reach -- harness is not driving the solve");
            if (s.HandMaxM > 0.05f) return (false, $"hand fell {s.HandMaxM * 100f:F1} cm short of its target near full extension");

            // The LEG has no such bisection: its hint is applied as a weight-scaled quaternion and is documented
            // as not reach-preserving. So foot slip is a SOLVER PROPERTY to be measured, not a harness fault --
            // but 5 cm of it would be a visible foot slide on a foot tracker, so it is still bounded.
            if (s.FootMaxM > 0.002f)
                return (false, $"the knee hint slid the foot {s.FootMaxM * 1000f:F1} mm off its target -- the leg hint is not reach-preserving");

            if (s.RigidityMaxM > 0.002f)
                return (false, $"the solved rotations rebuild the joint {s.RigidityMaxM * 1000f:F1} mm away from the solved position -- " +"the core's rotation and position outputs disagree");

            if (s.Hint == BasisMocapHintSource.TruthJoint)
            {
                // Handed the real joint, the solver must essentially reproduce it. This is the ceiling, and it
                // also proves the harness is wired correctly.
                if (s.ElbowMeanM > 0.03f) return (false, $"elbow mean {s.ElbowMeanM * 100f:F1} cm even when handed the TRUE elbow -- wiring bug");
                if (s.KneeMeanM > 0.05f) return (false, $"knee mean {s.KneeMeanM * 100f:F1} cm even when handed the TRUE knee -- wiring bug");
            }

            return (true, $"{s.Clip} [{s.Hint}] elbow mean {s.ElbowMeanM * 100f:F1} cm (p95 {s.ElbowP95M * 100f:F1}, max {s.ElbowMaxM * 100f:F1}, " + $"{s.ElbowMeanFracArm * 100f:F1}% of arm) | knee mean {s.KneeMeanM * 100f:F1} cm (p95 {s.KneeP95M * 100f:F1}) | " + $"pops elbow {s.ElbowPops} knee {s.KneePops}");
        }
    }
}
