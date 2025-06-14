using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using static Basis.Scripts.Common.BasisTransformMapping;

[DefaultExecutionOrder(15001)]
[System.Serializable]
public class BasisMuscleDriver
{
    [SerializeField] public BasisFingerPose LeftHandPoses;
    [SerializeField] public BasisFingerPose RightHandPoses;

    public float LerpSpeed = 17f;

    public Transform LeftHand;
    public Transform RightHand;
    public bool HasLeftHand;
    public bool HasRightHand;

    private Dictionary<(HandSide, FingerJoint), Transform> jointTransforms;
    private Dictionary<(HandSide, FingerJoint), GameObject> debugCubes;

    public Dictionary<HumanBodyBones, TransformPose> TposeData;
    public Quaternion vrToTposeCorrection = Quaternion.Euler(270, 120f, 0f); // Adjust as needed based on actual coordinate system
    public Quaternion vrToTposeCorrectionFingers = Quaternion.Euler(280f, 0, 180); // Adjust as needed based on actual coordinate system
    public void Initialize()
    {
        var Mapping = BasisLocalPlayer.Instance.LocalAvatarDriver.References;

        HasLeftHand = Mapping.HasleftHand;
        HasRightHand = Mapping.HasrightHand;

        if (HasLeftHand) LeftHand = Mapping.leftHand;
        if (HasRightHand) RightHand = Mapping.rightHand;

        jointTransforms = BuildJointTransformMap(
            new[] { Mapping.LeftThumb, Mapping.LeftIndex, Mapping.LeftMiddle, Mapping.LeftRing, Mapping.LeftLittle },
            new[] { Mapping.RightThumb, Mapping.RightIndex, Mapping.RightMiddle, Mapping.RightRing, Mapping.RightLittle }
        );

        TposeData = Mapping.TPoseRecords; // Assuming Mapping provides this

        CreateDebugCubes();
    }
    public void OnDestroy()
    {
    }
    private void CreateDebugCubes()
    {
        debugCubes = new Dictionary<(HandSide, FingerJoint), GameObject>();

        foreach (var kvp in jointTransforms)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.localScale = Vector3.one * 0.01f;
            cube.name = $"DebugCube_{kvp.Key.Item1}_{kvp.Key.Item2}";
            cube.GetComponent<Collider>().enabled = false;
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.parent = BasisLocalPlayer.Instance.transform;
            debugCubes[kvp.Key] = cube;
        }
    }
    public void UpdateFingers(BasisLocalAvatarDriver localAvatarDriver)
    {
        if (HasLeftHand)
        {
            TransformPose tposedata = TposeData[HumanBodyBones.LeftHand];
            ConvertData(LeftHandPoses.PalmPos, LeftHandPoses.PalmRot, tposedata.Position, tposedata.Rotation * vrToTposeCorrection, out Vector3 convertedPosition, out quaternion rotation);
            LeftHand.SetPositionAndRotation(convertedPosition, rotation);
        }
        if (HasRightHand)
        {
            TransformPose tposedata = TposeData[HumanBodyBones.RightHand];
            ConvertData(RightHandPoses.PalmPos, RightHandPoses.PalmRot, tposedata.Position, tposedata.Rotation * vrToTposeCorrection, out Vector3 convertedPosition, out quaternion rotation);
            RightHand.SetPositionAndRotation(convertedPosition, rotation);
        }

        UpdateFinger(LeftHandPoses, HandSide.Left);
        UpdateFinger(RightHandPoses, HandSide.Right);
    }

    public void UpdateFinger(BasisFingerPose finger, HandSide hand)
    {
        float Destination = Time.deltaTime * LerpSpeed;

        void ApplyPose(Vector3[] targetPositions, Quaternion[] targetRotations, FingerType fingerType)
        {
            for (int Index = 0; Index < 3; Index++)
            {
                FingerJoint joint = (FingerJoint)((int)fingerType * 3 + Index);
                if (!jointTransforms.TryGetValue((hand, joint), out Transform targetTransform))
                {
                    continue;
                }

                // Use HumanBodyBones naming or custom mapping
                HumanBodyBones bone = MapFingerJointToHumanBone(hand, joint);
                if (!TposeData.TryGetValue(bone, out TransformPose tpose))
                {
                    continue;
                }

                ConvertData(
                    targetPositions[Index],
                    targetRotations[Index],
                    tpose.Position,
                    tpose.Rotation * vrToTposeCorrectionFingers,
                    out Vector3 convertedPos,
                    out quaternion convertedRot
                );

                if (targetTransform != null)
                {
                    targetTransform.SetPositionAndRotation(
                        Vector3.Lerp(targetTransform.position, convertedPos, Destination),
                        Quaternion.Slerp(targetTransform.rotation, convertedRot, Destination)
                    );
                }

                if (debugCubes.TryGetValue((hand, joint), out GameObject cube))
                {
                    cube.transform.SetPositionAndRotation(convertedPos, convertedRot);
                }
            }
        }

        ApplyPose(finger.thumbPositions, finger.thumbRotations, FingerType.Thumb);
        ApplyPose(finger.indexPositions, finger.indexRotations, FingerType.Index);
        ApplyPose(finger.middlePositions, finger.middleRotations, FingerType.Middle);
        ApplyPose(finger.ringPositions, finger.ringRotations, FingerType.Ring);
        ApplyPose(finger.littlePositions, finger.littleRotations, FingerType.Little);
    }
    public void ConvertData(Vector3 localRawPosition, Quaternion localRawRotation, Vector3 TposePosition, Quaternion TposeRotation, out Vector3 convertedPosition, out quaternion convertedRotation)
    {
        localRawPosition -= TposePosition;
        localRawRotation = localRawRotation * Quaternion.Inverse(TposeRotation);
        var player = BasisLocalPlayer.Instance;
        var parentTransform = player.transform;

        float scale = player?.CurrentHeight?.SelectedAvatarToAvatarDefaultScale ?? 1f;

        float3 scaledPosition = localRawPosition * scale;
        quaternion localRotation = localRawRotation;

        convertedPosition = parentTransform.localToWorldMatrix.MultiplyPoint3x4(scaledPosition);
        convertedRotation = parentTransform.rotation * localRotation;
    }
    private HumanBodyBones MapFingerJointToHumanBone(HandSide hand, FingerJoint joint)
    {
        return joint switch
        {
            FingerJoint.ThumbProximal => hand == HandSide.Left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal,
            FingerJoint.ThumbIntermediate => hand == HandSide.Left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate,
            FingerJoint.ThumbDistal => hand == HandSide.Left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal,

            FingerJoint.IndexProximal => hand == HandSide.Left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal,
            FingerJoint.IndexIntermediate => hand == HandSide.Left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate,
            FingerJoint.IndexDistal => hand == HandSide.Left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal,

            FingerJoint.MiddleProximal => hand == HandSide.Left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal,
            FingerJoint.MiddleIntermediate => hand == HandSide.Left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate,
            FingerJoint.MiddleDistal => hand == HandSide.Left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal,

            FingerJoint.RingProximal => hand == HandSide.Left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal,
            FingerJoint.RingIntermediate => hand == HandSide.Left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate,
            FingerJoint.RingDistal => hand == HandSide.Left ? HumanBodyBones.LeftRingDistal : HumanBodyBones.RightRingDistal,

            FingerJoint.LittleProximal => hand == HandSide.Left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal,
            FingerJoint.LittleIntermediate => hand == HandSide.Left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate,
            FingerJoint.LittleDistal => hand == HandSide.Left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal,

            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, null)
        };
    }
    private Dictionary<(HandSide, FingerJoint), Transform> BuildJointTransformMap(Transform[][] leftHand, Transform[][] rightHand)
    {
        Dictionary<(HandSide, FingerJoint), Transform> map = new Dictionary<(HandSide, FingerJoint), Transform>();

        for (int finger = 0; finger < 5; finger++)
        {
            for (int joint = 0; joint < 3; joint++)
            {
                map[(HandSide.Left, (FingerJoint)(finger * 3 + joint))] = leftHand[finger][joint];
                map[(HandSide.Right, (FingerJoint)(finger * 3 + joint))] = rightHand[finger][joint];
            }
        }

        return map;
    }

    public enum HandSide
    {
        Left,
        Right
    }

    public enum FingerJoint
    {
        ThumbProximal, ThumbIntermediate, ThumbDistal,
        IndexProximal, IndexIntermediate, IndexDistal,
        MiddleProximal, MiddleIntermediate, MiddleDistal,
        RingProximal, RingIntermediate, RingDistal,
        LittleProximal, LittleIntermediate, LittleDistal
    }

    public enum FingerType
    {
        Thumb = 0,
        Index = 1,
        Middle = 2,
        Ring = 3,
        Little = 4
    }
}
