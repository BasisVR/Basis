using System.Collections.Generic;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Builds throwaway humanoid rigs in memory so finger tests can run against real
    /// <see cref="HumanPoseHandler"/> muscle space without depending on any avatar asset.
    ///
    /// The point of having two of them is the claim the finger block rests on: nothing about finger
    /// geometry crosses the wire, so a pose authored on one rig has to reproduce PROPORTIONALLY on a
    /// rig with different finger lengths. That is only testable with two rigs whose hands genuinely
    /// differ, which is what <see cref="FingerScale"/> and <see cref="HandScale"/> are for.
    ///
    /// Bones are authored directly in T-pose (arms down ±X, legs down −Y) because that is the pose
    /// Unity's humanoid retarget is defined against and the pose the grid bake samples from.
    /// </summary>
    public sealed class BasisHumanoidRigFixture : System.IDisposable
    {
        public GameObject Root;
        public Animator Animator;
        public Avatar Avatar;

        /// <summary>Finger bone transforms, flat-indexed finger*3 + joint (L thumb→little, then R).</summary>
        public Transform[] FingerJoints = new Transform[30];
        public Transform LeftHand;
        public Transform RightHand;

        /// <summary>Per-finger segment length used when authoring; tests convert to proportions with it.</summary>
        public float[] FingerSegmentLength = new float[10];

        readonly List<Transform> _all = new List<Transform>();

        public static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };

        /// <summary>Relative length of each finger, thumb→little. Roughly anatomical.</summary>
        static readonly float[] FingerLengthRatio = { 0.72f, 1.00f, 1.08f, 0.98f, 0.78f };

        /// <summary>Lateral offset of each knuckle across the palm, thumb→little.</summary>
        static readonly float[] KnuckleSpread = { 0.034f, 0.014f, -0.002f, -0.018f, -0.034f };

        /// <summary>
        /// Builds a rig. <paramref name="fingerScale"/> multiplies every finger segment;
        /// <paramref name="handScale"/> multiplies the wrist→knuckle span; <paramref name="uniformScale"/>
        /// scales the whole root transform, which must cancel out of anything proportional.
        /// <paramref name="fingers"/> false builds the rig with hands but no finger bones, which is
        /// the shape of the far LOD skeleton (twenty core bones, arms ending at the wrist).
        /// </summary>
        public static BasisHumanoidRigFixture Build(
            string name, float fingerScale = 1f, float handScale = 1f, float uniformScale = 1f, bool fingers = true)
        {
            var fixture = new BasisHumanoidRigFixture();
            fixture.Construct(name, fingerScale, handScale, uniformScale, fingers);
            return fixture;
        }

        void Construct(string name, float fingerScale, float handScale, float uniformScale, bool fingers)
        {
            Root = new GameObject(name);
            Root.SetActive(false);
            Transform root = Root.transform;
            _all.Add(root);

            Transform hips = MakeBone(HumanBodyBones.Hips, root, new Vector3(0f, 1.00f, 0f));
            Transform spine = MakeBone(HumanBodyBones.Spine, hips, new Vector3(0f, 0.12f, 0f));
            Transform chest = MakeBone(HumanBodyBones.Chest, spine, new Vector3(0f, 0.14f, 0f));
            Transform neck = MakeBone(HumanBodyBones.Neck, chest, new Vector3(0f, 0.20f, 0f));
            MakeBone(HumanBodyBones.Head, neck, new Vector3(0f, 0.10f, 0f));

            BuildLeg(hips, true);
            BuildLeg(hips, false);
            BuildArm(chest, true, fingerScale, handScale, fingers);
            BuildArm(chest, false, fingerScale, handScale, fingers);

            var skeleton = new SkeletonBone[_all.Count];
            for (int i = 0; i < _all.Count; i++)
            {
                Transform t = _all[i];
                skeleton[i] = new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                };
            }

            var description = new HumanDescription
            {
                human = _human.ToArray(),
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            Avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
            Avatar.name = name + "_avatar";

            Animator = Root.AddComponent<Animator>();
            Animator.avatar = Avatar;

            root.localScale = Vector3.one * uniformScale;
        }

        void BuildLeg(Transform hips, bool left)
        {
            float side = left ? 1f : -1f;
            var upper = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
            var lower = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            var foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;

            Transform u = MakeBone(upper, hips, new Vector3(0.09f * side, -0.05f, 0f));
            Transform l = MakeBone(lower, u, new Vector3(0f, -0.43f, 0f));
            MakeBone(foot, l, new Vector3(0f, -0.44f, 0f));
        }

        void BuildArm(Transform chest, bool left, float fingerScale, float handScale, bool fingers)
        {
            float side = left ? 1f : -1f;
            var upper = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            var lower = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            var hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;

            Transform u = MakeBone(upper, chest, new Vector3(0.15f * side, 0.15f, 0f));
            Transform l = MakeBone(lower, u, new Vector3(0.27f * side, 0f, 0f));
            Transform h = MakeBone(hand, l, new Vector3(0.26f * side, 0f, 0f));

            if (left) LeftHand = h; else RightHand = h;
            if (!fingers) return;

            float palm = 0.09f * handScale;
            int handBase = left ? 0 : 5;

            for (int f = 0; f < 5; f++)
            {
                float segment = 0.035f * FingerLengthRatio[f] * fingerScale;
                FingerSegmentLength[handBase + f] = segment;

                HumanBodyBones proximal = ProximalBone(left, f);
                Transform parent = h;
                Vector3 first = new Vector3(palm * side, 0f, KnuckleSpread[f] * side);

                for (int j = 0; j < 3; j++)
                {
                    var bone = (HumanBodyBones)((int)proximal + j);
                    Vector3 offset = j == 0 ? first : new Vector3(segment * side, 0f, 0f);
                    parent = MakeBone(bone, parent, offset);
                    FingerJoints[(handBase + f) * 3 + j] = parent;
                }
            }
        }

        static HumanBodyBones ProximalBone(bool left, int finger)
        {
            int baseBone = left
                ? (int)HumanBodyBones.LeftThumbProximal
                : (int)HumanBodyBones.RightThumbProximal;
            return (HumanBodyBones)(baseBone + finger * 3);
        }

        readonly List<HumanBone> _human = new List<HumanBone>();

        Transform MakeBone(HumanBodyBones bone, Transform parent, Vector3 localPosition)
        {
            var go = new GameObject(HumanTrait.BoneName[(int)bone]);
            Transform t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            _all.Add(t);
            _human.Add(new HumanBone
            {
                boneName = go.name,
                humanName = HumanTrait.BoneName[(int)bone],
                limit = new HumanLimit { useDefaultValues = true },
            });
            return t;
        }

        public void Dispose()
        {
            if (Avatar != null) Object.DestroyImmediate(Avatar);
            if (Root != null) Object.DestroyImmediate(Root);
            Root = null;
            Animator = null;
            Avatar = null;
        }
    }
}
