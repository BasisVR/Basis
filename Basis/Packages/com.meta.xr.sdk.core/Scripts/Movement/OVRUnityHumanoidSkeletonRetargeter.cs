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
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;
using SkeletonType = OVRPlugin.SkeletonType;
using BoneId = OVRPlugin.BoneId;
[Feature(Feature.BodyTracking)]

/// <summary>
/// A class responsible for retargeting from <see cref="OVRSkeleton"/> body tracking
/// bones to a third party  humanoid skeleton. Unlike <see cref="OVRCustomSkeleton"/>, the skeleton
/// retargeted to does not need to use bones that match body tracking names, or
/// have a hierarchy that matches what body tracking expects. Instead, you
/// can use this class to apply [body tracking](https://developer.oculus.com/documentation/unity/move-body-tracking/)
/// to characters that have been imported as Unity Humanoids.
///
/// The retargeter is split into several parts, each with its own responsibility.
///
/// The main portion is responsible for computing the offsets between
/// the source (body tracking) and target (Humanoid) skeletons, which will be used
/// animate the target based on the source's movements.
///
/// Another portion, known as <see cref="OVRSkeletonMetadata"/>, is responsible
/// for meta data applicable for retargeting, such as bone-to-bone pairs in body
/// tracking and Humanoid skeletons. These bone-to-bone pairs are used to align
/// the Humanoid (target) skeleton to the body tracking (source) skeleton, so
/// that a Humanoid can be driven by body tracking movements.
///
/// There is also <see cref="OVRHumanBodyBonesMappings"/>, which contains maps
/// useful for retargeting, such as maps between body tracking and humanoid bones
/// for upper body and full body tracking.
/// </summary>
public partial class OVRUnityHumanoidSkeletonRetargeter : OVRSkeleton
{
    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRSkeletonMetadata _sourceSkeletonData;
    protected OVRSkeletonMetadata SourceSkeletonData => _sourceSkeletonData;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRSkeletonMetadata _sourceSkeletonTPoseData;
    protected OVRSkeletonMetadata SourceSkeletonTPoseData => _sourceSkeletonTPoseData;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRSkeletonMetadata _targetSkeletonData;
    protected OVRSkeletonMetadata TargetSkeletonData => _targetSkeletonData;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private Animator _animatorTargetSkeleton;
    protected Animator AnimatorTargetSkeleton => _animatorTargetSkeleton;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private Dictionary<BoneId, HumanBodyBones> _customBoneIdToHumanBodyBone =
        new Dictionary<BoneId, HumanBodyBones>();

    protected Dictionary<BoneId, HumanBodyBones> CustomBoneIdToHumanBodyBone
    {
        get => _customBoneIdToHumanBodyBone;
    }

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private readonly Dictionary<HumanBodyBones, Quaternion> _targetTPoseRotations =
        new Dictionary<HumanBodyBones, Quaternion>();
    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private Dictionary<HumanBodyBones, Transform> _targetTPoseTransformDup =
        new Dictionary<HumanBodyBones, Transform>();

    protected Dictionary<HumanBodyBones, Quaternion> TargetTPoseRotations
    {
        get => _targetTPoseRotations;
    }

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private int _lastSkelChangeCount = -1;
    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private Vector3 _lastTrackedScale;

    /// <summary>
    /// Allows you to tweak joint movements after the <see cref="OVRUnityHumanoidSkeletonRetargeter"/>
    /// has retargeted to a character. If the retargeted result is unsatisfactory,
    /// you can use this field to affect the final output.
    /// </summary>
    [Serializable]
    public class JointAdjustment
    {
        /// <summary>
        /// Maps to the Unity Humanoid joint that can be adjusted
        /// using position or rotation-based tweaks.
        /// </summary>
        public HumanBodyBones Joint;

        /// <summary>
        /// The position change to apply to the joint, post-retargeting,
        /// in world-space. Defaults to the zero vector.
        /// </summary>
        public Vector3 PositionChange = Vector3.zero;

