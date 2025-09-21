/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

public partial class OVRUnityHumanoidSkeletonRetargeter
{
    /// <summary>
    /// Implements the <see cref="OVRHumanBodyBonesMappingsInterface"/> interface, and contains mappings
    /// that are relevant to retargeting by classes such as <see cref="OVRUnityHumanoidSkeletonRetargeter"/>.
    /// This includes associations between <see cref="HumanBodyBones"/> and <see cref="BoneId"/>,
    /// <see cref="HumanBodyBones"/> and <see cref="BodySection"/>.
    /// There are also mappings between a body tracking or humanoid bone and joint pairs used for
    /// retargeting.
    /// <remarks>
    /// This is a default implementation, which means that if you have a custom character is not
    /// compatible then you will need to implement <see cref="OVRHumanBodyBonesMappings"/> separately.
    /// </remarks>
    /// </summary>
    public class OVRHumanBodyBonesMappings : OVRHumanBodyBonesMappingsInterface
    {
        /// <summary>
        /// The mapping between <see cref="HumanBodyBones"/> a tuple of <see cref="HumanBodyBones"/>.
        /// <remarks>
        /// The tuple is a joint pair that retargeting uses to create a <see cref="HumanBodyBones"/>'s orientation
        /// during runtime. Define this field so that you can influence what kind of tuple is
        /// used during retargeting.
        /// </remarks>
        /// </summary>
        public Dictionary<HumanBodyBones, Tuple<HumanBodyBones, HumanBodyBones>> GetBoneToJointPair
            => BoneToJointPair;

        /// <summary>
        /// The mapping between <see cref="HumanBodyBones"/> and <see cref="BodySection"/>. Define
        /// this mapping to associate a body with parts of the body, such as the back, left arm,
        /// right leg, and so on.
        /// </summary>
        public Dictionary<HumanBodyBones, BodySection> GetBoneToBodySection => BoneToBodySection;

        /// <summary>
        /// Returns <see cref="BoneId"> to <see cref="HumanBodyBones"/> mapping for full body
        /// characters. Since retargeting retargets to humanoid characters, it uses this field
        /// to map body tracking bones to humanoid bones.
        /// </summary>
        public Dictionary<OVRSkeleton.BoneId, HumanBodyBones> GetFullBodyBoneIdToHumanBodyBone
            => FullBodyBoneIdToHumanBodyBone;

        /// <summary>
        /// Returns <see cref="BoneId"> to <see cref="HumanBodyBones"/> mapping for upper body
        /// characters. Since retargeting retargets to humanoid characters, it uses this field
        /// to map body tracking bones to humanoid bones.
        /// </summary>
        public Dictionary<OVRSkeleton.BoneId, HumanBodyBones> GetBoneIdToHumanBodyBone
            => BoneIdToHumanBodyBone;

        /// <summary>
        /// The mapping between <see cref="BoneId"/> a tuple of <see cref="BoneId"/>.
        /// Retargeting uses this tuple or joint pair to create a <see cref="BoneId"/>'s orientation
        /// during retargeting. Define this field so that you can influence what kind of tuple is
        /// used during retargeting. Intended for the retargeting source (i.e. body tracking)
        /// assuming full body tracking is used.
        /// </summary>
        public Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>
            GetFullBodyBoneIdToJointPair => FullBoneIdToJointPair;

        /// <summary>
        /// The mapping between <see cref="BoneId"/> a tuple of <see cref="BoneId"/>.
        /// Retargeting uses this tuple or joint pair to create a <see cref="BoneId"/>'s orientation
        /// during retargeting. Define this field so that you can influence what kind of tuple is
        /// used during retargeting. Intended for the retargeting source (i.e. body tracking)
        /// assuming upper body tracking is used.
        /// </summary>
        public Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>
            GetBoneIdToJointPair => BoneIdToJointPair;

        /// <summary>
        /// Since humanoid characters can possibly be described as large sections, such the
        /// back, left leg, right arm, and so on, <see cref="BodySection"/> is defined to
        /// define those sections. Use this enum to associate a body section with
        /// a character's bone.
        /// </summary>
        public enum BodySection
        {
            LeftLeg,
            LeftFoot,
            RightLeg,
            RightFoot,
            LeftArm,
            LeftHand,
            RightArm,
            RightHand,
            Hips,
            Back,
            Neck,
            Head
        };

