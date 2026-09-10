using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.IK;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisArmPriorDumpTests
    {
        static string CorpusDir => Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~");
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        [Test]
        public void DumpArmFeatures_ForOfflinePriorFitting()
        {
            if (!Directory.Exists(CorpusDir)) Assert.Ignore("no mocap corpus");
            string[] files = Directory.GetFiles(CorpusDir, "*.bvh");
            System.Array.Sort(files);
            var sb = new StringBuilder("clip,frame,side,hx,hy,hz,ex,ey,ez,headx,heady,headz,palmx,palmy,palmz,fwdx,fwdy,fwdz,armLen,upperLen\n");
            int rows = 0;
            foreach (string file in files)
            {
                if (!BasisBvhLoader.TryLoad(file, out BasisMotionClip clip, out string err)) continue;
                if (!BasisBvhLoader.Validate(clip, out _)) continue;
                for (int f = 0; f < clip.FrameCount; f += 2)
                {
                    Vector3 lSh = clip.Get(f, BasisMocapJoint.LeftUpperArm).Position, rSh = clip.Get(f, BasisMocapJoint.RightUpperArm).Position;
                    Vector3 hipsP = clip.Get(f, BasisMocapJoint.Hips).Position, neckP = clip.Get(f, BasisMocapJoint.Neck).Position, headP = clip.Get(f, BasisMocapJoint.Head).Position;
                    BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(lSh, rSh, hipsP, neckP);
                    if (!frame.Valid) continue;
                    for (int side = 0; side < 2; side++)
                    {
                        bool isLeft = side == 0;
                        Vector3 s = isLeft ? lSh : rSh, e = clip.Get(f, isLeft ? BasisMocapJoint.LeftLowerArm : BasisMocapJoint.RightLowerArm).Position, w = clip.Get(f, isLeft ? BasisMocapJoint.LeftHand : BasisMocapJoint.RightHand).Position;
                        Quaternion handRot = clip.Get(f, isLeft ? BasisMocapJoint.LeftHand : BasisMocapJoint.RightHand).Rotation;
                        float upperLen = Vector3.Distance(s, e), armLen = upperLen + Vector3.Distance(e, w);
                        if (armLen < 1e-4f) continue;
                        Vector3 outward = isLeft ? -frame.Right : frame.Right;
                        Vector3 h = Local(w - s, outward, frame) / armLen, el = Local(e - s, outward, frame) / armLen, hd = Local(headP - s, outward, frame) / armLen;
                        Vector3 palm = Local(handRot * Vector3.down, outward, frame), fwd = Local(handRot * (isLeft ? Vector3.left : Vector3.right), outward, frame);
                        sb.Append(clip.Name).Append(',').Append(f).Append(',').Append(isLeft ? 'L' : 'R').Append(',');
                        Append(sb, h); Append(sb, el); Append(sb, hd); Append(sb, palm); Append(sb, fwd);
                        sb.Append(F(armLen)).Append(',').Append(F(upperLen)).Append('\n');
                        rows++;
                    }
                }
            }
            string dir = Path.Combine(Application.persistentDataPath, "MocapAccuracy");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "armdump.csv");
            File.WriteAllText(path, sb.ToString());
            TestContext.WriteLine($"wrote {rows} rows to {path}");
            Assert.That(rows, Is.GreaterThan(0));
        }
        static Vector3 Local(Vector3 v, Vector3 outward, in BasisSwivelFrame frame) => new Vector3(Vector3.Dot(v, outward), Vector3.Dot(v, frame.Up), Vector3.Dot(v, frame.Forward));
        static void Append(StringBuilder sb, Vector3 v) => sb.Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(',');
    }
}