        /// <summary>
        /// The rotation change to apply to the joint, post-retargeting,
        /// in world-space. Defaults to the Quaternion identity.
        /// NOTE: This is deprecated, please use <inheritdoc cref="JointAdjustment.RotationTweaks"/>.
        /// </summary>
        public Quaternion RotationChange = Quaternion.identity;

        /// <summary>
        /// Allows accumulating a series of rotations to be applied to a joint,
        /// post-retargeting. These values are accumulated and stored in
        /// <see cref="JointAdjustment.PrecomputedRotationTweaks"/>.
        /// </summary>
        public Quaternion[] RotationTweaks = null;

        /// <summary>
        /// Use this to disable rotational retargeting on the target joint, so that the
        /// target's rotation values are not affected by body tracking.
        /// </summary>
        public bool DisableRotationTransform = false;

        /// <summary>
        /// Use this to disable positional retargeting on the target joint, so that the
        /// target's position values are not affected by body tracking.
        /// </summary>
        public bool DisablePositionTransform = false;

        /// <summary>
        /// Allows mapping this human body bone to a (full body) OVRSkeleton bone different from the
        /// standard value. An <see cref="OVRHumanBodyBonesMappings.FullBodyTrackingBoneId.NoOverride"/>
        /// value indicates to not override the default mapping; <see cref="OVRHumanBodyBonesMappings.FullBodyTrackingBoneId.Remove"/>
        /// means to exclude the bone from retargeting. This cannot be changed at runtime.
        /// </summary>
        public OVRHumanBodyBonesMappings.FullBodyTrackingBoneId FullBodyBoneIdOverrideValue =
            OVRHumanBodyBonesMappings.FullBodyTrackingBoneId.NoOverride;

        /// <summary>
        /// Allows mapping this human body bone to a (half body) OVRSkeleton bone different from the
        /// standard value. An <see cref="OVRHumanBodyBonesMappings.BodyTrackingBoneId.NoOverride"/>
        /// value indicates to not override the default mapping; <see cref="OVRHumanBodyBonesMappings.BodyTrackingBoneId.Remove"/>
        /// means to exclude the bone from retargeting. This cannot be changed at runtime.
        /// </summary>
        public OVRHumanBodyBonesMappings.BodyTrackingBoneId BoneIdOverrideValue =
            OVRHumanBodyBonesMappings.BodyTrackingBoneId.NoOverride;

        /// <summary>
        /// Precomputed accumulated rotations, derived from
        /// <see cref="JointAdjustment.RotationTweaks"/>.
        /// </summary>
        public Quaternion PrecomputedRotationTweaks { get; private set; }

        /// <summary>
        /// Precompute rotation tweaks by accumulating them and storing
        /// them into <see cref="JointAdjustment.PrecomputedRotationTweaks"/>.
        /// Using the precomputed value is faster at runtime than accumulating the quaternions during
        /// each frame.
        /// </summary>
        public void PrecomputeRotationTweaks()
        {
            PrecomputedRotationTweaks = Quaternion.identity;
            if (RotationTweaks == null || RotationTweaks.Length == 0)
            {
                return;
            }

            foreach (var rotationTweak in RotationTweaks)
            {
                // Make sure the quaternion is valid. Quaternions are initialized to all
                // zeroes by default, which makes them invalid.
                if (rotationTweak.w < Mathf.Epsilon && rotationTweak.x < Mathf.Epsilon &&
                    rotationTweak.y < Mathf.Epsilon && rotationTweak.z < Mathf.Epsilon)
                {
                    continue;
                }

                PrecomputedRotationTweaks *= rotationTweak;
            }
        }
    }

    /// <summary>
    /// Default constructor for the retargeter class that initializes
    /// the instance's skeleton type to match half body tracking.
    /// </summary>
    public OVRUnityHumanoidSkeletonRetargeter()
    {
        _skeletonType = SkeletonType.Body;
    }

    [SerializeField]
    protected JointAdjustment[] _adjustments =
    {
        new JointAdjustment
        {
            Joint = HumanBodyBones.Hips,
            RotationChange = Quaternion.Euler(60.0f, 0.0f, 0.0f)
        }
    };

