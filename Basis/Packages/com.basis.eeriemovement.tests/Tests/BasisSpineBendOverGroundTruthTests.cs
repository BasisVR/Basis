using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisSpineBendOverGroundTruthTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.eeriemovement.tests/Tests/MocapCorpus~");
        // The clips in the corpus where a human actually folds up.
        static readonly string[] bendOverClips = { "26_09", "143_11", "69_70", "143_18" };
        static List<BasisMotionClip> LoadBendOverClips()
        {
            if (!Directory.Exists(CorpusDir)) Assert.Ignore($"no mocap corpus at {CorpusDir}");
            var clips = new List<BasisMotionClip>();
            foreach (string name in bendOverClips)
            {
                string path = Path.Combine(CorpusDir, name + ".bvh");
                if (File.Exists(path) && BasisBvhLoader.TryLoad(path, out BasisMotionClip c, out _)) clips.Add(c);
            }
            if (clips.Count == 0) Assert.Ignore("none of the bend-over clips are present in the corpus");
            return clips;
        }
        static Vector3 PelvisForward(BasisMotionClip c, int f)
        {
            Vector3 right = c.Get(f, BasisMocapJoint.RightUpperLeg).Position - c.Get(f, BasisMocapJoint.LeftUpperLeg).Position;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-8f) return Vector3.forward;
            return Vector3.Cross(right.normalized, Vector3.up);   // Unity: Cross(right, up) == forward
        }
        static Vector3 SupportCentre(BasisMotionClip c, int f)
        {
            Vector3 l = c.Get(f, BasisMocapJoint.LeftFoot).Position, r = c.Get(f, BasisMocapJoint.RightFoot).Position;
            return new Vector3(0.5f * (l.x + r.x), 0f, 0.5f * (l.z + r.z));
        }
        [Test]
        public void ARealHumansPelvis_DropsWithTheHead_ByAnAmountThatDependsOnTheMotion()
        {
            List<BasisMotionClip> clips = LoadBendOverClips();
            var report = new StringBuilder();

            report.AppendLine("WHAT A REAL HUMAN DOES WHEN THEY BEND OVER (CMU corpus, bend/pick-up/squat clips)");
            report.AppendLine();
            report.AppendLine("  headDrop   how far the head has fallen below its own standing baseline, metres");
            report.AppendLine("  hipsDrop   how far the pelvis has fallen below ITS standing baseline, metres");
            report.AppendLine("  dHips/dHead   the VERTICAL coupling: metres of pelvis per metre of head.");
            report.AppendLine("             The rig saturates its own version of this exponentially");
            report.AppendLine("             (VSpineHipsMaxDropMeters 0.30, VSpineHipsCompressionStrength 0.85).");
            report.AppendLine();
            report.AppendLine("  headFwd    head's forward offset from the SUPPORT BASE (the midpoint of the feet)");
            report.AppendLine("  hipsFwd    pelvis's forward offset from that same support base");
            report.AppendLine("  dHips/dHead   the HORIZONTAL coupling. THE SIGN IS THE WHOLE QUESTION:");
            report.AppendLine("             the rig ships +0.20 / +0.25 (pelvis lerps TOWARD the head).");
            report.AppendLine("             A NEGATIVE number here means a real pelvis goes the OTHER WAY.");
            report.AppendLine();
            report.AppendLine($"{"clip",-9} {"maxHeadDrop",12} {"maxHipsDrop",12} {"dHips/dHead",12} | " + $"{"maxHeadFwd",11} {"hipsFwd@max",12} {"dHips/dHead",12}");
            report.AppendLine(new string('-', 92));

            // Pooled least-squares slopes through the origin: slope = sum(x*y) / sum(x*x).
            double vNum = 0, vDen = 0, hNum = 0, hDen = 0;
            float worstClipVertSlope = float.MaxValue;
            int deepFrames = 0;

            foreach (BasisMotionClip c in clips)
            {
                int n = c.FrameCount;

                // The standing baseline: the clip's HIGHEST head, i.e. the most upright the person ever is.
                // A median would be dragged down by clips that spend most of their time folded.
                float baseHeadY = float.MinValue, baseHipsY = 0f;
                for (int f = 0; f < n; f++)
                {
                    float hy = c.Get(f, BasisMocapJoint.Head).Position.y;
                    if (hy > baseHeadY) { baseHeadY = hy; baseHipsY = c.Get(f, BasisMocapJoint.Hips).Position.y; }
                }

                double cvNum = 0, cvDen = 0, chNum = 0, chDen = 0;
                float maxHeadDrop = 0f, maxHipsDrop = 0f, maxHeadFwd = 0f, hipsFwdAtMax = 0f;

                for (int f = 0; f < n; f++)
                {
                    Vector3 head = c.Get(f, BasisMocapJoint.Head).Position;
                    Vector3 hips = c.Get(f, BasisMocapJoint.Hips).Position;
                    float headDrop = baseHeadY - head.y, hipsDrop = baseHipsY - hips.y;
                    Vector3 fwd = PelvisForward(c, f), support = SupportCentre(c, f);
                    float headFwd = Vector3.Dot(new Vector3(head.x - support.x, 0f, head.z - support.z), fwd);
                    float hipsFwd = Vector3.Dot(new Vector3(hips.x - support.x, 0f, hips.z - support.z), fwd);

                    // Only regress where the person is ACTUALLY BENT. Near-upright frames carry no signal and
                    // their noise would dominate a through-origin slope.
                    if (headDrop > 0.10f)
                    {
                        cvNum += headDrop * hipsDrop; cvDen += headDrop * headDrop;
                        chNum += headFwd * hipsFwd; chDen += headFwd * headFwd;
                        deepFrames++;
                    }

                    if (headDrop > maxHeadDrop) maxHeadDrop = headDrop;
                    if (hipsDrop > maxHipsDrop) maxHipsDrop = hipsDrop;
                    if (headFwd > maxHeadFwd) { maxHeadFwd = headFwd; hipsFwdAtMax = hipsFwd; }
                }

                float vSlope = cvDen > 1e-9 ? (float)(cvNum / cvDen) : float.NaN;
                float hSlope = chDen > 1e-9 ? (float)(chNum / chDen) : float.NaN;
                vNum += cvNum; vDen += cvDen; hNum += chNum; hDen += chDen;
                if (!float.IsNaN(vSlope) && vSlope < worstClipVertSlope) worstClipVertSlope = vSlope;

                report.AppendLine($"{c.Name,-9} {maxHeadDrop,12:F3} {maxHipsDrop,12:F3} {vSlope,12:F3} | " + $"{maxHeadFwd,11:F3} {hipsFwdAtMax,12:F3} {hSlope,12:F3}");
            }

            float pooledVert = vDen > 1e-9 ? (float)(vNum / vDen) : float.NaN;
            float pooledHoriz = hDen > 1e-9 ? (float)(hNum / hDen) : float.NaN;

            report.AppendLine(new string('-', 92));
            report.AppendLine($"POOLED   vertical dHips/dHead = {pooledVert:F3}    horizontal dHips/dHead = {pooledHoriz:F3}");
            report.AppendLine($"         ({deepFrames} frames with the head more than 10 cm below its standing height)");
            report.AppendLine();
            report.AppendLine("WHAT THE RIG DOES TODAY, for comparison:");
            report.AppendLine($"  vertical   : lerp(drop, 0.30*(1-exp(-drop/0.30)), 0.85) -- see the table below");
            for (float d = 0.1f; d <= 0.71f; d += 0.2f)
            {
                float soft = 0.30f * (1f - Mathf.Exp(-d / 0.30f)), got = Mathf.Lerp(d, soft, 0.85f);
                report.AppendLine($"               head drops {d:F2} m -> pelvis drops {got:F3} m  ({got / d * 100f:F0}% of rigid)");
            }
            report.AppendLine($"  horizontal : +0.20 (feet tracked) / +0.25 -- pelvis lerped TOWARD the head");
            report.AppendLine();

            Debug.Log(report.ToString());

            // ------------------------------------------------------------------------------------------
            // The assertions. Only what the data says without room for argument.
            // ------------------------------------------------------------------------------------------
            Assert.IsFalse(float.IsNaN(pooledVert), "no bent frames were found -- the measurement did not run");

            Assert.Greater(pooledVert, 0.30f, $"a real pelvis DROPS when the head drops (measured {pooledVert:F3} m per m). If this is near zero " +"the corpus is not doing what the clip names say and the rest of this file is meaningless.");

            // ==========================================================================================
            // ⚠ THE HORIZONTAL SIGN IS *NOT* ASSERTED, AND THE REASON IS WORTH MORE THAN THE ASSERTION.
            //
            // I expected this to come out NEGATIVE -- a bending human's pelvis slides back over the heels to
            // keep the mass over the feet, so the rig lerping it TOWARD the head at +0.20/+0.25 looked like a
            // plain sign error. The corpus says otherwise, and it says it loudly: the per-clip slopes are
            //
            //      26_09 -0.250   143_11 -0.505   69_70 -0.724   143_18 +0.988
            //
            // Three strongly negative, one strongly positive, pooling to a meaningless +0.10. That is not
            // noise -- it is the metric pooling MOTIONS THAT ARE NOT THE SAME MOTION. A waist-bend counter-
            // balances backward; a squat stays stacked over the feet and the pelvis tracks the head forward.
            // A single through-origin slope over both is a number with no referent, and asserting on it would
            // have been asserting on an artefact.
            //
            // So the honest finding is not "the sign is wrong". It is: THERE IS NO SINGLE CORRECT CONSTANT
            // HERE, and the rig ships one anyway, applied with no knowledge of which motion is happening.
            // The `hipsFwd@max` column is the informative one -- at the frame where the head is furthest
            // forward, the pelvis is BEHIND the support base on 3 of the 4 clips (-6.5, -11.4, -16.2 cm) and
            // ahead of it only on the squat (+11.7 cm). The discriminator is the head's forward travel, which
            // is precisely the input the vertical law never looks at.
            //
            // Fix the vertical coupling first (see the sibling test -- it is worth 32.8 cm). Do not touch this
            // constant until there is a measurement that separates the motions.
            // ==========================================================================================
            Assert.IsFalse(float.IsNaN(pooledHoriz), "the horizontal measurement did not run");
        }
        [Test]
        public void TheRigsCompressionLaw_LeavesTheNeckHoldingAGap_OnRealBendOvers()
        {
            List<BasisMotionClip> clips = LoadBendOverClips();

            // The shipped law: BasisVirtualSpineCore.ComputeHipsPosition, with the shipped defaults.
            const float maxDrop = 0.30f;        // VSpineHipsMaxDropMeters
            const float strength = 0.85f;       // VSpineHipsCompressionStrength

            float worstGap = 0f;
            string worstWhere = "";
            var report = new StringBuilder();
            report.AppendLine("THE GAP THE NECK IS LEFT HOLDING (rig's pelvis drop vs the human's, on the same head drop)");
            report.AppendLine($"{"clip",-9} {"headDrop m",11} {"human hips",11} {"rig hips",10} {"GAP m",8}");
            report.AppendLine(new string('-', 54));

            foreach (BasisMotionClip c in clips)
            {
                int n = c.FrameCount;
                float baseHeadY = float.MinValue, baseHipsY = 0f;
                for (int f = 0; f < n; f++)
                {
                    float hy = c.Get(f, BasisMocapJoint.Head).Position.y;
                    if (hy > baseHeadY) { baseHeadY = hy; baseHipsY = c.Get(f, BasisMocapJoint.Hips).Position.y; }
                }

                float clipWorst = 0f, atHead = 0f, atHuman = 0f, atRig = 0f;
                for (int f = 0; f < n; f++)
                {
                    float headDrop = baseHeadY - c.Get(f, BasisMocapJoint.Head).Position.y;
                    if (headDrop <= 0.10f) continue;

                    float humanHips = baseHipsY - c.Get(f, BasisMocapJoint.Hips).Position.y;

                    // The rig's pelvis is driven from the NECK, rigidly, then compressed. The rigid drop the
                    // law is handed is the head/neck drop; this is that law, verbatim.
                    float soft = maxDrop * (1f - Mathf.Exp(-headDrop / maxDrop));
                    float rigHips = Mathf.Lerp(headDrop, soft, strength);

                    float gap = humanHips - rigHips;   // positive => the human's pelvis is LOWER than the rig's
                    if (gap > clipWorst) { clipWorst = gap; atHead = headDrop; atHuman = humanHips; atRig = rigHips; }
                }

                report.AppendLine($"{c.Name,-9} {atHead,11:F3} {atHuman,11:F3} {atRig,10:F3} {clipWorst,8:F3}");
                if (clipWorst > worstGap) { worstGap = clipWorst; worstWhere = c.Name; }
            }

            report.AppendLine(new string('-', 54));
            report.AppendLine($"WORST GAP (legacy law): {worstGap * 100f:F1} cm, on {worstWhere}.");
            report.AppendLine("That is how far the pelvis was being held ABOVE where a real one goes, on a bend");
            report.AppendLine("this deep -- and since the head is welded to the HMD, the spine and neck had to");
            report.AppendLine("find every centimetre of it by ROTATING. That is the tortoise, in centimetres.");
            report.AppendLine();

            // ---------------------------------------------------------------------------------------------
            // AND NOW THE FIX, ON THE SAME FRAMES. BasisPelvisPostureModel replaced the law above; this is
            // the guard that says so, in the same units, on the same clips, so the claim cannot rot.
            // ---------------------------------------------------------------------------------------------
            float worstModelGap = 0f;
            foreach (BasisMotionClip c in clips)
            {
                BasisPostureFeatures.StandingReference(c, out float sHead, out float sHips);
                if (!(sHead > 0.1f)) continue;

                for (int f = 0; f < c.FrameCount; f++)
                {
                    BasisPostureSample p = BasisPostureFeatures.Extract(c, f, sHead, sHips);
                    if (!p.Valid || p.HeadDrop <= 0.10f) continue;

                    float modelHips = BasisPelvisPostureModel.PelvisDrop(p.HeadDrop, p.HeadFwd) * sHead;
                    float humanHips = p.HipsDrop * sHead;
                    worstModelGap = Mathf.Max(worstModelGap, humanHips - modelHips);
                }
            }

            report.AppendLine($"WORST GAP (posture model): {worstModelGap * 100f:F1} cm -- on the same frames.");
            Debug.Log(report.ToString());

            Assert.Greater(worstGap, 0.05f, "the shipped saturation law held the pelvis measurably above a real human's on a deep bend. " +"If this no longer holds, the legacy law has been changed and the premise of the fix has moved.");

            Assert.Less(worstModelGap, worstGap, $"BasisPelvisPostureModel must close the gap the saturation law opened -- legacy {worstGap * 100f:F1} cm, " + $"model {worstModelGap * 100f:F1} cm. This is the tortoise, and this assertion is what stops it coming back.");
        }
    }
}
