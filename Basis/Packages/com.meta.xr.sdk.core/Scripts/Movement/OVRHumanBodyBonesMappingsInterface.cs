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
using static OVRUnityHumanoidSkeletonRetargeter.OVRHumanBodyBonesMappings;
using BoneId = OVRSkeleton.BoneId;

/// <summary>
/// Defines the interface for mappings that are relevant to retargeting by classes such as
/// <see cref="OVRUnityHumanoidSkeletonRetargeter"/>. This includes associations between
/// <see cref="HumanBodyBones"/> and <see cref="BoneId"/>, <see cref="HumanBodyBones"/> and
/// <see cref="BodySection"/>. There are also mappings between a body tracking or
/// humanoid bone to joint pairs used for retargeting.
/// <remarks>
/// Implement this interface to create your own custom mappings in case your character is
/// specialized, otherwise use the implementation provided via <see cref="OVRHumanBodyBonesMappings"/>.
/// </remarks>
/// </summary>
public interface OVRHumanBodyBonesMappingsInterface
{
    /// <summary>
    /// The mapping between <see cref="HumanBodyBones"/> a tuple of <see cref="HumanBodyBones"/>.
    /// <remarks>
    /// The tuple is a joint pair that retargeting uses to create a <see cref="HumanBodyBones"/>'s orientation
    /// during runtime. Define this field so that you can influence what kind of tuple is
    /// used during retargeting.
    /// </remarks>
    /// </summary>
    public Dictionary<HumanBodyBones, Tuple<HumanBodyBones, HumanBodyBones>> GetBoneToJointPair { get; }

    /// <summary>
    /// The mapping between <see cref="HumanBodyBones"/> and <see cref="BodySection"/>. Define
    /// this mapping to associate a body with parts of the body, such as the back, left arm,
    /// right leg, and so on.
    /// </summary>
    public Dictionary<HumanBodyBones, BodySection> GetBoneToBodySection { get; }

    /// <summary>
    /// Returns <see cref="BoneId"> to <see cref="HumanBodyBones"/> mapping for full body
    /// characters. Since retargeting retargets to humanoid characters, it uses this field
    /// to map body tracking bones to humanoid bones.
    /// </summary>
    public Dictionary<BoneId, HumanBodyBones> GetFullBodyBoneIdToHumanBodyBone { get; }

    /// <summary>
    /// Returns <see cref="BoneId"> to <see cref="HumanBodyBones"/> mapping for upper body
    /// characters. Since retargeting retargets to humanoid characters, it uses this field
    /// to map body tracking bones to humanoid bones.
    /// </summary>
    public Dictionary<BoneId, HumanBodyBones> GetBoneIdToHumanBodyBone { get; }

    /// <summary>
    /// The mapping between <see cref="BoneId"/> a tuple of <see cref="BoneId"/>.
    /// Retargeting uses this tuple or joint pair to create a <see cref="BoneId"/>'s orientation
    /// during retargeting. Define this field so that you can influence what kind of tuple is
    /// used during retargeting. Intended for the retargeting source (i.e. body tracking)
    /// assuming full body tracking is used.
    /// </summary>
    public Dictionary<BoneId, Tuple<BoneId, BoneId>>
        GetFullBodyBoneIdToJointPair
    { get; }

    /// <summary>
    /// The mapping between <see cref="BoneId"/> a tuple of <see cref="BoneId"/>.
    /// Retargeting uses this tuple or joint pair to create a <see cref="BoneId"/>'s orientation
    /// during retargeting. Define this field so that you can influence what kind of tuple is
    /// used during retargeting. Intended for the retargeting source (i.e. body tracking)
    /// assuming upper body tracking is used.
    /// </summary>
    public Dictionary<BoneId, Tuple<BoneId, BoneId>>
        GetBoneIdToJointPair
    { get; }
}