    protected JointAdjustment[] Adjustments
    {
        get => _adjustments;
    }

    [SerializeField]
    protected OVRHumanBodyBonesMappings.BodySection[] _fullBodySectionsToAlign =
    {
        OVRHumanBodyBonesMappings.BodySection.LeftArm, OVRHumanBodyBonesMappings.BodySection.RightArm,
        OVRHumanBodyBonesMappings.BodySection.LeftHand, OVRHumanBodyBonesMappings.BodySection.RightHand,
        OVRHumanBodyBonesMappings.BodySection.Hips, OVRHumanBodyBonesMappings.BodySection.Back,
        OVRHumanBodyBonesMappings.BodySection.Neck, OVRHumanBodyBonesMappings.BodySection.Head,
        OVRHumanBodyBonesMappings.BodySection.LeftLeg, OVRHumanBodyBonesMappings.BodySection.LeftFoot,
        OVRHumanBodyBonesMappings.BodySection.RightLeg, OVRHumanBodyBonesMappings.BodySection.RightFoot
    };
    [SerializeField]
    protected OVRHumanBodyBonesMappings.BodySection[] _bodySectionsToAlign =
    {
        OVRHumanBodyBonesMappings.BodySection.LeftArm, OVRHumanBodyBonesMappings.BodySection.RightArm,
        OVRHumanBodyBonesMappings.BodySection.LeftHand, OVRHumanBodyBonesMappings.BodySection.RightHand,
        OVRHumanBodyBonesMappings.BodySection.Hips, OVRHumanBodyBonesMappings.BodySection.Back,
        OVRHumanBodyBonesMappings.BodySection.Neck, OVRHumanBodyBonesMappings.BodySection.Head
    };

    protected OVRHumanBodyBonesMappings.BodySection[] FullBodySectionsToAlign
    {
        get => _fullBodySectionsToAlign;
    }
    protected OVRHumanBodyBonesMappings.BodySection[] BodySectionsToAlign
    {
        get => _bodySectionsToAlign;
    }

    [SerializeField]
    protected OVRHumanBodyBonesMappings.BodySection[] _fullBodySectionToPosition =
    {
        OVRHumanBodyBonesMappings.BodySection.LeftArm, OVRHumanBodyBonesMappings.BodySection.RightArm,
        OVRHumanBodyBonesMappings.BodySection.LeftHand, OVRHumanBodyBonesMappings.BodySection.RightHand,
        OVRHumanBodyBonesMappings.BodySection.Hips, OVRHumanBodyBonesMappings.BodySection.Neck,
        OVRHumanBodyBonesMappings.BodySection.Head,
        OVRHumanBodyBonesMappings.BodySection.LeftLeg, OVRHumanBodyBonesMappings.BodySection.LeftFoot,
        OVRHumanBodyBonesMappings.BodySection.RightLeg, OVRHumanBodyBonesMappings.BodySection.RightFoot
    };
    [SerializeField]
    protected OVRHumanBodyBonesMappings.BodySection[] _bodySectionToPosition =
    {
        OVRHumanBodyBonesMappings.BodySection.LeftArm, OVRHumanBodyBonesMappings.BodySection.RightArm,
        OVRHumanBodyBonesMappings.BodySection.LeftHand, OVRHumanBodyBonesMappings.BodySection.RightHand,
        OVRHumanBodyBonesMappings.BodySection.Hips, OVRHumanBodyBonesMappings.BodySection.Neck,
        OVRHumanBodyBonesMappings.BodySection.Head
    };

    protected OVRHumanBodyBonesMappings.BodySection[] FullBodySectionToPosition
    {
        get => _fullBodySectionToPosition;
    }
    protected OVRHumanBodyBonesMappings.BodySection[] BodySectionToPosition
    {
        get => _bodySectionToPosition;
    }

    /// <summary>
    /// Since the retargeter's Update function is driven by <see cref="OVRSkeleton"/>'s
    /// calls via FixedUpdate as well, the variable allows one to control how often
    /// updates occurs. Use this field to allow updates during physics ticks, update ticks,
    /// or both.
    /// </summary>
    public enum UpdateType
    {
        FixedUpdateOnly = 0,
        UpdateOnly,
        FixedUpdateAndUpdate
    }