        /// <summary>
        /// Body tracking bone IDs that should be exposed through the inspector.
        /// <see cref="OVRPlugin.BoneId"> has enum values that map to the same integers, which would not work
        /// with a serialized field that expects unique integers. <see cref="FullBodyTrackingBoneId">
        /// is an enum that restricts BoneId to the values that are relevant for full body characters
        /// and avoids redundant values.
        /// </summary>
        public enum FullBodyTrackingBoneId
        {
            FullBody_Start = OVRPlugin.BoneId.FullBody_Start,
            FullBody_Root = OVRPlugin.BoneId.FullBody_Root,
            FullBody_Hips = OVRPlugin.BoneId.FullBody_Hips,
            FullBody_SpineLower = OVRPlugin.BoneId.FullBody_SpineLower,
            FullBody_SpineMiddle = OVRPlugin.BoneId.FullBody_SpineMiddle,
            FullBody_SpineUpper = OVRPlugin.BoneId.FullBody_SpineUpper,
            FullBody_Chest = OVRPlugin.BoneId.FullBody_Chest,
            FullBody_Neck = OVRPlugin.BoneId.FullBody_Neck,
            FullBody_Head = OVRPlugin.BoneId.FullBody_Head,
            FullBody_LeftShoulder = OVRPlugin.BoneId.FullBody_LeftShoulder,
            FullBody_LeftScapula = OVRPlugin.BoneId.FullBody_LeftScapula,
            FullBody_LeftArmUpper = OVRPlugin.BoneId.FullBody_LeftArmUpper,
            FullBody_LeftArmLower = OVRPlugin.BoneId.FullBody_LeftArmLower,
            FullBody_LeftHandWristTwist = OVRPlugin.BoneId.FullBody_LeftHandWristTwist,
            FullBody_RightShoulder = OVRPlugin.BoneId.FullBody_RightShoulder,
            FullBody_RightScapula = OVRPlugin.BoneId.FullBody_RightScapula,
            FullBody_RightArmUpper = OVRPlugin.BoneId.FullBody_RightArmUpper,
            FullBody_RightArmLower = OVRPlugin.BoneId.FullBody_RightArmLower,
            FullBody_RightHandWristTwist = OVRPlugin.BoneId.FullBody_RightHandWristTwist,
            FullBody_LeftHandPalm = OVRPlugin.BoneId.FullBody_LeftHandPalm,
            FullBody_LeftHandWrist = OVRPlugin.BoneId.FullBody_LeftHandWrist,
            FullBody_LeftHandThumbMetacarpal = OVRPlugin.BoneId.FullBody_LeftHandThumbMetacarpal,
            FullBody_LeftHandThumbProximal = OVRPlugin.BoneId.FullBody_LeftHandThumbProximal,
            FullBody_LeftHandThumbDistal = OVRPlugin.BoneId.FullBody_LeftHandThumbDistal,
            FullBody_LeftHandThumbTip = OVRPlugin.BoneId.FullBody_LeftHandThumbTip,
            FullBody_LeftHandIndexMetacarpal = OVRPlugin.BoneId.FullBody_LeftHandIndexMetacarpal,
            FullBody_LeftHandIndexProximal = OVRPlugin.BoneId.FullBody_LeftHandIndexProximal,
            FullBody_LeftHandIndexIntermediate = OVRPlugin.BoneId.FullBody_LeftHandIndexIntermediate,
            FullBody_LeftHandIndexDistal = OVRPlugin.BoneId.FullBody_LeftHandIndexDistal,
            FullBody_LeftHandIndexTip = OVRPlugin.BoneId.FullBody_LeftHandIndexTip,
            FullBody_LeftHandMiddleMetacarpal = OVRPlugin.BoneId.FullBody_LeftHandMiddleMetacarpal,
            FullBody_LeftHandMiddleProximal = OVRPlugin.BoneId.FullBody_LeftHandMiddleProximal,
            FullBody_LeftHandMiddleIntermediate = OVRPlugin.BoneId.FullBody_LeftHandMiddleIntermediate,
            FullBody_LeftHandMiddleDistal = OVRPlugin.BoneId.FullBody_LeftHandMiddleDistal,
            FullBody_LeftHandMiddleTip = OVRPlugin.BoneId.FullBody_LeftHandMiddleTip,
            FullBody_LeftHandRingMetacarpal = OVRPlugin.BoneId.FullBody_LeftHandRingMetacarpal,
            FullBody_LeftHandRingProximal = OVRPlugin.BoneId.FullBody_LeftHandRingProximal,
            FullBody_LeftHandRingIntermediate = OVRPlugin.BoneId.FullBody_LeftHandRingIntermediate,
            FullBody_LeftHandRingDistal = OVRPlugin.BoneId.FullBody_LeftHandRingDistal,
            FullBody_LeftHandRingTip = OVRPlugin.BoneId.FullBody_LeftHandRingTip,
            FullBody_LeftHandLittleMetacarpal = OVRPlugin.BoneId.FullBody_LeftHandLittleMetacarpal,
            FullBody_LeftHandLittleProximal = OVRPlugin.BoneId.FullBody_LeftHandLittleProximal,
            FullBody_LeftHandLittleIntermediate = OVRPlugin.BoneId.FullBody_LeftHandLittleIntermediate,
            FullBody_LeftHandLittleDistal = OVRPlugin.BoneId.FullBody_LeftHandLittleDistal,
            FullBody_LeftHandLittleTip = OVRPlugin.BoneId.FullBody_LeftHandLittleTip,
            FullBody_RightHandPalm = OVRPlugin.BoneId.FullBody_RightHandPalm,
            FullBody_RightHandWrist = OVRPlugin.BoneId.FullBody_RightHandWrist,
            FullBody_RightHandThumbMetacarpal = OVRPlugin.BoneId.FullBody_RightHandThumbMetacarpal,
            FullBody_RightHandThumbProximal = OVRPlugin.BoneId.FullBody_RightHandThumbProximal,
            FullBody_RightHandThumbDistal = OVRPlugin.BoneId.FullBody_RightHandThumbDistal,
            FullBody_RightHandThumbTip = OVRPlugin.BoneId.FullBody_RightHandThumbTip,
            FullBody_RightHandIndexMetacarpal = OVRPlugin.BoneId.FullBody_RightHandIndexMetacarpal,
            FullBody_RightHandIndexProximal = OVRPlugin.BoneId.FullBody_RightHandIndexProximal,
            FullBody_RightHandIndexIntermediate = OVRPlugin.BoneId.FullBody_RightHandIndexIntermediate,
            FullBody_RightHandIndexDistal = OVRPlugin.BoneId.FullBody_RightHandIndexDistal,
            FullBody_RightHandIndexTip = OVRPlugin.BoneId.FullBody_RightHandIndexTip,
            FullBody_RightHandMiddleMetacarpal = OVRPlugin.BoneId.FullBody_RightHandMiddleMetacarpal,
            FullBody_RightHandMiddleProximal = OVRPlugin.BoneId.FullBody_RightHandMiddleProximal,
            FullBody_RightHandMiddleIntermediate = OVRPlugin.BoneId.FullBody_RightHandMiddleIntermediate,
            FullBody_RightHandMiddleDistal = OVRPlugin.BoneId.FullBody_RightHandMiddleDistal,
            FullBody_RightHandMiddleTip = OVRPlugin.BoneId.FullBody_RightHandMiddleTip,
            FullBody_RightHandRingMetacarpal = OVRPlugin.BoneId.FullBody_RightHandRingMetacarpal,
            FullBody_RightHandRingProximal = OVRPlugin.BoneId.FullBody_RightHandRingProximal,
            FullBody_RightHandRingIntermediate = OVRPlugin.BoneId.FullBody_RightHandRingIntermediate,
            FullBody_RightHandRingDistal = OVRPlugin.BoneId.FullBody_RightHandRingDistal,
            FullBody_RightHandRingTip = OVRPlugin.BoneId.FullBody_RightHandRingTip,
            FullBody_RightHandLittleMetacarpal = OVRPlugin.BoneId.FullBody_RightHandLittleMetacarpal,
            FullBody_RightHandLittleProximal = OVRPlugin.BoneId.FullBody_RightHandLittleProximal,
            FullBody_RightHandLittleIntermediate = OVRPlugin.BoneId.FullBody_RightHandLittleIntermediate,
            FullBody_RightHandLittleDistal = OVRPlugin.BoneId.FullBody_RightHandLittleDistal,
            FullBody_RightHandLittleTip = OVRPlugin.BoneId.FullBody_RightHandLittleTip,
            FullBody_LeftUpperLeg = OVRPlugin.BoneId.FullBody_LeftUpperLeg,
            FullBody_LeftLowerLeg = OVRPlugin.BoneId.FullBody_LeftLowerLeg,
            FullBody_LeftFootAnkleTwist = OVRPlugin.BoneId.FullBody_LeftFootAnkleTwist,
            FullBody_LeftFootAnkle = OVRPlugin.BoneId.FullBody_LeftFootAnkle,
            FullBody_LeftFootSubtalar = OVRPlugin.BoneId.FullBody_LeftFootSubtalar,
            FullBody_LeftFootTransverse = OVRPlugin.BoneId.FullBody_LeftFootTransverse,
            FullBody_LeftFootBall = OVRPlugin.BoneId.FullBody_LeftFootBall,
            FullBody_RightUpperLeg = OVRPlugin.BoneId.FullBody_RightUpperLeg,
            FullBody_RightLowerLeg = OVRPlugin.BoneId.FullBody_RightLowerLeg,
            FullBody_RightFootAnkleTwist = OVRPlugin.BoneId.FullBody_RightFootAnkleTwist,
            FullBody_RightFootAnkle = OVRPlugin.BoneId.FullBody_RightFootAnkle,
            FullBody_RightFootSubtalar = OVRPlugin.BoneId.FullBody_RightFootSubtalar,
            FullBody_RightFootTransverse = OVRPlugin.BoneId.FullBody_RightFootTransverse,
            FullBody_RightFootBall = OVRPlugin.BoneId.FullBody_RightFootBall,
            FullBody_End = OVRPlugin.BoneId.FullBody_End,

            // add new bones here

            NoOverride = OVRPlugin.BoneId.FullBody_End + 1,
            Remove = OVRPlugin.BoneId.FullBody_End + 2
        };

