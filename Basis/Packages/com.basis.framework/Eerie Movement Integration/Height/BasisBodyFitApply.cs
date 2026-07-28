using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.IK
{
    public static class BasisBodyFitApply
    {
        /// <summary>
        /// Number of bones a body fit touches. The local rig applies these through the pose skeleton
        /// (which also rescales BindLength); remotes write them straight onto the transforms. Both read
        /// the same table via <see cref="CollectBones"/>/<see cref="CollectScales"/> so a bone added to
        /// the fit can never reach one path and miss the other.
        /// </summary>
        public const int BoneCount = 17;

        /// <summary>
        /// Fills <paramref name="bones"/> (length <see cref="BoneCount"/>) with the fitted bones in the
        /// canonical order. Slots whose bone is absent on this rig are left null.
        /// </summary>
        public static void CollectBones(BasisTransformMapping mapping, Transform[] bones)
        {
            if (mapping == null || bones == null || bones.Length < BoneCount)
            {
                return;
            }

            bones[0] = mapping.leftLowerArm;
            bones[1] = mapping.leftHand;
            bones[2] = mapping.HasleftUpperArmTwist ? mapping.leftUpperArmTwist : null;
            bones[3] = mapping.HasleftLowerArmTwist ? mapping.leftLowerArmTwist : null;
            bones[4] = mapping.RightLowerArm;
            bones[5] = mapping.rightHand;
            bones[6] = mapping.HasRightUpperArmTwist ? mapping.RightUpperArmTwist : null;
            bones[7] = mapping.HasRightLowerArmTwist ? mapping.RightLowerArmTwist : null;

            bones[8] = mapping.LeftLowerLeg;
            bones[9] = mapping.leftFoot;
            bones[10] = mapping.RightLowerLeg;
            bones[11] = mapping.rightFoot;

            bones[12] = mapping.spine;
            bones[13] = mapping.chest;
            bones[14] = mapping.Upperchest;
            bones[15] = mapping.neck;
            bones[16] = mapping.head;
        }

        /// <summary>
        /// Fills <paramref name="scales"/> (length <see cref="BoneCount"/>) with the per-bone scale for
        /// this fit, in the same order as <see cref="CollectBones"/>. A half that did not fit stays at 1.
        /// </summary>
        public static void CollectScales(in BasisBodyFitResult fit, float[] scales)
        {
            if (scales == null || scales.Length < BoneCount)
            {
                return;
            }

            float arm = fit.HasArmFit ? fit.ArmScale : 1f;
            for (int i = 0; i < 8; i++)
            {
                scales[i] = arm;
            }

            float leg = fit.HasBodyFit ? fit.LegScale : 1f;
            for (int i = 8; i < 12; i++)
            {
                scales[i] = leg;
            }

            float torso = fit.HasBodyFit ? fit.TorsoScale : 1f;
            for (int i = 12; i < BoneCount; i++)
            {
                scales[i] = torso;
            }
        }

        static readonly Transform[] s_bones = new Transform[BoneCount];
        static readonly float[] s_scales = new float[BoneCount];

        public static void Apply(BasisPoseSkeleton skeleton, BasisTransformMapping mapping, in BasisBodyFitResult fit)
        {
            if (skeleton == null || !skeleton.IsCreated || mapping == null)
            {
                return;
            }

            skeleton.ResetFit();

            CollectBones(mapping, s_bones);
            CollectScales(in fit, s_scales);

            for (int i = 0; i < BoneCount; i++)
            {
                if (s_bones[i] != null && s_scales[i] != 1f)
                {
                    skeleton.SetFitScale(s_bones[i], s_scales[i]);
                }
            }
        }
    }
}