    /// <summary>
    /// Controls if we run retargeting from FixedUpdate, Update,
    /// or both, and is based on the enum <see cref="OVRUnityHumanoidSkeletonRetargeter.UpdateType"/>.
    /// </summary>
    [SerializeField]
    [Tooltip("Controls if we run retargeting from FixedUpdate, Update, or both.")]
    protected UpdateType _updateType = UpdateType.UpdateOnly;

    private OVRHumanBodyBonesMappingsInterface _bodyBonesMappingInterface =
        new OVRHumanBodyBonesMappings();
    /// <summary>
    /// This returns body bone mappings, based on <see cref="OVRHumanBodyBonesMappingsInterface"/>.
    /// Use this field in case the default mappings fall short and you wish to
    /// override them with a custom one that works with a specific characters.
    /// </summary>
    public OVRHumanBodyBonesMappingsInterface BodyBoneMappingsInterface
    {
        get => _bodyBonesMappingInterface;
        set => _bodyBonesMappingInterface = value;
    }

    protected override void Start()
    {
        base.Start();
        _lastTrackedScale = transform.lossyScale;
        Assert.IsTrue(OVRSkeleton.IsBodySkeleton(_skeletonType));

        ValidateGameObjectForUnityHumanoidRetargeting(gameObject);
        _animatorTargetSkeleton = gameObject.GetComponent<Animator>();

        CreateCustomBoneIdToHumanBodyBoneMapping();
        StoreTTargetPoseRotations();

        _targetSkeletonData = new OVRSkeletonMetadata(_animatorTargetSkeleton, _bodyBonesMappingInterface);
        _targetSkeletonData.BuildCoordinateAxesForAllBones();

        PrecomputeAllRotationTweaks();
    }

    private void PrecomputeAllRotationTweaks()
    {
        if (_adjustments == null || _adjustments.Length == 0)
        {
            return;
        }
        foreach (var adjustment in _adjustments)
        {
            adjustment.PrecomputeRotationTweaks();
        }
    }

    protected virtual void OnValidate()
    {
        // Only do this from the editor.
#if UNITY_EDITOR
        if (!UnityEditor.EditorApplication.isPlaying)
        {
            return;
        }
#endif
        PrecomputeAllRotationTweaks();
    }

    internal static void ValidateGameObjectForUnityHumanoidRetargeting(GameObject go)
    {
        if (go.GetComponent<Animator>() == null)
        {
            throw new InvalidOperationException(
                $"Retargeting to Unity Humanoid requires an {nameof(Animator)} component with a humanoid avatar on T-Pose");
        }
    }

    private void StoreTTargetPoseRotations()
    {
        for (var i = HumanBodyBones.Hips; i < HumanBodyBones.LastBone; i++)
        {
            var boneTransform = _animatorTargetSkeleton.GetBoneTransform(i);
            _targetTPoseRotations[i] = boneTransform ? boneTransform.rotation : Quaternion.identity;
        }

        Transform tPoseCopy = CreateDuplicateTransformHierarchy(
            _animatorTargetSkeleton.GetBoneTransform(HumanBodyBones.Hips));
        tPoseCopy.name = $"{this.name}-tPose";
        tPoseCopy.SetParent(this.transform, false);
    }

    private Transform CreateDuplicateTransformHierarchy(
        Transform transformFromOriginalHierarchy)
    {
        var newGameObject = new GameObject(transformFromOriginalHierarchy.name + "-tPose");
        var newTransform = newGameObject.transform;
        newTransform.localPosition = transformFromOriginalHierarchy.localPosition;
        newTransform.localRotation = transformFromOriginalHierarchy.localRotation;
        newTransform.localScale = transformFromOriginalHierarchy.localScale;

        var humanBodyBone = FindHumanBodyBoneFromTransform(transformFromOriginalHierarchy);
        if (humanBodyBone != HumanBodyBones.LastBone)
        {
            _targetTPoseTransformDup[humanBodyBone] = newTransform;
        }

        foreach (Transform originalChild in transformFromOriginalHierarchy)
        {
            var newChild = CreateDuplicateTransformHierarchy(originalChild);
            newChild.SetParent(newTransform, false);
        }

        return newTransform;
    }