        /// <summary>
        /// Body tracking bone IDs that should be exposed through the inspector.
        /// <see cref="OVRPlugin.BoneId"> has enum values that map to the same integers, which would not work
        /// with a serialized field that expects unique integers. <see cref="BodyTrackingBoneId">
        /// is an enum that restricts BoneId to the values that are relevant for full body characters
        /// and avoids redundant values.
        /// </summary>
        public enum BodyTrackingBoneId
        {
            Body_Start = OVRPlugin.BoneId.Body_Start,
            Body_Root = OVRPlugin.BoneId.Body_Root,
            Body_Hips = OVRPlugin.BoneId.Body_Hips,
            Body_SpineLower = OVRPlugin.BoneId.Body_SpineLower,
            Body_SpineMiddle = OVRPlugin.BoneId.Body_SpineMiddle,
            Body_SpineUpper = OVRPlugin.BoneId.Body_SpineUpper,
            Body_Chest = OVRPlugin.BoneId.Body_Chest,
            Body_Neck = OVRPlugin.BoneId.Body_Neck,
            Body_Head = OVRPlugin.BoneId.Body_Head,
            Body_LeftShoulder = OVRPlugin.BoneId.Body_LeftShoulder,
            Body_LeftScapula = OVRPlugin.BoneId.Body_LeftScapula,
            Body_LeftArmUpper = OVRPlugin.BoneId.Body_LeftArmUpper,
            Body_LeftArmLower = OVRPlugin.BoneId.Body_LeftArmLower,
            Body_LeftHandWristTwist = OVRPlugin.BoneId.Body_LeftHandWristTwist,
            Body_RightShoulder = OVRPlugin.BoneId.Body_RightShoulder,
            Body_RightScapula = OVRPlugin.BoneId.Body_RightScapula,
            Body_RightArmUpper = OVRPlugin.BoneId.Body_RightArmUpper,
            Body_RightArmLower = OVRPlugin.BoneId.Body_RightArmLower,
            Body_RightHandWristTwist = OVRPlugin.BoneId.Body_RightHandWristTwist,
            Body_LeftHandPalm = OVRPlugin.BoneId.Body_LeftHandPalm,
            Body_LeftHandWrist = OVRPlugin.BoneId.Body_LeftHandWrist,
            Body_LeftHandThumbMetacarpal = OVRPlugin.BoneId.Body_LeftHandThumbMetacarpal,
            Body_LeftHandThumbProximal = OVRPlugin.BoneId.Body_LeftHandThumbProximal,
            Body_LeftHandThumbDistal = OVRPlugin.BoneId.Body_LeftHandThumbDistal,
            Body_LeftHandThumbTip = OVRPlugin.BoneId.Body_LeftHandThumbTip,
            Body_LeftHandIndexMetacarpal = OVRPlugin.BoneId.Body_LeftHandIndexMetacarpal,
            Body_LeftHandIndexProximal = OVRPlugin.BoneId.Body_LeftHandIndexProximal,
            Body_LeftHandIndexIntermediate = OVRPlugin.BoneId.Body_LeftHandIndexIntermediate,
            Body_LeftHandIndexDistal = OVRPlugin.BoneId.Body_LeftHandIndexDistal,
            Body_LeftHandIndexTip = OVRPlugin.BoneId.Body_LeftHandIndexTip,
            Body_LeftHandMiddleMetacarpal = OVRPlugin.BoneId.Body_LeftHandMiddleMetacarpal,
            Body_LeftHandMiddleProximal = OVRPlugin.BoneId.Body_LeftHandMiddleProximal,
            Body_LeftHandMiddleIntermediate = OVRPlugin.BoneId.Body_LeftHandMiddleIntermediate,
            Body_LeftHandMiddleDistal = OVRPlugin.BoneId.Body_LeftHandMiddleDistal,
            Body_LeftHandMiddleTip = OVRPlugin.BoneId.Body_LeftHandMiddleTip,
            Body_LeftHandRingMetacarpal = OVRPlugin.BoneId.Body_LeftHandRingMetacarpal,
            Body_LeftHandRingProximal = OVRPlugin.BoneId.Body_LeftHandRingProximal,
            Body_LeftHandRingIntermediate = OVRPlugin.BoneId.Body_LeftHandRingIntermediate,
            Body_LeftHandRingDistal = OVRPlugin.BoneId.Body_LeftHandRingDistal,
            Body_LeftHandRingTip = OVRPlugin.BoneId.Body_LeftHandRingTip,
            Body_LeftHandLittleMetacarpal = OVRPlugin.BoneId.Body_LeftHandLittleMetacarpal,
            Body_LeftHandLittleProximal = OVRPlugin.BoneId.Body_LeftHandLittleProximal,
            Body_LeftHandLittleIntermediate = OVRPlugin.BoneId.Body_LeftHandLittleIntermediate,
            Body_LeftHandLittleDistal = OVRPlugin.BoneId.Body_LeftHandLittleDistal,
            Body_LeftHandLittleTip = OVRPlugin.BoneId.Body_LeftHandLittleTip,
            Body_RightHandPalm = OVRPlugin.BoneId.Body_RightHandPalm,
            Body_RightHandWrist = OVRPlugin.BoneId.Body_RightHandWrist,
            Body_RightHandThumbMetacarpal = OVRPlugin.BoneId.Body_RightHandThumbMetacarpal,
            Body_RightHandThumbProximal = OVRPlugin.BoneId.Body_RightHandThumbProximal,
            Body_RightHandThumbDistal = OVRPlugin.BoneId.Body_RightHandThumbDistal,
            Body_RightHandThumbTip = OVRPlugin.BoneId.Body_RightHandThumbTip,
            Body_RightHandIndexMetacarpal = OVRPlugin.BoneId.Body_RightHandIndexMetacarpal,
            Body_RightHandIndexProximal = OVRPlugin.BoneId.Body_RightHandIndexProximal,
            Body_RightHandIndexIntermediate = OVRPlugin.BoneId.Body_RightHandIndexIntermediate,
            Body_RightHandIndexDistal = OVRPlugin.BoneId.Body_RightHandIndexDistal,
            Body_RightHandIndexTip = OVRPlugin.BoneId.Body_RightHandIndexTip,
            Body_RightHandMiddleMetacarpal = OVRPlugin.BoneId.Body_RightHandMiddleMetacarpal,
            Body_RightHandMiddleProximal = OVRPlugin.BoneId.Body_RightHandMiddleProximal,
            Body_RightHandMiddleIntermediate = OVRPlugin.BoneId.Body_RightHandMiddleIntermediate,
            Body_RightHandMiddleDistal = OVRPlugin.BoneId.Body_RightHandMiddleDistal,
            Body_RightHandMiddleTip = OVRPlugin.BoneId.Body_RightHandMiddleTip,
            Body_RightHandRingMetacarpal = OVRPlugin.BoneId.Body_RightHandRingMetacarpal,
            Body_RightHandRingProximal = OVRPlugin.BoneId.Body_RightHandRingProximal,
            Body_RightHandRingIntermediate = OVRPlugin.BoneId.Body_RightHandRingIntermediate,
            Body_RightHandRingDistal = OVRPlugin.BoneId.Body_RightHandRingDistal,
            Body_RightHandRingTip = OVRPlugin.BoneId.Body_RightHandRingTip,
            Body_RightHandLittleMetacarpal = OVRPlugin.BoneId.Body_RightHandLittleMetacarpal,
            Body_RightHandLittleProximal = OVRPlugin.BoneId.Body_RightHandLittleProximal,
            Body_RightHandLittleIntermediate = OVRPlugin.BoneId.Body_RightHandLittleIntermediate,
            Body_RightHandLittleDistal = OVRPlugin.BoneId.Body_RightHandLittleDistal,
            Body_RightHandLittleTip = OVRPlugin.BoneId.Body_RightHandLittleTip,
            Body_End = OVRPlugin.BoneId.Body_End,

            // add new bones here

            NoOverride = OVRPlugin.BoneId.Body_End + 1,
            Remove = OVRPlugin.BoneId.Body_End + 2
        };

