using Basis.Scripts.Drivers;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
namespace Basis.Tests.Sync
{
    public sealed class BasisFingerTestRig : IDisposable
    {
        public const string LankyPath = "Packages/com.basis.sdk/Meshes/LankyLoad.fbx", FixtureName = "fixture";
        public static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };
        public Animator Animator;
        public BasisHandPoseGrid Grid;
        public BasisFingerTipRetarget Retarget;
        public Transform[] Joints = new Transform[30];
        public Vector3[] TipLocal = new Vector3[10];
        public Transform LeftHand, RightHand;
        public Quaternion LeftPalmInverse, RightPalmInverse;
        public string Name;
        GameObject instance;
        BasisHumanoidRigFixture fixture;
        public static IEnumerable<string> Rigs()
        {
            yield return FixtureName;
            yield return LankyPath;
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is ModelImporter importer && importer.animationType == ModelImporterAnimationType.Human && Array.Exists(importer.humanDescription.human, b => b.humanName == "Left Thumb Proximal") && Array.Exists(importer.humanDescription.human, b => b.humanName == "Right Little Distal")) yield return path;
            }
        }
        public static IEnumerable<string> AnatomicalRigs()
        {
            foreach (string name in Rigs()) if (name != FixtureName) yield return name;
        }
        public static BasisFingerTestRig Build(string name)
        {
            BasisFingerTestRig rig = new BasisFingerTestRig { Name = name };
            if (name != FixtureName)
            {
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(name);
                Assert.IsNotNull(asset, name);
                rig.instance = UnityEngine.Object.Instantiate(asset);
                rig.Animator = rig.instance.GetComponentInChildren<Animator>();
            }
            else
            {
                rig.fixture = BasisHumanoidRigFixture.Build("rom");
                rig.Animator = rig.fixture.Animator;
            }
            rig.Setup();
            return rig;
        }
        public static BasisFingerTestRig FromAnimator(string name, Animator animator)
        {
            BasisFingerTestRig rig = new BasisFingerTestRig { Name = name, Animator = animator };
            rig.Setup();
            return rig;
        }
        public static BasisFingerTestRig TryFromAnimator(string name, Animator animator, out string reason)
        {
            reason = null;
            if (animator == null || !animator.isHuman)
            {
                reason = "not humanoid";
                return null;
            }
            for (int i = 0; i < 30; i++)
            {
                if (animator.GetBoneTransform((HumanBodyBones)((int)HumanBodyBones.LeftThumbProximal + i)) == null)
                {
                    reason = $"missing {(HumanBodyBones)((int)HumanBodyBones.LeftThumbProximal + i)}";
                    return null;
                }
            }
            BasisFingerTestRig rig = new BasisFingerTestRig { Name = name, Animator = animator };
            try
            {
                rig.Grid = new BasisHandPoseGrid();
                if (!rig.Grid.TryBake(animator, BasisHandPoseGrid.DefaultIncrement, out _)) reason = "grid bake failed";
                else
                {
                    rig.Retarget = new BasisFingerTipRetarget();
                    if (!rig.Retarget.Build(animator, rig.Grid)) reason = "retarget build failed (degenerate palm)";
                }
                if (reason == null) rig.Setup();
            }
            catch (Exception e)
            {
                reason = e.Message;
            }
            if (reason == null) return rig;
            rig.Dispose();
            return null;
        }
        void Setup()
        {
            Grid?.Dispose();
            Assert.IsNotNull(Animator);
            Assert.IsTrue(Animator.isHuman, $"{Name} not humanoid");
            Grid = new BasisHandPoseGrid();
            Assert.IsTrue(Grid.TryBake(Animator, BasisHandPoseGrid.DefaultIncrement, out _), $"{Name} bake failed");
            for (int i = 0; i < 30; i++)
            {
                Joints[i] = Animator.GetBoneTransform((HumanBodyBones)((int)HumanBodyBones.LeftThumbProximal + i));
                Assert.IsNotNull(Joints[i], $"{Name} missing finger bone {i}");
            }
            for (int f = 0; f < 10; f++)
            {
                Transform distal = Joints[f * 3 + 2];
                TipLocal[f] = distal.childCount > 0 ? distal.GetChild(0).localPosition : distal.localPosition;
                if (TipLocal[f].sqrMagnitude < 1e-12f) TipLocal[f] = distal.localPosition;
            }
            LeftHand = Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            RightHand = Animator.GetBoneTransform(HumanBodyBones.RightHand);
            LeftPalmInverse = PalmInverse(LeftHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal);
            RightPalmInverse = PalmInverse(RightHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal);
            Retarget = new BasisFingerTipRetarget();
            Assert.IsTrue(Retarget.Build(Animator, Grid), $"{Name} retarget build failed");
        }
        Quaternion PalmInverse(Transform hand, HumanBodyBones index, HumanBodyBones middle, HumanBodyBones little)
        {
            Assert.IsTrue(BasisFingerTipRetarget.TryPalmInverse(Vector3.zero, hand.InverseTransformPoint(Animator.GetBoneTransform(index).position), hand.InverseTransformPoint(Animator.GetBoneTransform(middle).position), hand.InverseTransformPoint(Animator.GetBoneTransform(little).position), out Quaternion inverse));
            return inverse;
        }
        void Pose(int finger, Vector2 percentage)
        {
            for (int j = 0; j < 3; j++) Joints[finger * 3 + j].localRotation = Grid.SampleJoint(finger, j, new float2(percentage.x, percentage.y));
        }
        public Vector3 Tip(int finger, Vector2 percentage)
        {
            Pose(finger, percentage);
            Transform distal = Joints[finger * 3 + 2], proximal = Joints[finger * 3], hand = finger < 5 ? LeftHand : RightHand;
            return Quaternion.Inverse(hand.rotation) * (distal.TransformPoint(TipLocal[finger]) - proximal.position).normalized;
        }
        public Vector3 PalmTip(int finger, Vector2 percentage)
        {
            Pose(finger, percentage);
            Transform hand = finger < 5 ? LeftHand : RightHand;
            Vector3 tip = hand.InverseTransformPoint(Joints[finger * 3 + 2].TransformPoint(TipLocal[finger])), basePoint = hand.InverseTransformPoint(Joints[finger * 3].position);
            return ((finger < 5 ? LeftPalmInverse : RightPalmInverse) * (tip - basePoint)).normalized;
        }
        public void Dispose()
        {
            Grid?.Dispose();
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            fixture?.Dispose();
        }
    }
    public static class BasisValvePoses
    {
        public const string Env = "BASIS_VALVE_POSES";
        public static readonly int[] FingerBase = { 1, 6, 11, 16, 21 }, FingerTip = { 4, 9, 14, 19, 24 };
        public struct Pose
        {
            public string Set, Label;
            public int Frame;
            public Vector3[] Points;
        }
        public static List<Pose> Load()
        {
            string path = Environment.GetEnvironmentVariable(Env);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            List<Pose> poses = new List<Pose>();
            foreach (Match m in Regex.Matches(File.ReadAllText(path), "\\{\"set\": \"(\\w+)\", \"frame\": (\\d+), \"label\": \"([^\"]*)\", \"p\": \\[([^\\]]*)\\]\\}"))
            {
                string[] parts = m.Groups[4].Value.Split(',');
                Vector3[] points = new Vector3[parts.Length / 3];
                for (int i = 0; i < points.Length; i++) points[i] = new Vector3(float.Parse(parts[i * 3], CultureInfo.InvariantCulture), float.Parse(parts[i * 3 + 1], CultureInfo.InvariantCulture), float.Parse(parts[i * 3 + 2], CultureInfo.InvariantCulture));
                poses.Add(new Pose { Set = m.Groups[1].Value, Frame = int.Parse(m.Groups[2].Value), Label = m.Groups[3].Value, Points = points });
            }
            return poses;
        }
        public static Vector3 Point(Vector3[] p, int i, bool left) => left ? new Vector3(-p[i].x, p[i].y, p[i].z) : p[i];
        public static Vector3 Target(Vector3[] p, int finger, bool left)
        {
            Assert.IsTrue(BasisFingerTipRetarget.TryPalmInverse(Point(p, 0, left), Point(p, 6, left), Point(p, 11, left), Point(p, 21, left), out Quaternion inverse));
            return (inverse * (Point(p, FingerTip[finger], left) - Point(p, FingerBase[finger], left))).normalized;
        }
        public static Vector3[] MediaPipeLandmarks(Vector3[] p, bool left)
        {
            Vector3[] lm = new Vector3[21];
            lm[0] = Point(p, 0, left);
            for (int i = 0; i < 4; i++) lm[1 + i] = Point(p, 1 + i, left);
            for (int f = 0; f < 4; f++)
                for (int j = 0; j < 4; j++) lm[5 + f * 4 + j] = Point(p, 6 + f * 5 + j, left);
            return lm;
        }
    }
}