    private HumanBodyBones FindHumanBodyBoneFromTransform(Transform candidateTransform)
    {
        for (var i = HumanBodyBones.Hips; i < HumanBodyBones.LastBone; i++)
        {
            if (_animatorTargetSkeleton.GetBoneTransform(i) == candidateTransform)
            {
                return i;
            }
        }

        return HumanBodyBones.LastBone;
    }

    private void AlignHierarchies(Transform transformToAlign, Transform referenceTransform)
    {
        transformToAlign.localRotation = referenceTransform.localRotation;
        transformToAlign.localPosition = referenceTransform.localPosition;
        transformToAlign.localScale = referenceTransform.localScale;

        for (int i = 0; i < referenceTransform.childCount; i++)
        {
            AlignHierarchies(transformToAlign.GetChild(i),
                referenceTransform.GetChild(i));
        }
    }

    private void CreateCustomBoneIdToHumanBodyBoneMapping()
    {
        CopyBoneIdToHumanBodyBoneMapping();
        AdjustCustomBoneIdToHumanBodyBoneMapping();
    }

    private void CopyBoneIdToHumanBodyBoneMapping()
    {
        _customBoneIdToHumanBodyBone.Clear();
        if (_skeletonType == SkeletonType.FullBody)
        {
            foreach (var keyValuePair in _bodyBonesMappingInterface.GetFullBodyBoneIdToHumanBodyBone)
            {
                _customBoneIdToHumanBodyBone.Add(keyValuePair.Key, keyValuePair.Value);
            }
        }
        else
        {
            foreach (var keyValuePair in _bodyBonesMappingInterface.GetBoneIdToHumanBodyBone)
            {
                _customBoneIdToHumanBodyBone.Add(keyValuePair.Key, keyValuePair.Value);
            }
        }
    }

    private void AdjustCustomBoneIdToHumanBodyBoneMapping()
    {
        // if there is a mapping override that the user provided,
        // enforce it.
        foreach (var adjustment in _adjustments)
        {
            bool fullJointSet = _skeletonType == SkeletonType.FullBody;
            if ((fullJointSet &&
                 adjustment.FullBodyBoneIdOverrideValue ==
                 OVRHumanBodyBonesMappings.FullBodyTrackingBoneId.NoOverride) ||
                adjustment.BoneIdOverrideValue == OVRHumanBodyBonesMappings.BodyTrackingBoneId.NoOverride)

            {
                continue;
            }
            if ((fullJointSet &&
                 adjustment.FullBodyBoneIdOverrideValue == OVRHumanBodyBonesMappings.FullBodyTrackingBoneId.Remove) ||
                adjustment.BoneIdOverrideValue == OVRHumanBodyBonesMappings.BodyTrackingBoneId.Remove)

            {
                RemoveMappingCorrespondingToHumanBodyBone(adjustment.Joint);
            }
            else
            {
                if (fullJointSet)
                {
                    _customBoneIdToHumanBodyBone[(BoneId)adjustment.FullBodyBoneIdOverrideValue]
                        = adjustment.Joint;
                }
                else
                {
                    _customBoneIdToHumanBodyBone[(BoneId)adjustment.BoneIdOverrideValue]
                        = adjustment.Joint;
                }
            }
        }
    }

    private void RemoveMappingCorrespondingToHumanBodyBone(HumanBodyBones boneId)
    {
        foreach (var key in _customBoneIdToHumanBodyBone.Keys)
        {
            if (_customBoneIdToHumanBodyBone[key] == boneId)
            {
                _customBoneIdToHumanBodyBone.Remove(key);
                return;
            }
        }
    }

    protected override void Update()
    {
        if (!ShouldRunUpdateThisFrame())
        {
            return;
        }

        UpdateSkeleton();

        RecomputeSkeletalOffsetsIfNecessary();

        AlignTargetWithSource();
    }