        /// <summary>
        /// The map associates a pair of bones for each <see cref="HumanBodyBones"/>.
        /// Retargeters such as <see cref="OVRUnityHumanoidSkeletonRetargeter"/> and
        /// <see cref="OVRSkeletonMetadata"/> use these pairs to creates axes for the purposes
        /// of retargeting.
        /// </summary>
        public static readonly Dictionary<HumanBodyBones, Tuple<HumanBodyBones, HumanBodyBones>>
            BoneToJointPair = new Dictionary<HumanBodyBones, Tuple<HumanBodyBones, HumanBodyBones>>()
            {
                {
                    HumanBodyBones.Neck,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Neck, HumanBodyBones.Head)
                },
                {
                    HumanBodyBones.Head,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Neck, HumanBodyBones.Head)
                },
                {
                    HumanBodyBones.LeftEye,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Head, HumanBodyBones.LeftEye)
                },
                {
                    HumanBodyBones.RightEye,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Head, HumanBodyBones.RightEye)
                },
                {
                    HumanBodyBones.Jaw,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Head, HumanBodyBones.Jaw)
                },

                {
                    HumanBodyBones.Hips,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Hips, HumanBodyBones.Spine)
                },
                {
                    HumanBodyBones.Spine,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Spine, HumanBodyBones.Chest)
                },
                {
                    HumanBodyBones.Chest,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.Chest, HumanBodyBones.UpperChest)
                },
                {
                    HumanBodyBones.UpperChest,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.UpperChest, HumanBodyBones.Neck)
                },

                {
                    HumanBodyBones.LeftShoulder,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm)
                },
                {
                    HumanBodyBones.LeftUpperArm,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm)
                },
                {
                    HumanBodyBones.LeftLowerArm,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand)
                },
                {
                    HumanBodyBones.LeftHand,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftHand,
                        HumanBodyBones.LeftMiddleProximal)
                },

                {
                    HumanBodyBones.RightShoulder,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightShoulder,
                        HumanBodyBones.RightUpperArm)
                },
                {
                    HumanBodyBones.RightUpperArm,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightLowerArm)
                },
                {
                    HumanBodyBones.RightLowerArm,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)
                },
                {
                    HumanBodyBones.RightHand,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightHand,
                        HumanBodyBones.RightMiddleProximal)
                },

                {
                    HumanBodyBones.RightUpperLeg,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightUpperLeg,
                        HumanBodyBones.RightLowerLeg)
                },
                {
                    HumanBodyBones.RightLowerLeg,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)
                },
                {
                    HumanBodyBones.RightFoot,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightFoot, HumanBodyBones.RightToes)
                },
                {
                    HumanBodyBones.RightToes,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightFoot, HumanBodyBones.RightToes)
                },

                {
                    HumanBodyBones.LeftUpperLeg,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg)
                },
                {
                    HumanBodyBones.LeftLowerLeg,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot)
                },
                {
                    HumanBodyBones.LeftFoot,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes)
                },
                {
                    HumanBodyBones.LeftToes,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes)
                },

                {
                    HumanBodyBones.LeftThumbProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftThumbProximal,
                        HumanBodyBones.LeftThumbIntermediate)
                },
                {
                    HumanBodyBones.LeftThumbIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftThumbIntermediate,
                        HumanBodyBones.LeftThumbDistal)
                },
                {
                    HumanBodyBones.LeftThumbDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftThumbDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.LeftIndexProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftIndexProximal,
                        HumanBodyBones.LeftIndexIntermediate)
                },
                {
                    HumanBodyBones.LeftIndexIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftIndexIntermediate,
                        HumanBodyBones.LeftIndexDistal)
                },
                {
                    HumanBodyBones.LeftIndexDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftIndexDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.LeftMiddleProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftMiddleProximal,
                        HumanBodyBones.LeftMiddleIntermediate)
                },
                {
                    HumanBodyBones.LeftMiddleIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftMiddleIntermediate,
                        HumanBodyBones.LeftMiddleDistal)
                },
                {
                    HumanBodyBones.LeftMiddleDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.LeftRingProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftRingProximal,
                        HumanBodyBones.LeftRingIntermediate)
                },
                {
                    HumanBodyBones.LeftRingIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftRingIntermediate,
                        HumanBodyBones.LeftRingDistal)
                },
                {
                    HumanBodyBones.LeftRingDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftRingDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.LeftLittleProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftLittleProximal,
                        HumanBodyBones.LeftLittleIntermediate)
                },
                {
                    HumanBodyBones.LeftLittleIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftLittleIntermediate,
                        HumanBodyBones.LeftLittleDistal)
                },
                {
                    HumanBodyBones.LeftLittleDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.LeftLittleDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.RightThumbProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightThumbProximal,
                        HumanBodyBones.RightThumbIntermediate)
                },
                {
                    HumanBodyBones.RightThumbIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightThumbIntermediate,
                        HumanBodyBones.RightThumbDistal)
                },
                {
                    HumanBodyBones.RightThumbDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightThumbDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.RightIndexProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightIndexProximal,
                        HumanBodyBones.RightIndexIntermediate)
                },
                {
                    HumanBodyBones.RightIndexIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightIndexIntermediate,
                        HumanBodyBones.RightIndexDistal)
                },
                {
                    HumanBodyBones.RightIndexDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightIndexDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.RightMiddleProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightMiddleProximal,
                        HumanBodyBones.RightMiddleIntermediate)
                },
                {
                    HumanBodyBones.RightMiddleIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightMiddleIntermediate,
                        HumanBodyBones.RightMiddleDistal)
                },
                {
                    HumanBodyBones.RightMiddleDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightMiddleDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.RightRingProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightRingProximal,
                        HumanBodyBones.RightRingIntermediate)
                },
                {
                    HumanBodyBones.RightRingIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightRingIntermediate,
                        HumanBodyBones.RightRingDistal)
                },
                {
                    HumanBodyBones.RightRingDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightRingDistal, HumanBodyBones.LastBone)
                }, // Invalid.

                {
                    HumanBodyBones.RightLittleProximal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightLittleProximal,
                        HumanBodyBones.RightLittleIntermediate)
                },
                {
                    HumanBodyBones.RightLittleIntermediate,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightLittleIntermediate,
                        HumanBodyBones.RightLittleDistal)
                },
                {
                    HumanBodyBones.RightLittleDistal,
                    new Tuple<HumanBodyBones, HumanBodyBones>(HumanBodyBones.RightLittleDistal, HumanBodyBones.LastBone)
                }, // Invalid.
            };

        /// <summary>
        /// Maps <see cref="HumanBodyBones"/> to <see cref="BodySection"/>. Use this
        /// to determine which part of the body (i.e. back) corresponds with a humanoid bone.
        /// </summary>
        public static readonly Dictionary<HumanBodyBones, BodySection> BoneToBodySection =
            new Dictionary<HumanBodyBones, BodySection>()
            {
                { HumanBodyBones.Neck, BodySection.Neck },

                { HumanBodyBones.Head, BodySection.Head },
                { HumanBodyBones.LeftEye, BodySection.Head },
                { HumanBodyBones.RightEye, BodySection.Head },
                { HumanBodyBones.Jaw, BodySection.Head },

                { HumanBodyBones.Hips, BodySection.Hips },

                { HumanBodyBones.Spine, BodySection.Back },
                { HumanBodyBones.Chest, BodySection.Back },
                { HumanBodyBones.UpperChest, BodySection.Back },

                { HumanBodyBones.RightShoulder, BodySection.RightArm },
                { HumanBodyBones.RightUpperArm, BodySection.RightArm },
                { HumanBodyBones.RightLowerArm, BodySection.RightArm },
                { HumanBodyBones.RightHand, BodySection.RightArm },

                { HumanBodyBones.LeftShoulder, BodySection.LeftArm },
                { HumanBodyBones.LeftUpperArm, BodySection.LeftArm },
                { HumanBodyBones.LeftLowerArm, BodySection.LeftArm },
                { HumanBodyBones.LeftHand, BodySection.LeftArm },

                { HumanBodyBones.LeftUpperLeg, BodySection.LeftLeg },
                { HumanBodyBones.LeftLowerLeg, BodySection.LeftLeg },

                { HumanBodyBones.LeftFoot, BodySection.LeftFoot },
                { HumanBodyBones.LeftToes, BodySection.LeftFoot },

                { HumanBodyBones.RightUpperLeg, BodySection.RightLeg },
                { HumanBodyBones.RightLowerLeg, BodySection.RightLeg },

                { HumanBodyBones.RightFoot, BodySection.RightFoot },
                { HumanBodyBones.RightToes, BodySection.RightFoot },

                { HumanBodyBones.LeftThumbProximal, BodySection.LeftHand },
                { HumanBodyBones.LeftThumbIntermediate, BodySection.LeftHand },
                { HumanBodyBones.LeftThumbDistal, BodySection.LeftHand },
                { HumanBodyBones.LeftIndexProximal, BodySection.LeftHand },
                { HumanBodyBones.LeftIndexIntermediate, BodySection.LeftHand },
                { HumanBodyBones.LeftIndexDistal, BodySection.LeftHand },
                { HumanBodyBones.LeftMiddleProximal, BodySection.LeftHand },
                { HumanBodyBones.LeftMiddleIntermediate, BodySection.LeftHand },
                { HumanBodyBones.LeftMiddleDistal, BodySection.LeftHand },
                { HumanBodyBones.LeftRingProximal, BodySection.LeftHand },
                { HumanBodyBones.LeftRingIntermediate, BodySection.LeftHand },
                { HumanBodyBones.LeftRingDistal, BodySection.LeftHand },
                { HumanBodyBones.LeftLittleProximal, BodySection.LeftHand },
                { HumanBodyBones.LeftLittleIntermediate, BodySection.LeftHand },
                { HumanBodyBones.LeftLittleDistal, BodySection.LeftHand },

                { HumanBodyBones.RightThumbProximal, BodySection.RightHand },
                { HumanBodyBones.RightThumbIntermediate, BodySection.RightHand },
                { HumanBodyBones.RightThumbDistal, BodySection.RightHand },
                { HumanBodyBones.RightIndexProximal, BodySection.RightHand },
                { HumanBodyBones.RightIndexIntermediate, BodySection.RightHand },
                { HumanBodyBones.RightIndexDistal, BodySection.RightHand },
                { HumanBodyBones.RightMiddleProximal, BodySection.RightHand },
                { HumanBodyBones.RightMiddleIntermediate, BodySection.RightHand },
                { HumanBodyBones.RightMiddleDistal, BodySection.RightHand },
                { HumanBodyBones.RightRingProximal, BodySection.RightHand },
                { HumanBodyBones.RightRingIntermediate, BodySection.RightHand },
                { HumanBodyBones.RightRingDistal, BodySection.RightHand },
                { HumanBodyBones.RightLittleProximal, BodySection.RightHand },
                { HumanBodyBones.RightLittleIntermediate, BodySection.RightHand },
                { HumanBodyBones.RightLittleDistal, BodySection.RightHand },
            };

        /// <summary>
        /// Maps full body body tracking bones with <see cref="HumanBodyBones"/> bones.
        /// Use this field to retarget from a body tracking to its humanoid equivalent.
        /// </summary>
        public static readonly Dictionary<OVRSkeleton.BoneId, HumanBodyBones> FullBodyBoneIdToHumanBodyBone =
            new Dictionary<OVRSkeleton.BoneId, HumanBodyBones>()
            {
                { OVRSkeleton.BoneId.FullBody_Hips, HumanBodyBones.Hips },
                { OVRSkeleton.BoneId.FullBody_SpineLower, HumanBodyBones.Spine },
                { OVRSkeleton.BoneId.FullBody_SpineUpper, HumanBodyBones.Chest },
                { OVRSkeleton.BoneId.FullBody_Chest, HumanBodyBones.UpperChest },
                { OVRSkeleton.BoneId.FullBody_Neck, HumanBodyBones.Neck },
                { OVRSkeleton.BoneId.FullBody_Head, HumanBodyBones.Head },

                { OVRSkeleton.BoneId.FullBody_LeftShoulder, HumanBodyBones.LeftShoulder },
                { OVRSkeleton.BoneId.FullBody_LeftArmUpper, HumanBodyBones.LeftUpperArm },
                { OVRSkeleton.BoneId.FullBody_LeftArmLower, HumanBodyBones.LeftLowerArm },
                { OVRSkeleton.BoneId.FullBody_LeftHandWrist, HumanBodyBones.LeftHand },

                { OVRSkeleton.BoneId.FullBody_RightShoulder, HumanBodyBones.RightShoulder },
                { OVRSkeleton.BoneId.FullBody_RightArmUpper, HumanBodyBones.RightUpperArm },
                { OVRSkeleton.BoneId.FullBody_RightArmLower, HumanBodyBones.RightLowerArm },
                { OVRSkeleton.BoneId.FullBody_RightHandWrist, HumanBodyBones.RightHand },

                { OVRSkeleton.BoneId.FullBody_LeftHandThumbMetacarpal, HumanBodyBones.LeftThumbProximal },
                { OVRSkeleton.BoneId.FullBody_LeftHandThumbProximal, HumanBodyBones.LeftThumbIntermediate },
                { OVRSkeleton.BoneId.FullBody_LeftHandThumbDistal, HumanBodyBones.LeftThumbDistal },
                { OVRSkeleton.BoneId.FullBody_LeftHandIndexProximal, HumanBodyBones.LeftIndexProximal },
                { OVRSkeleton.BoneId.FullBody_LeftHandIndexIntermediate, HumanBodyBones.LeftIndexIntermediate },
                { OVRSkeleton.BoneId.FullBody_LeftHandIndexDistal, HumanBodyBones.LeftIndexDistal },
                { OVRSkeleton.BoneId.FullBody_LeftHandMiddleProximal, HumanBodyBones.LeftMiddleProximal },
                { OVRSkeleton.BoneId.FullBody_LeftHandMiddleIntermediate, HumanBodyBones.LeftMiddleIntermediate },
                { OVRSkeleton.BoneId.FullBody_LeftHandMiddleDistal, HumanBodyBones.LeftMiddleDistal },
                { OVRSkeleton.BoneId.FullBody_LeftHandRingProximal, HumanBodyBones.LeftRingProximal },
                { OVRSkeleton.BoneId.FullBody_LeftHandRingIntermediate, HumanBodyBones.LeftRingIntermediate },
                { OVRSkeleton.BoneId.FullBody_LeftHandRingDistal, HumanBodyBones.LeftRingDistal },
                { OVRSkeleton.BoneId.FullBody_LeftHandLittleProximal, HumanBodyBones.LeftLittleProximal },
                { OVRSkeleton.BoneId.FullBody_LeftHandLittleIntermediate, HumanBodyBones.LeftLittleIntermediate },
                { OVRSkeleton.BoneId.FullBody_LeftHandLittleDistal, HumanBodyBones.LeftLittleDistal },

                { OVRSkeleton.BoneId.FullBody_RightHandThumbMetacarpal, HumanBodyBones.RightThumbProximal },
                { OVRSkeleton.BoneId.FullBody_RightHandThumbProximal, HumanBodyBones.RightThumbIntermediate },
                { OVRSkeleton.BoneId.FullBody_RightHandThumbDistal, HumanBodyBones.RightThumbDistal },
                { OVRSkeleton.BoneId.FullBody_RightHandIndexProximal, HumanBodyBones.RightIndexProximal },
                { OVRSkeleton.BoneId.FullBody_RightHandIndexIntermediate, HumanBodyBones.RightIndexIntermediate },
                { OVRSkeleton.BoneId.FullBody_RightHandIndexDistal, HumanBodyBones.RightIndexDistal },
                { OVRSkeleton.BoneId.FullBody_RightHandMiddleProximal, HumanBodyBones.RightMiddleProximal },
                { OVRSkeleton.BoneId.FullBody_RightHandMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate },
                { OVRSkeleton.BoneId.FullBody_RightHandMiddleDistal, HumanBodyBones.RightMiddleDistal },
                { OVRSkeleton.BoneId.FullBody_RightHandRingProximal, HumanBodyBones.RightRingProximal },
                { OVRSkeleton.BoneId.FullBody_RightHandRingIntermediate, HumanBodyBones.RightRingIntermediate },
                { OVRSkeleton.BoneId.FullBody_RightHandRingDistal, HumanBodyBones.RightRingDistal },
                { OVRSkeleton.BoneId.FullBody_RightHandLittleProximal, HumanBodyBones.RightLittleProximal },
                { OVRSkeleton.BoneId.FullBody_RightHandLittleIntermediate, HumanBodyBones.RightLittleIntermediate },
                { OVRSkeleton.BoneId.FullBody_RightHandLittleDistal, HumanBodyBones.RightLittleDistal },

                { OVRSkeleton.BoneId.FullBody_LeftUpperLeg, HumanBodyBones.LeftUpperLeg },
                { OVRSkeleton.BoneId.FullBody_LeftLowerLeg, HumanBodyBones.LeftLowerLeg },
                { OVRSkeleton.BoneId.FullBody_LeftFootAnkle, HumanBodyBones.LeftFoot },
                { OVRSkeleton.BoneId.FullBody_LeftFootBall, HumanBodyBones.LeftToes },
                { OVRSkeleton.BoneId.FullBody_RightUpperLeg, HumanBodyBones.RightUpperLeg },
                { OVRSkeleton.BoneId.FullBody_RightLowerLeg, HumanBodyBones.RightLowerLeg },
                { OVRSkeleton.BoneId.FullBody_RightFootAnkle, HumanBodyBones.RightFoot },
                { OVRSkeleton.BoneId.FullBody_RightFootBall, HumanBodyBones.RightToes },
            };

        /// <summary>
        /// Maps upper body body tracking bones with <see cref="HumanBodyBones"/> bones.
        /// Use this field to retarget from a body tracking to its humanoid equivalent.
        /// </summary>
        public static readonly Dictionary<OVRSkeleton.BoneId, HumanBodyBones> BoneIdToHumanBodyBone =
            new Dictionary<OVRSkeleton.BoneId, HumanBodyBones>()
            {
                { OVRSkeleton.BoneId.Body_Hips, HumanBodyBones.Hips },
                { OVRSkeleton.BoneId.Body_SpineLower, HumanBodyBones.Spine },
                { OVRSkeleton.BoneId.Body_SpineUpper, HumanBodyBones.Chest },
                { OVRSkeleton.BoneId.Body_Chest, HumanBodyBones.UpperChest },
                { OVRSkeleton.BoneId.Body_Neck, HumanBodyBones.Neck },
                { OVRSkeleton.BoneId.Body_Head, HumanBodyBones.Head },

                { OVRSkeleton.BoneId.Body_LeftShoulder, HumanBodyBones.LeftShoulder },
                { OVRSkeleton.BoneId.Body_LeftArmUpper, HumanBodyBones.LeftUpperArm },
                { OVRSkeleton.BoneId.Body_LeftArmLower, HumanBodyBones.LeftLowerArm },
                { OVRSkeleton.BoneId.Body_LeftHandWrist, HumanBodyBones.LeftHand },

                { OVRSkeleton.BoneId.Body_RightShoulder, HumanBodyBones.RightShoulder },
                { OVRSkeleton.BoneId.Body_RightArmUpper, HumanBodyBones.RightUpperArm },
                { OVRSkeleton.BoneId.Body_RightArmLower, HumanBodyBones.RightLowerArm },
                { OVRSkeleton.BoneId.Body_RightHandWrist, HumanBodyBones.RightHand },

                { OVRSkeleton.BoneId.Body_LeftHandThumbMetacarpal, HumanBodyBones.LeftThumbProximal },
                { OVRSkeleton.BoneId.Body_LeftHandThumbProximal, HumanBodyBones.LeftThumbIntermediate },
                { OVRSkeleton.BoneId.Body_LeftHandThumbDistal, HumanBodyBones.LeftThumbDistal },
                { OVRSkeleton.BoneId.Body_LeftHandIndexProximal, HumanBodyBones.LeftIndexProximal },
                { OVRSkeleton.BoneId.Body_LeftHandIndexIntermediate, HumanBodyBones.LeftIndexIntermediate },
                { OVRSkeleton.BoneId.Body_LeftHandIndexDistal, HumanBodyBones.LeftIndexDistal },
                { OVRSkeleton.BoneId.Body_LeftHandMiddleProximal, HumanBodyBones.LeftMiddleProximal },
                { OVRSkeleton.BoneId.Body_LeftHandMiddleIntermediate, HumanBodyBones.LeftMiddleIntermediate },
                { OVRSkeleton.BoneId.Body_LeftHandMiddleDistal, HumanBodyBones.LeftMiddleDistal },
                { OVRSkeleton.BoneId.Body_LeftHandRingProximal, HumanBodyBones.LeftRingProximal },
                { OVRSkeleton.BoneId.Body_LeftHandRingIntermediate, HumanBodyBones.LeftRingIntermediate },
                { OVRSkeleton.BoneId.Body_LeftHandRingDistal, HumanBodyBones.LeftRingDistal },
                { OVRSkeleton.BoneId.Body_LeftHandLittleProximal, HumanBodyBones.LeftLittleProximal },
                { OVRSkeleton.BoneId.Body_LeftHandLittleIntermediate, HumanBodyBones.LeftLittleIntermediate },
                { OVRSkeleton.BoneId.Body_LeftHandLittleDistal, HumanBodyBones.LeftLittleDistal },

                { OVRSkeleton.BoneId.Body_RightHandThumbMetacarpal, HumanBodyBones.RightThumbProximal },
                { OVRSkeleton.BoneId.Body_RightHandThumbProximal, HumanBodyBones.RightThumbIntermediate },
                { OVRSkeleton.BoneId.Body_RightHandThumbDistal, HumanBodyBones.RightThumbDistal },
                { OVRSkeleton.BoneId.Body_RightHandIndexProximal, HumanBodyBones.RightIndexProximal },
                { OVRSkeleton.BoneId.Body_RightHandIndexIntermediate, HumanBodyBones.RightIndexIntermediate },
                { OVRSkeleton.BoneId.Body_RightHandIndexDistal, HumanBodyBones.RightIndexDistal },
                { OVRSkeleton.BoneId.Body_RightHandMiddleProximal, HumanBodyBones.RightMiddleProximal },
                { OVRSkeleton.BoneId.Body_RightHandMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate },
                { OVRSkeleton.BoneId.Body_RightHandMiddleDistal, HumanBodyBones.RightMiddleDistal },
                { OVRSkeleton.BoneId.Body_RightHandRingProximal, HumanBodyBones.RightRingProximal },
                { OVRSkeleton.BoneId.Body_RightHandRingIntermediate, HumanBodyBones.RightRingIntermediate },
                { OVRSkeleton.BoneId.Body_RightHandRingDistal, HumanBodyBones.RightRingDistal },
                { OVRSkeleton.BoneId.Body_RightHandLittleProximal, HumanBodyBones.RightLittleProximal },
                { OVRSkeleton.BoneId.Body_RightHandLittleIntermediate, HumanBodyBones.RightLittleIntermediate },
                { OVRSkeleton.BoneId.Body_RightHandLittleDistal, HumanBodyBones.RightLittleDistal },
            };

        /// <summary>
        /// The map associates a pair of bones for each <see cref="OVRSkeleton.BoneId"/>.
        /// Retargeters such as <see cref="OVRUnityHumanoidSkeletonRetargeter"/> and
        /// <see cref="OVRSkeletonMetadata"/> use these pairs to creates axes for the purposes
        /// of retargeting. Intended for full body tracking.
        /// </summary>
        public static readonly Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>
            FullBoneIdToJointPair = new Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>()
            {
                {
                    OVRSkeleton.BoneId.FullBody_Neck,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_Neck,
                        OVRSkeleton.BoneId.FullBody_Head)
                },
                {
                    OVRSkeleton.BoneId.FullBody_Head,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_Head,
                        OVRSkeleton.BoneId.Invalid)
                },

                {
                    OVRSkeleton.BoneId.FullBody_Root,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_Root,
                        OVRSkeleton.BoneId.FullBody_Hips)
                },
                {
                    OVRSkeleton.BoneId.FullBody_Hips,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_Hips,
                        OVRSkeleton.BoneId.FullBody_SpineLower)
                },
                {
                    OVRSkeleton.BoneId.FullBody_SpineLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_SpineLower,
                        OVRSkeleton.BoneId.FullBody_SpineMiddle)
                },
                {
                    OVRSkeleton.BoneId.FullBody_SpineMiddle,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_SpineMiddle,
                        OVRSkeleton.BoneId.FullBody_SpineUpper)
                },
                {
                    OVRSkeleton.BoneId.FullBody_SpineUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_SpineUpper,
                        OVRSkeleton.BoneId.FullBody_Chest)
                },
                {
                    OVRSkeleton.BoneId.FullBody_Chest,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_Chest,
                        OVRSkeleton.BoneId.FullBody_Neck)
                },

                {
                    OVRSkeleton.BoneId.FullBody_LeftShoulder,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftShoulder,
                        OVRSkeleton.BoneId.FullBody_LeftArmUpper)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftScapula,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftScapula,
                        OVRSkeleton.BoneId.FullBody_LeftArmUpper)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftArmUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftArmUpper,
                        OVRSkeleton.BoneId.FullBody_LeftArmLower)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftArmLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftArmLower,
                        OVRSkeleton.BoneId.FullBody_LeftHandWrist)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandWrist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandWrist,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandPalm,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandPalm,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandWristTwist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandWristTwist,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleMetacarpal)
                },

                {
                    OVRSkeleton.BoneId.FullBody_LeftHandThumbMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandThumbMetacarpal,
                        OVRSkeleton.BoneId.FullBody_LeftHandThumbProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandThumbProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandThumbProximal,
                        OVRSkeleton.BoneId.FullBody_LeftHandThumbDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandThumbDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandThumbDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandThumbTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandThumbDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandIndexMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexMetacarpal,
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandIndexProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandIndexProximal,
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandIndexIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexIntermediate,
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandIndexDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandIndexDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandIndexTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandIndexDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandMiddleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleMetacarpal,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandMiddleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleProximal,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandMiddleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleIntermediate,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandMiddleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandMiddleDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandMiddleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandMiddleDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandRingMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandRingMetacarpal,
                        OVRSkeleton.BoneId.FullBody_LeftHandRingProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandRingProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandRingProximal,
                        OVRSkeleton.BoneId.FullBody_LeftHandRingIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandRingIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandRingIntermediate,
                        OVRSkeleton.BoneId.FullBody_LeftHandRingDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandRingDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandRingDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandRingTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandRingDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandLittleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleMetacarpal,
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandLittleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleProximal,
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandLittleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleIntermediate,
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandLittleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandLittleDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftHandLittleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftHandLittleDistal,
                        OVRSkeleton.BoneId.FullBody_LeftHandLittleTip)
                },

                {
                    OVRSkeleton.BoneId.FullBody_RightShoulder,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightShoulder,
                        OVRSkeleton.BoneId.FullBody_RightArmUpper)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightScapula,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightScapula,
                        OVRSkeleton.BoneId.FullBody_RightArmUpper)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightArmUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightArmUpper,
                        OVRSkeleton.BoneId.FullBody_RightArmLower)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightArmLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightArmLower,
                        OVRSkeleton.BoneId.FullBody_RightHandWrist)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandWrist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandWrist,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandPalm,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandPalm,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandWristTwist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandWristTwist,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleMetacarpal)
                },

                {
                    OVRSkeleton.BoneId.FullBody_RightHandThumbMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandThumbMetacarpal,
                        OVRSkeleton.BoneId.FullBody_RightHandThumbProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandThumbProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandThumbProximal,
                        OVRSkeleton.BoneId.FullBody_RightHandThumbDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandThumbDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandThumbDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandThumbTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandThumbDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandIndexMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandIndexMetacarpal,
                        OVRSkeleton.BoneId.FullBody_RightHandIndexProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandIndexProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandIndexProximal,
                        OVRSkeleton.BoneId.FullBody_RightHandIndexIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandIndexIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandIndexIntermediate,
                        OVRSkeleton.BoneId.FullBody_RightHandIndexDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandIndexDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandIndexDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandIndexTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandIndexDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandMiddleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleMetacarpal,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandMiddleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleProximal,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandMiddleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleIntermediate,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandMiddleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandMiddleDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandMiddleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandMiddleDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandRingMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandRingMetacarpal,
                        OVRSkeleton.BoneId.FullBody_RightHandRingProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandRingProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandRingProximal,
                        OVRSkeleton.BoneId.FullBody_RightHandRingIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandRingIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandRingIntermediate,
                        OVRSkeleton.BoneId.FullBody_RightHandRingDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandRingDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandRingDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandRingTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandRingDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandLittleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandLittleMetacarpal,
                        OVRSkeleton.BoneId.FullBody_RightHandLittleProximal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandLittleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandLittleProximal,
                        OVRSkeleton.BoneId.FullBody_RightHandLittleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandLittleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.FullBody_RightHandLittleIntermediate,
                        OVRSkeleton.BoneId.FullBody_RightHandLittleDistal)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandLittleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandLittleDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandLittleTip)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightHandLittleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightHandLittleDistal,
                        OVRSkeleton.BoneId.FullBody_RightHandLittleTip)
                },

                {
                    OVRSkeleton.BoneId.FullBody_LeftUpperLeg,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftUpperLeg,
                        OVRSkeleton.BoneId.FullBody_LeftLowerLeg)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftLowerLeg,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftLowerLeg,
                        OVRSkeleton.BoneId.FullBody_LeftFootAnkle)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftFootAnkle,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftFootAnkle,
                        OVRSkeleton.BoneId.FullBody_LeftFootBall)
                },
                {
                    OVRSkeleton.BoneId.FullBody_LeftFootBall,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_LeftFootAnkle,
                        OVRSkeleton.BoneId.FullBody_LeftFootBall)
                },

                {
                    OVRSkeleton.BoneId.FullBody_RightUpperLeg,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightUpperLeg,
                        OVRSkeleton.BoneId.FullBody_RightLowerLeg)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightLowerLeg,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightLowerLeg,
                        OVRSkeleton.BoneId.FullBody_RightFootAnkle)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightFootAnkle,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightFootAnkle,
                        OVRSkeleton.BoneId.FullBody_RightFootBall)
                },
                {
                    OVRSkeleton.BoneId.FullBody_RightFootBall,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.FullBody_RightFootAnkle,
                        OVRSkeleton.BoneId.FullBody_RightFootBall)
                },
            };

        /// <summary>
        /// The map associates a pair of bones for each <see cref="OVRSkeleton.BoneId"/>.
        /// Retargeters such as <see cref="OVRUnityHumanoidSkeletonRetargeter"/> and
        /// <see cref="OVRSkeletonMetadata"/> use these pairs to creates axes for the purposes
        /// of retargeting. Intended for half body tracking.
        /// </summary>
        public static readonly Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>
            BoneIdToJointPair = new Dictionary<OVRSkeleton.BoneId, Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>>()
            {
                {
                    OVRSkeleton.BoneId.Body_Neck,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_Neck,
                        OVRSkeleton.BoneId.Body_Head)
                },
                {
                    OVRSkeleton.BoneId.Body_Head,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_Head,
                        OVRSkeleton.BoneId.Invalid)
                },

                {
                    OVRSkeleton.BoneId.Body_Root,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_Root,
                        OVRSkeleton.BoneId.Body_Hips)
                },
                {
                    OVRSkeleton.BoneId.Body_Hips,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_Hips,
                        OVRSkeleton.BoneId.Body_SpineLower)
                },
                {
                    OVRSkeleton.BoneId.Body_SpineLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_SpineLower,
                        OVRSkeleton.BoneId.Body_SpineMiddle)
                },
                {
                    OVRSkeleton.BoneId.Body_SpineMiddle,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_SpineMiddle,
                        OVRSkeleton.BoneId.Body_SpineUpper)
                },
                {
                    OVRSkeleton.BoneId.Body_SpineUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_SpineUpper,
                        OVRSkeleton.BoneId.Body_Chest)
                },
                {
                    OVRSkeleton.BoneId.Body_Chest,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_Chest,
                        OVRSkeleton.BoneId.Body_Neck)
                },

                {
                    OVRSkeleton.BoneId.Body_LeftShoulder,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftShoulder,
                        OVRSkeleton.BoneId.Body_LeftArmUpper)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftScapula,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftScapula,
                        OVRSkeleton.BoneId.Body_LeftArmUpper)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftArmUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftArmUpper,
                        OVRSkeleton.BoneId.Body_LeftArmLower)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftArmLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftArmLower,
                        OVRSkeleton.BoneId.Body_LeftHandWrist)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandWrist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandWrist,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandPalm,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandPalm,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandWristTwist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandWristTwist,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleMetacarpal)
                },

                {
                    OVRSkeleton.BoneId.Body_LeftHandThumbMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandThumbMetacarpal,
                        OVRSkeleton.BoneId.Body_LeftHandThumbProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandThumbProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandThumbProximal,
                        OVRSkeleton.BoneId.Body_LeftHandThumbDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandThumbDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandThumbDistal,
                        OVRSkeleton.BoneId.Body_LeftHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandThumbTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandThumbDistal,
                        OVRSkeleton.BoneId.Body_LeftHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandIndexMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandIndexMetacarpal,
                        OVRSkeleton.BoneId.Body_LeftHandIndexProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandIndexProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandIndexProximal,
                        OVRSkeleton.BoneId.Body_LeftHandIndexIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandIndexIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandIndexIntermediate,
                        OVRSkeleton.BoneId.Body_LeftHandIndexDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandIndexDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandIndexDistal,
                        OVRSkeleton.BoneId.Body_LeftHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandIndexTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandIndexDistal,
                        OVRSkeleton.BoneId.Body_LeftHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandMiddleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandMiddleMetacarpal,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandMiddleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandMiddleProximal,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandMiddleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.Body_LeftHandMiddleIntermediate,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandMiddleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandMiddleDistal,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandMiddleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandMiddleDistal,
                        OVRSkeleton.BoneId.Body_LeftHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandRingMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandRingMetacarpal,
                        OVRSkeleton.BoneId.Body_LeftHandRingProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandRingProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandRingProximal,
                        OVRSkeleton.BoneId.Body_LeftHandRingIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandRingIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandRingIntermediate,
                        OVRSkeleton.BoneId.Body_LeftHandRingDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandRingDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandRingDistal,
                        OVRSkeleton.BoneId.Body_LeftHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandRingTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandRingDistal,
                        OVRSkeleton.BoneId.Body_LeftHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandLittleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandLittleMetacarpal,
                        OVRSkeleton.BoneId.Body_LeftHandLittleProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandLittleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandLittleProximal,
                        OVRSkeleton.BoneId.Body_LeftHandLittleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandLittleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.Body_LeftHandLittleIntermediate,
                        OVRSkeleton.BoneId.Body_LeftHandLittleDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandLittleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandLittleDistal,
                        OVRSkeleton.BoneId.Body_LeftHandLittleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_LeftHandLittleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_LeftHandLittleDistal,
                        OVRSkeleton.BoneId.Body_LeftHandLittleTip)
                },

                {
                    OVRSkeleton.BoneId.Body_RightShoulder,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightShoulder,
                        OVRSkeleton.BoneId.Body_RightArmUpper)
                },
                {
                    OVRSkeleton.BoneId.Body_RightScapula,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightScapula,
                        OVRSkeleton.BoneId.Body_RightArmUpper)
                },
                {
                    OVRSkeleton.BoneId.Body_RightArmUpper,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightArmUpper,
                        OVRSkeleton.BoneId.Body_RightArmLower)
                },
                {
                    OVRSkeleton.BoneId.Body_RightArmLower,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightArmLower,
                        OVRSkeleton.BoneId.Body_RightHandWrist)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandWrist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandWrist,
                        OVRSkeleton.BoneId.Body_RightHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandPalm,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandPalm,
                        OVRSkeleton.BoneId.Body_RightHandMiddleMetacarpal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandWristTwist,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandWristTwist,
                        OVRSkeleton.BoneId.Body_RightHandMiddleMetacarpal)
                },

                {
                    OVRSkeleton.BoneId.Body_RightHandThumbMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandThumbMetacarpal,
                        OVRSkeleton.BoneId.Body_RightHandThumbProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandThumbProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandThumbProximal,
                        OVRSkeleton.BoneId.Body_RightHandThumbDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandThumbDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandThumbDistal,
                        OVRSkeleton.BoneId.Body_RightHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandThumbTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandThumbDistal,
                        OVRSkeleton.BoneId.Body_RightHandThumbTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandIndexMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandIndexMetacarpal,
                        OVRSkeleton.BoneId.Body_RightHandIndexProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandIndexProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandIndexProximal,
                        OVRSkeleton.BoneId.Body_RightHandIndexIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandIndexIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.Body_RightHandIndexIntermediate,
                        OVRSkeleton.BoneId.Body_RightHandIndexDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandIndexDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandIndexDistal,
                        OVRSkeleton.BoneId.Body_RightHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandIndexTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandIndexDistal,
                        OVRSkeleton.BoneId.Body_RightHandIndexTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandMiddleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandMiddleMetacarpal,
                        OVRSkeleton.BoneId.Body_RightHandMiddleProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandMiddleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandMiddleProximal,
                        OVRSkeleton.BoneId.Body_RightHandMiddleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandMiddleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.Body_RightHandMiddleIntermediate,
                        OVRSkeleton.BoneId.Body_RightHandMiddleDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandMiddleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandMiddleDistal,
                        OVRSkeleton.BoneId.Body_RightHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandMiddleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandMiddleDistal,
                        OVRSkeleton.BoneId.Body_RightHandMiddleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandRingMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandRingMetacarpal,
                        OVRSkeleton.BoneId.Body_RightHandRingProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandRingProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandRingProximal,
                        OVRSkeleton.BoneId.Body_RightHandRingIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandRingIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandRingIntermediate,
                        OVRSkeleton.BoneId.Body_RightHandRingDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandRingDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandRingDistal,
                        OVRSkeleton.BoneId.Body_RightHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandRingTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandRingDistal,
                        OVRSkeleton.BoneId.Body_RightHandRingTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandLittleMetacarpal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandLittleMetacarpal,
                        OVRSkeleton.BoneId.Body_RightHandLittleProximal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandLittleProximal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandLittleProximal,
                        OVRSkeleton.BoneId.Body_RightHandLittleIntermediate)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandLittleIntermediate,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(
                        OVRSkeleton.BoneId.Body_RightHandLittleIntermediate,
                        OVRSkeleton.BoneId.Body_RightHandLittleDistal)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandLittleDistal,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandLittleDistal,
                        OVRSkeleton.BoneId.Body_RightHandLittleTip)
                },
                {
                    OVRSkeleton.BoneId.Body_RightHandLittleTip,
                    new Tuple<OVRSkeleton.BoneId, OVRSkeleton.BoneId>(OVRSkeleton.BoneId.Body_RightHandLittleDistal,
                        OVRSkeleton.BoneId.Body_RightHandLittleTip)
                },
            };
    }
}
