using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Basis.IK.Mocap
{
    // Real human motion, loaded from BVH, as ground truth for IK accuracy.
    //
    // Editor-only on purpose (a mocap parser has no business in a player build) but it lives in the RUNTIME
    // assembly so the NUnit tests and the sweep windows can both reach it -- Basis.Framework.IK.Tests does not
    // reference BasisEditor, and putting it there would force the tests to hand-mirror the maths, which is the
    // drift trap the whole core/sweep/gate convention exists to avoid.
    public enum BasisMocapJoint
    {
        Hips, Spine, Chest, UpperChest, Neck, Head,
        LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand,
        RightShoulder, RightUpperArm, RightLowerArm, RightHand,
        LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes,
        RightUpperLeg, RightLowerLeg, RightFoot, RightToes,
        Count
    }

    public struct BasisMocapPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Valid;
    }

    public class BasisMotionClip
    {
        public string Name;
        public float FrameTime;
        public int FrameCount;
        public BasisMocapPose[] Poses;   // FrameCount * BasisMocapJoint.Count, row-major
        public float SourceToMetres;     // the scale that was applied

        public BasisMocapPose Get(int frame, BasisMocapJoint j) => Poses[frame * (int)BasisMocapJoint.Count + (int)j];
        public bool Has(BasisMocapJoint j) => FrameCount > 0 && Get(0, j).Valid;
    }

    public static class BasisBvhLoader
    {
        // A CMU subject is a real person, so normalising every clip to one adult stature makes the centimetre
        // errors comparable across subjects. The solve is exactly scale-invariant (BasisBodyProportionTests),
        // so this changes the units of the answer, never the answer.
        public const float TargetLegLengthMetres = 0.85f;

        class Node
        {
            public string Name;
            public int Parent = -1;
            public Vector3 Offset;                 // already converted to Unity axes
            public readonly List<string> Channels = new List<string>();
            public int ChannelStart;
            public BasisMocapJoint Joint = BasisMocapJoint.Count;   // Count = not mapped
        }

        // CMU/Biovision joint names -> the joints we care about. CMU's LHipJoint/RHipJoint/Neck1/LowerBack are
        // structural in-betweens with no humanoid equivalent; they still contribute to FK, they just are not read.
        static readonly Dictionary<string, BasisMocapJoint> k_NameMap = new Dictionary<string, BasisMocapJoint>
        {
            { "Hips", BasisMocapJoint.Hips },
            { "LowerBack", BasisMocapJoint.Spine },
            { "Spine", BasisMocapJoint.Chest },
            { "Spine1", BasisMocapJoint.UpperChest },
            { "Neck", BasisMocapJoint.Neck },
            { "Head", BasisMocapJoint.Head },
            { "LeftShoulder", BasisMocapJoint.LeftShoulder },
            { "LeftArm", BasisMocapJoint.LeftUpperArm },
            { "LeftForeArm", BasisMocapJoint.LeftLowerArm },
            { "LeftHand", BasisMocapJoint.LeftHand },
            { "RightShoulder", BasisMocapJoint.RightShoulder },
            { "RightArm", BasisMocapJoint.RightUpperArm },
            { "RightForeArm", BasisMocapJoint.RightLowerArm },
            { "RightHand", BasisMocapJoint.RightHand },
            { "LeftUpLeg", BasisMocapJoint.LeftUpperLeg },
            { "LeftLeg", BasisMocapJoint.LeftLowerLeg },
            { "LeftFoot", BasisMocapJoint.LeftFoot },
            { "LeftToeBase", BasisMocapJoint.LeftToes },
            { "RightUpLeg", BasisMocapJoint.RightUpperLeg },
            { "RightLeg", BasisMocapJoint.RightLowerLeg },
            { "RightFoot", BasisMocapJoint.RightFoot },
            { "RightToeBase", BasisMocapJoint.RightToes },
        };

        public static bool TryLoad(string path, out BasisMotionClip clip, out string error)
        {
            clip = null;
            error = null;
            if (!File.Exists(path))
            {
                error = $"no such file: {path}";
                return false;
            }

            string[] tokens = File.ReadAllText(path)
                .Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

            var nodes = new List<Node>();
            int cursor = 0;
            int channelCount = 0;

            try
            {
                if (!Eat(tokens, ref cursor, "HIERARCHY")) { error = "not a BVH file (no HIERARCHY)"; return false; }
                ParseJoint(tokens, ref cursor, nodes, -1, ref channelCount);
                if (!Eat(tokens, ref cursor, "MOTION")) { error = "no MOTION section"; return false; }
                if (!Eat(tokens, ref cursor, "Frames:")) { error = "no Frames:"; return false; }
                int frameCount = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
                if (!Eat(tokens, ref cursor, "Frame")) { error = "no Frame Time:"; return false; }
                if (!Eat(tokens, ref cursor, "Time:")) { error = "no Frame Time:"; return false; }
                float frameTime = float.Parse(tokens[cursor++], CultureInfo.InvariantCulture);

                clip = Bake(nodes, tokens, cursor, frameCount, frameTime, channelCount, Path.GetFileNameWithoutExtension(path));
                return true;
            }
            catch (System.Exception e)
            {
                error = $"parse failed: {e.Message}";
                clip = null;
                return false;
            }
        }

        static bool Eat(string[] t, ref int i, string want)
        {
            if (i >= t.Length || t[i] != want) return false;
            i++;
            return true;
        }

        static void ParseJoint(string[] t, ref int i, List<Node> nodes, int parent, ref int channelCount)
        {
            // ROOT <name> | JOINT <name> | End Site
            bool endSite = t[i] == "End";
            string name = endSite ? "__end" : t[i + 1];
            i += endSite ? 2 : 2;   // "End Site" / "ROOT name"

            var node = new Node { Name = name, Parent = parent };
            int self = nodes.Count;
            nodes.Add(node);

            Eat(t, ref i, "{");

            while (i < t.Length && t[i] != "}")
            {
                if (t[i] == "OFFSET")
                {
                    i++;
                    float ox = float.Parse(t[i++], CultureInfo.InvariantCulture);
                    float oy = float.Parse(t[i++], CultureInfo.InvariantCulture);
                    float oz = float.Parse(t[i++], CultureInfo.InvariantCulture);
                    // BVH is right-handed Y-up; Unity is left-handed. Mirror X.
                    node.Offset = new Vector3(-ox, oy, oz);
                }
                else if (t[i] == "CHANNELS")
                {
                    i++;
                    int n = int.Parse(t[i++], CultureInfo.InvariantCulture);
                    node.ChannelStart = channelCount;
                    for (int c = 0; c < n; c++) node.Channels.Add(t[i++]);
                    channelCount += n;
                }
                else if (t[i] == "JOINT" || t[i] == "End")
                {
                    ParseJoint(t, ref i, nodes, self, ref channelCount);
                }
                else
                {
                    i++;
                }
            }
            Eat(t, ref i, "}");

            if (!endSite && k_NameMap.TryGetValue(name, out BasisMocapJoint j)) node.Joint = j;
        }

        // The mirror that takes a right-handed rotation into Unity's left-handed frame is q -> (x, -y, -z, w),
        // which is the same as negating the Y and Z Euler angles and keeping X. Channels compose in the order
        // they are declared (BVH: first listed is leftmost), so append on the right as we walk them.
        static Quaternion ChannelRotation(Node n, float[] frame)
        {
            Quaternion r = Quaternion.identity;
            for (int c = 0; c < n.Channels.Count; c++)
            {
                float v = frame[n.ChannelStart + c];
                switch (n.Channels[c])
                {
                    case "Xrotation": r *= Quaternion.AngleAxis(v, Vector3.right); break;
                    case "Yrotation": r *= Quaternion.AngleAxis(-v, Vector3.up); break;
                    case "Zrotation": r *= Quaternion.AngleAxis(-v, Vector3.forward); break;
                }
            }
            return r;
        }

        static Vector3 ChannelPosition(Node n, float[] frame)
        {
            Vector3 p = Vector3.zero;
            for (int c = 0; c < n.Channels.Count; c++)
            {
                float v = frame[n.ChannelStart + c];
                switch (n.Channels[c])
                {
                    case "Xposition": p.x = -v; break;   // mirrored with the offsets
                    case "Yposition": p.y = v; break;
                    case "Zposition": p.z = v; break;
                }
            }
            return p;
        }

        static BasisMotionClip Bake(List<Node> nodes, string[] t, int cursor, int frameCount, float frameTime, int channelCount, string name)
        {
            int jointCount = (int)BasisMocapJoint.Count;
            var clip = new BasisMotionClip
            {
                Name = name,
                FrameTime = frameTime,
                FrameCount = frameCount,
                Poses = new BasisMocapPose[frameCount * jointCount],
            };

            var frame = new float[channelCount];
            var worldPos = new Vector3[nodes.Count];
            var worldRot = new Quaternion[nodes.Count];

            for (int f = 0; f < frameCount; f++)
            {
                for (int c = 0; c < channelCount; c++)
                {
                    frame[c] = float.Parse(t[cursor++], CultureInfo.InvariantCulture);
                }

                for (int n = 0; n < nodes.Count; n++)
                {
                    Node node = nodes[n];
                    Quaternion local = ChannelRotation(node, frame);

                    if (node.Parent < 0)
                    {
                        worldPos[n] = node.Offset + ChannelPosition(node, frame);
                        worldRot[n] = local;
                    }
                    else
                    {
                        // BVH: the offset is expressed in the PARENT's frame.
                        worldPos[n] = worldPos[node.Parent] + worldRot[node.Parent] * node.Offset;
                        worldRot[n] = worldRot[node.Parent] * local;
                    }

                    if (node.Joint != BasisMocapJoint.Count)
                    {
                        clip.Poses[f * jointCount + (int)node.Joint] = new BasisMocapPose
                        {
                            Position = worldPos[n],
                            Rotation = worldRot[n],
                            Valid = true,
                        };
                    }
                }
            }

            Rescale(clip);
            return clip;
        }

        // Normalise to a standard adult so centimetre errors are comparable across subjects.
        static void Rescale(BasisMotionClip clip)
        {
            if (clip.FrameCount == 0) return;

            float thigh = Vector3.Distance(clip.Get(0, BasisMocapJoint.LeftUpperLeg).Position, clip.Get(0, BasisMocapJoint.LeftLowerLeg).Position);
            float shin = Vector3.Distance(clip.Get(0, BasisMocapJoint.LeftLowerLeg).Position, clip.Get(0, BasisMocapJoint.LeftFoot).Position);
            float leg = thigh + shin;
            if (leg <= 1e-4f) { clip.SourceToMetres = 1f; return; }

            float s = TargetLegLengthMetres / leg;
            clip.SourceToMetres = s;
            for (int i = 0; i < clip.Poses.Length; i++)
            {
                clip.Poses[i].Position *= s;   // rotations are scale-free
            }
        }

        // Handedness is the classic BVH bug, and it is SILENT: get it wrong and left/right swap, which would
        // quietly corrupt every chirality-sensitive number the accuracy harness reports. So assert the skeleton
        // is anatomically sane instead of trusting the conversion.
        public static bool Validate(BasisMotionClip clip, out string reason)
        {
            reason = null;
            if (clip == null || clip.FrameCount == 0) { reason = "empty clip"; return false; }

            foreach (BasisMocapJoint j in new[]
            {
                BasisMocapJoint.Hips, BasisMocapJoint.Head,
                BasisMocapJoint.LeftUpperArm, BasisMocapJoint.LeftLowerArm, BasisMocapJoint.LeftHand,
                BasisMocapJoint.RightUpperArm, BasisMocapJoint.RightLowerArm, BasisMocapJoint.RightHand,
                BasisMocapJoint.LeftUpperLeg, BasisMocapJoint.LeftLowerLeg, BasisMocapJoint.LeftFoot,
                BasisMocapJoint.RightUpperLeg, BasisMocapJoint.RightLowerLeg, BasisMocapJoint.RightFoot,
            })
            {
                if (!clip.Has(j)) { reason = $"clip is missing {j}"; return false; }
            }

            int lefties = 0, kneesForward = 0, checks = 0;
            int step = Mathf.Max(1, clip.FrameCount / 20);

            for (int f = 0; f < clip.FrameCount; f += step)
            {
                Quaternion hips = clip.Get(f, BasisMocapJoint.Hips).Rotation;
                Vector3 fwd = hips * Vector3.forward;
                Vector3 up = hips * Vector3.up;
                Vector3 right = Vector3.Cross(up, fwd);   // Unity is left-handed: Cross(up, forward) == right

                Vector3 chest = clip.Get(f, BasisMocapJoint.Chest).Position;
                Vector3 lHand = clip.Get(f, BasisMocapJoint.LeftHand).Position;
                if (Vector3.Dot(lHand - chest, right) < 0f) lefties++;

                Vector3 hip = clip.Get(f, BasisMocapJoint.LeftUpperLeg).Position;
                Vector3 knee = clip.Get(f, BasisMocapJoint.LeftLowerLeg).Position;
                Vector3 ankle = clip.Get(f, BasisMocapJoint.LeftFoot).Position;
                if (Vector3.Dot(knee - 0.5f * (hip + ankle), fwd) > 0f) kneesForward++;

                checks++;
            }

            if (checks == 0) { reason = "no frames sampled"; return false; }

            // A walking arm crosses the midline, so demand a clear majority, not unanimity.
            if (lefties * 2 <= checks)
            {
                reason = $"the left hand is on the body's RIGHT in {checks - lefties}/{checks} sampled frames -- " +
                         "the skeleton is mirrored, so the right-handed to left-handed conversion is wrong";
                return false;
            }
            if (kneesForward * 2 <= checks)
            {
                reason = $"the knee bends BACKWARD in {checks - kneesForward}/{checks} sampled frames -- " +
                         "the skeleton is mirrored or the rotation channel order is wrong";
                return false;
            }
            return true;
        }
    }
}