    protected bool ShouldRunUpdateThisFrame()
    {
        bool isFixedUpdate = Time.inFixedTimeStep;
        switch (_updateType)
        {
            case UpdateType.FixedUpdateOnly:
                return isFixedUpdate;
            case UpdateType.UpdateOnly:
                return !isFixedUpdate;
            default:
                return true;
        }
    }

    protected void RecomputeSkeletalOffsetsIfNecessary()
    {
        if (OffsetComputationNeededThisFrame())
        {
            ComputeOffsetsUsingSkeletonComponent();
        }
    }

    /// <summary>
    /// Indicates if skeletal offsets will be computed during this
    /// frame. This usually happens if there is a change in the
    /// input skeletal data, a change in the target skeletal scale,
    /// et cetera.
    /// </summary>
    /// <returns>True if computation is required; false if not.</returns>
    protected bool OffsetComputationNeededThisFrame()
    {
        bool skeletonIsNotReady = !IsInitialized ||
            BindPoses == null || BindPoses.Count == 0;
        if (skeletonIsNotReady)
        {
            return false;
        }

        bool skeletalCountChange = _lastSkelChangeCount != SkeletonChangedCount;
        bool scaleChanged =
          (transform.lossyScale - _lastTrackedScale).sqrMagnitude
          > Mathf.Epsilon;

        return skeletalCountChange || scaleChanged;
    }

    /// <summary>
    /// Compute skeletal offsets using the current input skeletal data
    /// and the target skeletal data.
    /// </summary>
    protected void ComputeOffsetsUsingSkeletonComponent()
    {
        if (!IsInitialized ||
            BindPoses == null || BindPoses.Count == 0)
        {
            return;
        }

        if (_sourceSkeletonData == null)
        {
            _sourceSkeletonData = new OVRSkeletonMetadata(this, false, _customBoneIdToHumanBodyBone,
                _skeletonType == SkeletonType.FullBody,
                _bodyBonesMappingInterface);
        }
        else
        {
            if (_skeletonType == SkeletonType.FullBody)
            {
                _sourceSkeletonData.BuildBoneDataSkeletonFullBody(this, false,
                    _customBoneIdToHumanBodyBone, _bodyBonesMappingInterface);
            }
            else
            {
                _sourceSkeletonData.BuildBoneDataSkeleton(this, false,
                    _customBoneIdToHumanBodyBone, _bodyBonesMappingInterface);
            }
        }

        _sourceSkeletonData.BuildCoordinateAxesForAllBones();

        if (_sourceSkeletonTPoseData == null)
        {
            _sourceSkeletonTPoseData = new OVRSkeletonMetadata(this, true, _customBoneIdToHumanBodyBone,
                _skeletonType == SkeletonType.FullBody,
                _bodyBonesMappingInterface);
        }
        else
        {
            if (_skeletonType == SkeletonType.FullBody)
            {
                _sourceSkeletonTPoseData.BuildBoneDataSkeletonFullBody(this, true,
                    _customBoneIdToHumanBodyBone, _bodyBonesMappingInterface);
            }
            else
            {
                _sourceSkeletonTPoseData.BuildBoneDataSkeleton(this, true,
                    _customBoneIdToHumanBodyBone, _bodyBonesMappingInterface);
            }
        }

        _sourceSkeletonTPoseData.BuildCoordinateAxesForAllBones();

        // snap the target to t-pose, then rebuild its data
        // this forces the T-pose to respect the current scale values.
        AlignHierarchies(_animatorTargetSkeleton.GetBoneTransform(HumanBodyBones.Hips),
            _targetTPoseTransformDup[HumanBodyBones.Hips]);
        _targetSkeletonData.BuildCoordinateAxesForAllBones();

        for (var i = 0; i < BindPoses.Count; i++)
        {
            if (!_customBoneIdToHumanBodyBone.TryGetValue(BindPoses[i].Id, out var humanBodyBone))
            {
                continue;
            }

            if (!_targetSkeletonData.BodyToBoneData.TryGetValue(humanBodyBone, out var targetData))
            {
                continue;
            }

            var bodySection = _bodyBonesMappingInterface.GetBoneToBodySection[humanBodyBone];

            if (!IsBodySectionInArray(bodySection,
                    _skeletonType == SkeletonType.FullBody ? _fullBodySectionsToAlign : _bodySectionsToAlign
                ))
            {
                continue;
            }

            if (!_sourceSkeletonTPoseData.BodyToBoneData.TryGetValue(humanBodyBone, out var sourceTPoseData))
            {
                continue;
            }

            if (!_sourceSkeletonData.BodyToBoneData.TryGetValue(humanBodyBone, out var sourcePoseData))
            {
                continue;
            }

            // if encountered degenerate source bones, skip
            if (sourceTPoseData.DegenerateJoint || sourcePoseData.DegenerateJoint)
            {
                targetData.CorrectionQuaternion = null;
                continue;
            }

            var forwardSource = sourceTPoseData.JointPairOrientation * Vector3.forward;
            var forwardTarget = targetData.JointPairOrientation * Vector3.forward;
            var targetToSrc = Quaternion.FromToRotation(forwardTarget, forwardSource);

            var sourceRotationValueInv = Quaternion.Inverse(BindPoses[i].Transform.rotation);

            targetData.CorrectionQuaternion =
                sourceRotationValueInv * targetToSrc *
                 _animatorTargetSkeleton.GetBoneTransform(humanBodyBone).rotation;
        }

        _lastSkelChangeCount = SkeletonChangedCount;
        _lastTrackedScale = transform.lossyScale;
    }

