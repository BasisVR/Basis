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

using System.Collections.Generic;
using Meta.XR.Util;
using UnityEngine;

/// <summary>
/// A child class definition of the class responsible for setting
/// <see cref="OVRSkeleton"/> body tracking bones transforms to a specified
/// oculus skeleton. The oculus skeleton is a skeleton that will need to use bones
/// that match body tracking names specified in <see cref="OVRSkeleton.BoneId"/>
/// for the desired skeleton joint set, and performs no other modifications on top of
/// applying the body tracking data. As such, the skeleton bones should be rigged
/// such that the rest pose bones of the skeleton matches the bone structure and space in
/// <see cref="OVRSkeleton.BindPoses"/>. A visual reference of body joints can also be found
/// [here](https://developer.oculus.com/documentation/native/android/move-ref-body-joints/).
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/move-samples/")]
[Feature(Feature.BodyTracking)]
public class OVRCustomSkeleton : OVRSkeleton, ISerializationCallbackReceiver
{
    [HideInInspector]
    [SerializeField]
    private List<Transform> _customBones_V2;

    /// <summary>
    /// Provides access to the list of bones that are part of the Oculus skeleton. These bones have
    /// body tracking data applied, and can be accessed to apply or retrieve information about
    /// the skeleton such as the position and rotation of a specific bone. By using these transforms,
    /// you can modify the skeleton to be affected by application logic.
    /// </summary>
    public List<Transform> CustomBones => _customBones_V2;

    /// <summary>
    /// This enum specifies the types of supported skeleton structures to be retargeted to from body tracking.
    /// Any skeleton type that isn't specified here is not compatible with the <see cref="OVRCustomSkeleton"/>.
    /// Instead, please check the [body tracking](https://developer.oculus.com/documentation/unity/move-body-tracking/) documentation
    /// for more information on retargeting to a third party humanoid skeleton.
    /// </summary>
    public enum RetargetingType
    {
        /// <summary>
        /// The default skeleton structure of the body tracking system. The rest pose of this skeleton
        /// should match the rest pose specified by <see cref="OVRSkeleton.BindPoses"/>.
        /// </summary>
        OculusSkeleton
    }

    [SerializeField, HideInInspector]
    internal RetargetingType retargetingType = RetargetingType.OculusSkeleton;

    protected override Transform GetBoneTransform(BoneId boneId) => _customBones_V2[(int)boneId];

#if UNITY_EDITOR
    private bool _shouldSetDirty;

    private void OnValidate()
    {
        if (!_shouldSetDirty) return;

        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        UnityEditor.EditorUtility.SetDirty(this);
        _shouldSetDirty = false;
    }
#endif

    void ISerializationCallbackReceiver.OnBeforeSerialize()
    {
    }

    void ISerializationCallbackReceiver.OnAfterDeserialize()
    {
        AllocateBones();
    }

    private void AllocateBones()
    {
        int max = (int)BoneId.Max;

        if (_customBones_V2.Count == max)
        {
            return;
        }

        // Make sure we have the right number of bones
        while (_customBones_V2.Count < max)
        {
            _customBones_V2.Add(null);
        }

#if UNITY_EDITOR
        _shouldSetDirty = true;
#endif
    }

    internal override void SetSkeletonType(SkeletonType skeletonType)
    {
        base.SetSkeletonType(skeletonType);
        _customBones_V2 ??= new List<Transform>();

        AllocateBones();
    }
}
