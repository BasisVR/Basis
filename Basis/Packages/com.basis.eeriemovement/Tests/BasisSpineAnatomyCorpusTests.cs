using System.Collections.Generic;
using System.IO;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// IS THE ENVELOPE RIGHT? — the anatomical ROM table, measured against 35,081 frames of real humans.
    ///
    /// The unit tests next door (BasisSpineAnatomyTests) prove the guard's MATHS: that it cannot be escaped,
    /// that it is the identity on legal poses, that it does not pop. They cannot tell you whether the NUMBERS
    /// in the table are the right numbers. That is what the corpus is for, and it is a different kind of
    /// question -- not "is the code correct" but "is the anatomy correct".
    ///
    /// THE RULE THAT KEEPS THIS HONEST, AND IT RUNS THE OPPOSITE WAY TO EVERY OTHER FIT IN THIS PROJECT:
    /// the limits are NOT fitted to the corpus. They are max-voluntary-ROM from the clinical literature --
    /// what a spine CAN do -- and the corpus gets a VETO ONLY. Motion capture records what people DID, and
    /// CMU subjects walk around a lab; they do not side-bend to end range. Fit the limit to that and you
    /// encode "didn't" as "can't", then clip the first VR user who leans over to look at something.
    ///
    /// So the acceptance test is: the guard must fire on almost NONE of real human motion, and everything it
    /// DOES touch must be past the 99th percentile -- the anatomically-impossible tail that CMU's 3-joint
    /// spine produces as retargeting slop (a 35 deg lumbar twist; a 100 deg lumbar flexion). If the guard
    /// were firing in the fat of the distribution, the table would be wrong and this would fail.
    ///
    /// It exercises the SHIPPING functions -- BasisSpineAnatomy.BuildRestFrame, .Decompose, .Clamp -- on real
    /// poses, so it is also a live regression guard on the exact code the rig runs, not a re-implementation.
    /// </summary>
    public sealed class BasisSpineAnatomyCorpusTests
    {
        static string MainDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~");
        static string PostureDir => Path.Combine(MainDir, "posture");

        // Every clip a segment fires on more than this fraction of is treated as the table being too tight.
        // The Python pre-measurement put every segment under 0.6%; 2% is generous headroom for the handful of
        // clips that are ALL deep bends (the posture corpus was chosen to stress exactly this).
        const float k_MaxFireFraction = 0.03f;

        // A spine segment, and where its three neighbours sit in the mocap chain Hips->Spine->Chest->
        // UpperChest->Neck->Head. `Bone` is the joint that rotates; `Child` is one step toward the head
        // (its long axis); `Parent` is one step toward the hips (the frame its local rotation lives in).
        struct Seg
        {
            public BasisSpineSegment Kind;
            public BasisMocapJoint Bone, Child, Parent;
            public string Label;
        }

        // THE UPPER THORACIC IS DELIBERATELY ABSENT. In the CMU skeleton the Neck joint sits at ZERO offset
        // from Spine1 (the upper-chest joint) -- they are the same point -- so UpperChest has no measurable
        // long axis in this corpus, and routing the axis up through the coincident neck instead picks up the
        // neck's authored forward tilt and reads a phantom 27 deg of flexion. CMU cannot cleanly measure this
        // segment. Its ROM (BasisSpineAnatomy) is still held by the unit tests (the envelope cannot be
        // escaped) and by the literature; it simply does not get a corpus veto from a corpus that cannot see
        // it. The real Unity rig HAS a genuine upperChest->neck length, so it measures it fine at runtime.
        //
        // The three that remain are exactly the ones the user's complaints live in: the lumbar and the chest
        // (the trunk that must fold and follow the head down) and the neck (the crane).
        static readonly Seg[] k_Segs =
        {
            new Seg { Kind = BasisSpineSegment.Lumbar,        Bone = BasisMocapJoint.Spine, Child = BasisMocapJoint.Chest,      Parent = BasisMocapJoint.Hips,       Label = "lumbar    (Spine)" },
            new Seg { Kind = BasisSpineSegment.LowerThoracic, Bone = BasisMocapJoint.Chest, Child = BasisMocapJoint.UpperChest, Parent = BasisMocapJoint.Spine,      Label = "lo-thorac (Chest)" },
            new Seg { Kind = BasisSpineSegment.Cervical,      Bone = BasisMocapJoint.Neck,  Child = BasisMocapJoint.Head,       Parent = BasisMocapJoint.UpperChest, Label = "cervical  (Neck)" },
        };

        static List<BasisMotionClip> LoadAll()
        {
            var clips = new List<BasisMotionClip>();
            foreach (string dir in new[] { MainDir, PostureDir })
            {
                if (!Directory.Exists(dir)) continue;
                string[] files = Directory.GetFiles(dir, "*.bvh");
                System.Array.Sort(files);
                foreach (string f in files)
                {
                    if (BasisBvhLoader.TryLoad(f, out BasisMotionClip c, out _)) clips.Add(c);
                }
            }
            if (clips.Count == 0) Assert.Ignore($"no corpus at {MainDir}");
            return clips;
        }

        // The clip's most neutral frame: the trunk bones' long axes closest to straight up, all at once.
        // At this frame the spine is at rest, so BuildRestFrame captures the true anatomical neutral -- which
        // is what makes every downstream angle a deviation FROM rest, exactly as the live rig sees it.
        static int StraightestFrame(BasisMotionClip c)
        {
            int best = 0;
            float bestScore = float.MaxValue;
            for (int f = 0; f < c.FrameCount; f++)
            {
                float score = 0f;
                bool ok = true;
                foreach (Seg s in k_Segs)
                {
                    if (s.Kind == BasisSpineSegment.Cervical) continue;   // the neck has a real rest tilt; do not chase it
                    BasisMocapPose bone = c.Get(f, s.Bone);
                    BasisMocapPose child = c.Get(f, s.Child);
                    if (!bone.Valid || !child.Valid) { ok = false; break; }
                    Vector3 up = (child.Position - bone.Position).normalized;
                    score += 1f - up.y;   // 0 when the bone points straight up
                }
                if (ok && score < bestScore) { bestScore = score; best = f; }
            }
            return best;
        }

        static bool TryRestFrame(BasisMotionClip c, int f, Seg s, out BasisSpineRestFrame frame)
        {
            frame = default;
            BasisMocapPose bone = c.Get(f, s.Bone);
            BasisMocapPose child = c.Get(f, s.Child);
            BasisMocapPose parent = c.Get(f, s.Parent);
            BasisMocapPose lsh = c.Get(f, BasisMocapJoint.LeftUpperArm);
            BasisMocapPose rsh = c.Get(f, BasisMocapJoint.RightUpperArm);
            if (!bone.Valid || !child.Valid || !parent.Valid || !lsh.Valid || !rsh.Valid) return false;

            frame = BasisSpineAnatomy.BuildRestFrame(
                bone.Position, child.Position, bone.Rotation, parent.Rotation, rsh.Position - lsh.Position);
            return frame.Valid;
        }

        static float Pct(List<float> xs, float p)
        {
            if (xs.Count == 0) return 0f;
            xs.Sort();
            int i = Mathf.Clamp(Mathf.RoundToInt(p / 100f * (xs.Count - 1)), 0, xs.Count - 1);
            return xs[i];
        }

        [Test]
        public void TheEnvelope_NeverFiresOnTheFatOfRealHumanMotion()
        {
            List<BasisMotionClip> clips = LoadAll();

            foreach (Seg s in k_Segs)
            {
                var flex = new List<float>();
                var lat = new List<float>();
                var axial = new List<float>();
                int frames = 0, fired = 0;
                float worstClipFire = 0f;
                string worstClip = "";
                BasisSpineRom rom = BasisSpineAnatomy.Rom(s.Kind);

                foreach (BasisMotionClip c in clips)
                {
                    int rf = StraightestFrame(c);
                    if (!TryRestFrame(c, rf, s, out BasisSpineRestFrame frame)) continue;

                    int clipFrames = 0, clipFired = 0;
                    for (int f = 0; f < c.FrameCount; f++)
                    {
                        BasisMocapPose bone = c.Get(f, s.Bone);
                        BasisMocapPose parent = c.Get(f, s.Parent);
                        if (!bone.Valid || !parent.Valid) continue;

                        Quaternion local = BasisSpineAnatomyCore.Conj(parent.Rotation) * bone.Rotation;
                        BasisSpineAnatomyCore.Decompose(local * BasisSpineAnatomyCore.Conj(frame.RestLocalRot),
                            frame, out float fl, out float la, out float ax);
                        flex.Add(fl); lat.Add(Mathf.Abs(la)); axial.Add(Mathf.Abs(ax));

                        BasisSpineAnatomyCore.Clamp(local, frame, rom, out BasisSpineClampInfo info);
                        frames++; clipFrames++;
                        if (info.Touched) { fired++; clipFired++; }
                    }

                    if (clipFrames > 0)
                    {
                        float frac = (float)clipFired / clipFrames;
                        if (frac > worstClipFire) { worstClipFire = frac; worstClip = c.Name; }
                    }
                }

                Assert.Greater(frames, 1000, $"{s.Label}: too few frames to trust ({frames})");
                float fireFrac = (float)fired / frames;

                Debug.Log($"[spine ROM] {s.Label}  fire {100f * fireFrac:F3}% over {frames} frames  " +
                          $"(worst clip {100f * worstClipFire:F1}% = {worstClip})  |  " +
                          $"real p99 flex {Pct(flex, 99):F1}  ext {-Pct(flex, 1):F1}  lat {Pct(lat, 99):F1}  " +
                          $"axial {Pct(axial, 99):F1}   vs table {rom.FlexDeg}/{rom.ExtDeg}/{rom.LatDeg}/{rom.AxialDeg}");

                // THE ACCEPTANCE TEST. A limit that fires on the body of real motion is too tight and would
                // clip a real VR user. Under 3% corpus-wide is "only the impossible tail".
                Assert.Less(fireFrac, k_MaxFireFraction,
                    $"{s.Label}: the envelope fired on {100f * fireFrac:F2}% of REAL human motion -- the table is too tight");
            }
        }

        [Test]
        public void EverythingTheGuardTouches_IsPastThe99thPercentile_ItOnlyClipsTheImpossibleTail()
        {
            List<BasisMotionClip> clips = LoadAll();

            foreach (Seg s in k_Segs)
            {
                BasisSpineRom rom = BasisSpineAnatomy.Rom(s.Kind);
                var swingMag = new List<float>();     // every frame's swing radius as a fraction of the ellipse
                var axialMag = new List<float>();

                foreach (BasisMotionClip c in clips)
                {
                    int rf = StraightestFrame(c);
                    if (!TryRestFrame(c, rf, s, out BasisSpineRestFrame frame)) continue;

                    for (int f = 0; f < c.FrameCount; f++)
                    {
                        BasisMocapPose bone = c.Get(f, s.Bone);
                        BasisMocapPose parent = c.Get(f, s.Parent);
                        if (!bone.Valid || !parent.Valid) continue;

                        Quaternion local = BasisSpineAnatomyCore.Conj(parent.Rotation) * bone.Rotation;
                        BasisSpineAnatomyCore.Decompose(local * BasisSpineAnatomyCore.Conj(frame.RestLocalRot),
                            frame, out float fl, out float la, out float ax);

                        float lim = fl >= 0f ? rom.FlexDeg : rom.ExtDeg;
                        swingMag.Add(Mathf.Sqrt((fl / lim) * (fl / lim) + (la / rom.LatDeg) * (la / rom.LatDeg)));
                        axialMag.Add(Mathf.Abs(ax) / rom.AxialDeg);
                    }
                }

                // The guard's soft threshold is 1.0 (on the ellipse / at the axial limit). If the 99th
                // percentile of real motion is already below 1.0, then by construction the guard is silent on
                // 99% of what humans do -- it can only ever engage in the top 1% tail. This is the same claim
                // as the fire-rate test, read from the distribution instead of the counter.
                float swingP99 = Pct(swingMag, 99);
                float axialP99 = Pct(axialMag, 99);
                Debug.Log($"[spine tail] {s.Label}  p99 swing {swingP99:F2} of limit,  p99 axial {axialP99:F2} of limit");

                Assert.Less(swingP99, 1.0f,
                    $"{s.Label}: real swing reaches {swingP99:F2}x the limit at p99 -- the limit sits inside real motion");
                Assert.Less(axialP99, 1.0f,
                    $"{s.Label}: real axial rotation reaches {axialP99:F2}x the limit at p99 -- the limit is too tight");
            }
        }
    }
}