    protected static bool IsBodySectionInArray(
        OVRHumanBodyBonesMappings.BodySection bodySectionToCheck,
        OVRHumanBodyBonesMappings.BodySection[] sectionArrayToCheck)
    {
        foreach (var bodySection in sectionArrayToCheck)
        {
            if (bodySection == bodySectionToCheck)
            {
                return true;
            }
        }

        return false;
    }

    private void AlignTargetWithSource()
    {
        if (!IsInitialized || Bones == null || Bones.Count == 0)
        {
            return;
        }

        for (var i = 0; i < Bones.Count; i++)
        {
            if (!_customBoneIdToHumanBodyBone.TryGetValue(Bones[i].Id, out var humanBodyBone))
            {
                continue;
            }

            if (!_targetSkeletonData.BodyToBoneData.TryGetValue(humanBodyBone, out var targetData))
            {
                continue;
            }

            // Skip if we cannot map the joint at all.
            if (!targetData.CorrectionQuaternion.HasValue)
            {
                continue;
            }

            var targetJoint = targetData.OriginalJoint;
            var correctionQuaternion = targetData.CorrectionQuaternion.Value;
            var adjustment = FindAdjustment(humanBodyBone);

            var bodySectionOfJoint = _bodyBonesMappingInterface.GetBoneToBodySection[humanBodyBone];
            var shouldUpdatePosition = IsBodySectionInArray(
                bodySectionOfJoint,
                _skeletonType == SkeletonType.FullBody ? _fullBodySectionToPosition : _bodySectionToPosition

            );

            if (adjustment == null)
            {
                targetJoint.rotation = Bones[i].Transform.rotation * correctionQuaternion;
                if (shouldUpdatePosition)
                {
                    targetJoint.position = Bones[i].Transform.position;
                }
            }
            else
            {
                if (!adjustment.DisableRotationTransform)
                {
                    targetJoint.rotation = Bones[i].Transform.rotation * correctionQuaternion;
                }

                targetJoint.rotation *= adjustment.RotationChange;
                targetJoint.rotation *= adjustment.PrecomputedRotationTweaks;

                if (!adjustment.DisablePositionTransform && shouldUpdatePosition)
                {
                    targetJoint.position = Bones[i].Transform.position;
                }
                targetJoint.position += adjustment.PositionChange;
            }
        }
    }

    protected JointAdjustment FindAdjustment(HumanBodyBones boneId)
    {
        foreach (var adjustment in _adjustments)
        {
            if (adjustment.Joint == boneId)
            {
                return adjustment;
            }
        }

        return null;
    }
}
