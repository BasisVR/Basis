using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Developer.Recorder.Tests
{
    public class BasisAvatarRecordingTests
    {
        private const float Eps = 1e-4f;

        private static float[] BodyFrame(float interval, Quaternion bodyRot, Vector3 bodyPos, float muscle)
        {
            float[] f = new float[BasisAvatarRecorder.FloatsPerFrame];
            f[0] = interval;
            f[1] = bodyRot.x; f[2] = bodyRot.y; f[3] = bodyRot.z; f[4] = bodyRot.w;
            f[5] = bodyPos.x; f[6] = bodyPos.y; f[7] = bodyPos.z;
            for (int m = 0; m < BasisAvatarRecorder.MuscleCount; m++) f[8 + m] = muscle + m * 0.001f;
            f[8 + BasisAvatarRecorder.MuscleCount] = 1f;
            return f;
        }

        private static float[] V2Frame(float interval, Quaternion bodyRot, Vector3 bodyPos, float muscle, Vector3 rootPos, Quaternion rootRot, float rootScale, params (Vector3 p, Quaternion q, Vector3 s)[] bones)
        {
            float[] f = new float[BasisAvatarRecorder.GetFloatsPerFrame(bones.Length)];
            BodyFrame(interval, bodyRot, bodyPos, muscle).CopyTo(f, 0);
            int o = BasisAvatarRecorder.FloatsPerFrame;
            f[o] = rootPos.x; f[o + 1] = rootPos.y; f[o + 2] = rootPos.z;
            f[o + 3] = rootRot.x; f[o + 4] = rootRot.y; f[o + 5] = rootRot.z; f[o + 6] = rootRot.w;
            f[o + 7] = rootScale;
            o += BasisAvatarRecorder.RootFloats;
            foreach (var b in bones)
            {
                f[o] = b.p.x; f[o + 1] = b.p.y; f[o + 2] = b.p.z;
                f[o + 3] = b.q.x; f[o + 4] = b.q.y; f[o + 5] = b.q.z; f[o + 6] = b.q.w;
                f[o + 7] = b.s.x; f[o + 8] = b.s.y; f[o + 9] = b.s.z;
                o += BasisAvatarRecorder.BoneFloats;
            }
            return f;
        }

        private static void WriteFloats(BinaryWriter w, float[] f) { foreach (float v in f) w.Write(v); }

        private static void AssertVec(Vector3 expected, Vector3 actual) => Assert.That((expected - actual).magnitude, Is.LessThan(Eps), $"expected {expected} got {actual}");
        private static void AssertRot(Quaternion expected, Quaternion actual) => Assert.That(Quaternion.Angle(expected, actual), Is.LessThan(0.01f), $"expected {expected} got {actual}");

        [Test]
        public void LegacyFileWithoutHeaderReadsAsBodyOnly()
        {
            using MemoryStream ms = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                WriteFloats(w, BodyFrame(0.1f, Quaternion.identity, new Vector3(0f, 1f, 0f), 0.2f));
                WriteFloats(w, BodyFrame(0.1f, Quaternion.Euler(0f, 30f, 0f), new Vector3(0.1f, 1f, 0.2f), 0.3f));
            }
            Assert.AreEqual(2 * BasisAvatarRecorder.BytesPerFrame, ms.Length);
            ms.Position = 0;
            BasisAvatarRecording r = BasisAvatarRecording.Read(ms);
            Assert.AreEqual(1, r.Version);
            Assert.IsFalse(r.HasRootMotion);
            Assert.AreEqual(2, r.FrameCount);
            Assert.AreEqual(0, r.BoneCount);
            Assert.AreEqual(0.1f, r.Times[1], Eps);
            Assert.AreEqual(0.3f + 5 * 0.001f, r.Muscles[5][1], Eps);
            r.ComputeRootCurves(out Vector3[] rootT, out Quaternion[] rootQ);
            AssertVec(new Vector3(0.1f, 1f, 0.2f), rootT[1]);
            AssertRot(Quaternion.Euler(0f, 30f, 0f), rootQ[1]);
        }

        [Test]
        public void V2HeaderAndBonesRoundTrip()
        {
            string[] paths = { "Armature/Hips/Tail", "Armature/Hips/Tail/Tail.001" };
            Quaternion tailRot = Quaternion.Euler(10f, 20f, 30f);
            using MemoryStream ms = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                BasisAvatarRecorder.WriteHeader(w, 0.9f, true, paths);
                WriteFloats(w, V2Frame(0.02f, Quaternion.identity, Vector3.up, 0f, new Vector3(1f, 0f, 1f), Quaternion.identity, 1f,
                    (new Vector3(0f, 0.1f, 0f), Quaternion.identity, Vector3.one), (new Vector3(0f, 0.2f, 0f), Quaternion.identity, Vector3.one)));
                WriteFloats(w, V2Frame(0.02f, Quaternion.identity, Vector3.up, 0f, new Vector3(1f, 0f, 2f), Quaternion.identity, 1f,
                    (new Vector3(0f, 0.1f, 0f), tailRot, Vector3.one), (new Vector3(0f, 0.2f, 0f), Quaternion.identity, new Vector3(2f, 2f, 2f))));
            }
            ms.Position = 0;
            BasisAvatarRecording r = BasisAvatarRecording.Read(ms);
            Assert.AreEqual(2, r.Version);
            Assert.IsTrue(r.HasRootMotion);
            Assert.AreEqual(0.9f, r.HumanScale, Eps);
            CollectionAssert.AreEqual(paths, r.BonePaths);
            Assert.AreEqual(2, r.FrameCount);
            AssertVec(new Vector3(1f, 0f, 2f), r.RootPositions[1]);
            AssertRot(tailRot, r.BoneLocalRotations[1 * 2 + 0]);
            AssertVec(new Vector3(2f, 2f, 2f), r.BoneLocalScales[1 * 2 + 1]);
            r.ComputeRootCurves(out Vector3[] rootT, out _);
            AssertVec(Vector3.up, rootT[0]);
            AssertVec(new Vector3(0f, 1f, 1f / 0.9f), rootT[1]);
        }

        [Test]
        public void RootMotionOffKeepsBodyRelativePose()
        {
            using MemoryStream ms = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                BasisAvatarRecorder.WriteHeader(w, 1f, false, null);
                WriteFloats(w, V2Frame(0f, Quaternion.identity, Vector3.up, 0f, Vector3.zero, Quaternion.identity, 1f));
                WriteFloats(w, V2Frame(0.1f, Quaternion.identity, Vector3.up, 0f, new Vector3(5f, 0f, 5f), Quaternion.identity, 1f));
            }
            ms.Position = 0;
            BasisAvatarRecording r = BasisAvatarRecording.Read(ms);
            Assert.IsFalse(r.HasRootMotion);
            r.ComputeRootCurves(out Vector3[] rootT, out _);
            AssertVec(Vector3.up, rootT[1]);
        }

        [Test]
        public void ComposeRootTranslatesInFirstFrameSpaceAndNormalizesScale()
        {
            Vector3 p0 = new Vector3(1f, 0f, 1f);
            Quaternion r0 = Quaternion.Euler(0f, 90f, 0f);
            Vector3 p1 = p0 + r0 * new Vector3(0f, 0f, 2f);
            BasisAvatarRecording.ComposeRoot(p0, r0, 2f, 0.9f, p1, r0, 2f, new Vector3(0f, 1f, 0f), Quaternion.identity, out Vector3 t, out Quaternion q);
            AssertVec(new Vector3(0f, 1f, 2f / (0.9f * 2f)), t);
            AssertRot(Quaternion.identity, q);
        }

        [Test]
        public void ComposeRootCarriesTurningIntoBodyPose()
        {
            Vector3 p0 = new Vector3(3f, 0f, -2f);
            Quaternion r0 = Quaternion.Euler(0f, 45f, 0f);
            Quaternion turn = Quaternion.Euler(0f, 90f, 0f);
            BasisAvatarRecording.ComposeRoot(p0, r0, 1f, 1f, p0, r0 * turn, 1f, new Vector3(0f, 1f, 0.5f), Quaternion.Euler(5f, 0f, 0f), out Vector3 t, out Quaternion q);
            AssertVec(new Vector3(0.5f, 1f, 0f), t);
            AssertRot(turn * Quaternion.Euler(5f, 0f, 0f), q);
        }

        [Test]
        public void BuildClipWritesRootMotionAndAdditionalBoneCurves()
        {
            string[] paths = { "Armature/Ear", "Armature/Static" };
            using MemoryStream ms = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                BasisAvatarRecorder.WriteHeader(w, 1f, true, paths);
                for (int i = 0; i < 4; i++)
                    WriteFloats(w, V2Frame(1f / 30f, Quaternion.identity, Vector3.up, 0f, new Vector3(0f, 0f, i), Quaternion.Euler(0f, i * 10f, 0f), 1f,
                        (Vector3.zero, Quaternion.Euler(0f, 0f, i * 15f), Vector3.one), (Vector3.one, Quaternion.identity, Vector3.one)));
            }
            ms.Position = 0;
            BasisAvatarRecording r = BasisAvatarRecording.Read(ms);
            AnimationClip clip = BasisAvatarRecorderWindow.BuildClip(r);
            try
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (string name in new[] { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" })
                    Assert.IsTrue(bindings.Any(b => b.path == "" && b.propertyName == name), name);
                Assert.IsTrue(bindings.Any(b => b.path == "Armature/Ear" && b.type == typeof(Transform) && b.propertyName.Contains("LocalRotation")));
                Assert.IsTrue(bindings.Any(b => b.path == "Armature/Static" && b.type == typeof(Transform) && b.propertyName.Contains("LocalPosition")));
                Assert.IsFalse(bindings.Any(b => b.propertyName.Contains("LocalScale")));
                EditorCurveBinding rootTz = bindings.First(b => b.path == "" && b.propertyName == "RootT.z");
                Assert.AreEqual(3f, AnimationUtility.GetEditorCurve(clip, rootTz).Evaluate(r.Times[3]), Eps);
                EditorCurveBinding staticX = bindings.First(b => b.path == "Armature/Static" && b.propertyName.Contains("LocalPosition") && b.propertyName.EndsWith(".x"));
                Assert.AreEqual(2, AnimationUtility.GetEditorCurve(clip, staticX).length);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void RelativePathSkipsRoot()
        {
            GameObject root = new GameObject("Avatar");
            try
            {
                Transform a = new GameObject("Armature").transform;
                a.SetParent(root.transform, false);
                Transform tail = new GameObject("Tail").transform;
                tail.SetParent(a, false);
                Assert.AreEqual("Armature/Tail", BasisAvatarRecorderDriver.GetRelativePath(root.transform, tail));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
