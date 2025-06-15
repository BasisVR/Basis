using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using System;
using System.Collections.Generic;
using UnityEngine;
[DefaultExecutionOrder(15001)]
[System.Serializable]
public class BasisHandDriver
{
    [SerializeField]
    public BasisFingerPose LeftHandPoses;
    [SerializeField]
    public BasisFingerPose RightHandPoses;

    public float LerpSpeed = 17f;
    public Transform LeftHand;
    public Transform RightHand;
    public bool HasLeftHand;
    public bool HasRightHand;

    public Dictionary<HumanBodyBones, BasisCalibratedCoords> TposeData = new Dictionary<HumanBodyBones, BasisCalibratedCoords>();
    public HumanPoseHandler poseHandler;
    public HumanPose pose;

    public float[] LeftThumb = new float[4];
    public float[] LeftIndex = new float[4];
    public float[] LeftMiddle = new float[4];
    public float[] LeftRing = new float[4];
    public float[] LeftLittle = new float[4];

    public float[] RightThumb = new float[4];
    public float[] RightIndex = new float[4];
    public float[] RightMiddle = new float[4];
    public float[] RightRing = new float[4];
    public float[] RightLittle = new float[4];

    public BasisCalibratedCoords[] LeftHandCoords = new BasisCalibratedCoords[15];
    public BasisCalibratedCoords[] RightHandCoords = new BasisCalibratedCoords[15];


    private Transform[] LeftHandJoints = new Transform[15];
    private Transform[] RightHandJoints = new Transform[15];
    private bool[] LeftHandJointsbools = new bool[15];
    private bool[] RightHandJointsbools = new bool[15];
    [SerializeField]
    public BasisFingerPoseParams[] LeftHandParams = new BasisFingerPoseParams[5];
    [SerializeField]
    public BasisFingerPoseParams[] RightHandParams = new BasisFingerPoseParams[5];
    const float epsilon = 0.0001f;
    const int fingersCount = 5;
    const int jointsPerFinger = 3;
    const int totalCoords = fingersCount * jointsPerFinger;

    // Reusable arrays to avoid per-call allocations
    private readonly Vector3[] weightedPositions = new Vector3[jointsPerFinger];
    private readonly Vector4[] rotationAccumulator = new Vector4[jointsPerFinger];
    private readonly float[] totalWeights = new float[jointsPerFinger];
    private BasisCalibratedCoords[] LeftHandDestination = new BasisCalibratedCoords[totalCoords];
    private BasisCalibratedCoords[] RightHandDestination = new BasisCalibratedCoords[totalCoords];
    public BasisStoredHandPose[] LeftHandPoseData;
    public BasisStoredHandPose[] RightHandPoseData;
    public void Initialize()
    {
        LeftThumb = new float[4];
        LeftIndex = new float[4];
        LeftMiddle = new float[4];
        LeftRing = new float[4];
        LeftLittle = new float[4];

        RightThumb = new float[4];
        RightIndex = new float[4];
        RightMiddle = new float[4];
        RightRing = new float[4];
        RightLittle = new float[4];

        BasisTransformMapping Mapping = BasisLocalPlayer.Instance.LocalAvatarDriver.References;

        HasLeftHand = Mapping.HasleftHand;
        HasRightHand = Mapping.HasrightHand;

        if (HasLeftHand) LeftHand = Mapping.leftHand;
        if (HasRightHand) RightHand = Mapping.rightHand;

        TposeData = Mapping.TPoseRecords;

        if (LeftHandParams == null || LeftHandParams.Length != 5)
        {
            LeftHandParams = new BasisFingerPoseParams[5];
        }
        if (RightHandParams == null || RightHandParams.Length != 5)
        {
            RightHandParams = new BasisFingerPoseParams[5];
        }

        for (int i = 0; i < 15; i++)
        {
            LeftHandJointsbools[i] = Mapping.GetTransform((HumanBodyBones)(24 + i), out LeftHandJoints[i]);
            RightHandJointsbools[i] = Mapping.GetTransform((HumanBodyBones)(39 + i), out RightHandJoints[i]);
        }
        Initalize(BasisLocalPlayer.Instance.BasisAvatar.Animator);
    }
    public void OnDestroy() { }
    public void Initalize(Animator animator)
    {
        poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        pose = new HumanPose();
        RecordAllPoses();
    }
    public void RecordAllPoses()
    {
        poseHandler.GetHumanPose(ref pose);
        List<BasisFingerPoseParams> samples = new List<BasisFingerPoseParams>
        {
           new BasisFingerPoseParams(1f, 1f),//1
           new BasisFingerPoseParams(0f, 1f),//2
          new  BasisFingerPoseParams(1f, 0f),//3
          new  BasisFingerPoseParams(0f, 0f),//4
          new  BasisFingerPoseParams(-1f, -1f),//5
          new BasisFingerPoseParams(0f, -1f),//6
           new BasisFingerPoseParams(-1f, 0f)//7
        };

        List<BasisStoredHandPose> LeftHandPoseDataList = new List<BasisStoredHandPose>();
        List<BasisStoredHandPose> RightHandPoseDataList = new List<BasisStoredHandPose>();
        foreach (BasisFingerPoseParams sample in samples)
        {
            SetAndRecordPose(sample, ref LeftHandCoords, ref RightHandCoords);

            LeftHandPoseDataList.Add(new BasisStoredHandPose(sample, (BasisCalibratedCoords[])LeftHandCoords.Clone()));
            RightHandPoseDataList.Add(new BasisStoredHandPose(sample, (BasisCalibratedCoords[])RightHandCoords.Clone()));
        }
        LeftHandPoseData = LeftHandPoseDataList.ToArray();
        RightHandPoseData = RightHandPoseDataList.ToArray();
    }
    public void SetAndRecordPose(BasisFingerPoseParams poses, ref BasisCalibratedCoords[] LeftHand, ref BasisCalibratedCoords[] RightHand)
    {
        SetMuscleData(ref LeftThumb, poses);
        SetMuscleData(ref LeftIndex, poses);
        SetMuscleData(ref LeftMiddle, poses);
        SetMuscleData(ref LeftRing, poses);
        SetMuscleData(ref LeftLittle, poses);

        SetMuscleData(ref RightThumb, poses);
        SetMuscleData(ref RightIndex, poses);
        SetMuscleData(ref RightMiddle, poses);
        SetMuscleData(ref RightRing, poses);
        SetMuscleData(ref RightLittle, poses);

        ApplyMuscleData();
        poseHandler.SetHumanPose(ref pose);
        RecordHandPositionsAndRotations(ref LeftHand, ref RightHand);
    }

    public void RecordHandPositionsAndRotations(ref BasisCalibratedCoords[] LeftHand, ref BasisCalibratedCoords[] RightHand)
    {
        if (LeftHand == null || LeftHand.Length != 15) LeftHand = new BasisCalibratedCoords[15];
        if (RightHand == null || RightHand.Length != 15) RightHand = new BasisCalibratedCoords[15];

        for (int index = 0; index < 15; index++)
        {
            if (LeftHandJointsbools[index] && LeftHandJoints[index] != null)
            {
                LeftHand[index] = new BasisCalibratedCoords(
                    LeftHandJoints[index].localPosition,
                    LeftHandJoints[index].localRotation
                );
            }
            else
            {
                LeftHand[index] = new BasisCalibratedCoords(Vector3.zero, Quaternion.identity);
            }

            if (RightHandJointsbools[index] && RightHandJoints[index] != null)
            {
                RightHand[index] = new BasisCalibratedCoords(
                    RightHandJoints[index].localPosition,
                    RightHandJoints[index].localRotation
                );
            }
            else
            {
                RightHand[index] = new BasisCalibratedCoords(Vector3.zero, Quaternion.identity);
            }
        }
    }
    public void ApplyMuscleData()
    {
        Array.Copy(LeftThumb, 0, pose.muscles, 55, 4);
        Array.Copy(LeftIndex, 0, pose.muscles, 59, 4);
        Array.Copy(LeftMiddle, 0, pose.muscles, 63, 4);
        Array.Copy(LeftRing, 0, pose.muscles, 67, 4);
        Array.Copy(LeftLittle, 0, pose.muscles, 71, 4);

        Array.Copy(RightThumb, 0, pose.muscles, 75, 4);
        Array.Copy(RightIndex, 0, pose.muscles, 79, 4);
        Array.Copy(RightMiddle, 0, pose.muscles, 83, 4);
        Array.Copy(RightRing, 0, pose.muscles, 87, 4);
        Array.Copy(RightLittle, 0, pose.muscles, 91, 4);
    }

    public void SetMuscleData(ref float[] muscleArray, BasisFingerPoseParams pose)
    {
        Array.Fill(muscleArray, pose.Stretch);
        muscleArray[1] = pose.Spread;
    }
    public void UpdateFingers()
    {
        ApplyInterpolatedPose(LeftHandParams, RightHandParams);
    }
    public void ApplyInterpolatedPose(BasisFingerPoseParams[] leftHand, BasisFingerPoseParams[] rightHand)
    {
        InterpolatePose(leftHand, LeftHandPoseData, ref LeftHandDestination);
        InterpolatePose(rightHand, RightHandPoseData, ref RightHandDestination);

        for (int Index = 0; Index < 15; Index++)
        {
            if (RightHandJointsbools[Index])
            {
                RightHandJoints[Index].SetLocalPositionAndRotation(RightHandDestination[Index].position, RightHandDestination[Index].rotation);
            }
            if (LeftHandJointsbools[Index])
            {
                LeftHandJoints[Index].SetLocalPositionAndRotation(LeftHandDestination[Index].position, LeftHandDestination[Index].rotation);
            }
        }
    }
    public void InterpolatePose(BasisFingerPoseParams[] targetPose, BasisStoredHandPose[] poseData, ref BasisCalibratedCoords[] result)
    {
        int poseCount = poseData.Length;

        for (int fingerIndex = 0; fingerIndex < fingersCount; fingerIndex++)
        {
            // Initialize accumulators
            for (int j = 0; j < jointsPerFinger; j++)
            {
                weightedPositions[j] = Vector3.zero;
                rotationAccumulator[j] = Vector4.zero;
                totalWeights[j] = 0f;
            }

            float targetStretch = targetPose[fingerIndex].Stretch;
            float targetSpread = targetPose[fingerIndex].Spread;

            for (int poseIndex = 0; poseIndex < poseCount; poseIndex++)
            {
                var storedPose = poseData[poseIndex];
                var poseParam = storedPose.FingerPoseForPosition;

                float diffStretch = targetStretch - poseParam.Stretch;
                float diffSpread = targetSpread - poseParam.Spread;
                float sqrDist = diffStretch * diffStretch + diffSpread * diffSpread;

                float weight = 1f / (Mathf.Sqrt(sqrDist) + epsilon);

                var joints = storedPose.FingerJoints;

                for (int j = 0; j < jointsPerFinger; j++)
                {
                    int globalIndex = fingerIndex * jointsPerFinger + j;
                    ref BasisCalibratedCoords joint = ref joints[globalIndex];

                    Vector3 Position = joint.position;
                    weightedPositions[j] += Position * weight;

                    Quaternion Rotation = joint.rotation;
                    Vector4 quatVec = new Vector4(Rotation.x, Rotation.y, Rotation.z, Rotation.w);
                    if (Vector4.Dot(rotationAccumulator[j], quatVec) < 0f)
                        quatVec = -quatVec;

                    rotationAccumulator[j] += quatVec * weight;
                    totalWeights[j] += weight;
                }
            }

            // Normalize accumulators and assign results
            for (int j = 0; j < jointsPerFinger; j++)
            {
                int globalIndex = fingerIndex * jointsPerFinger + j;
                float weight = totalWeights[j];

                if (weight > 0f)
                {
                    Vector3 pos = weightedPositions[j] / weight;
                    Vector4 accQuat = rotationAccumulator[j] / weight;

                    Quaternion rot = new Quaternion(accQuat.x, accQuat.y, accQuat.z, accQuat.w);
                    rot = Quaternion.Normalize(rot);

                    result[globalIndex] = new BasisCalibratedCoords(pos, rot);
                }
                else
                {
                    result[globalIndex] = new BasisCalibratedCoords(Vector3.zero, Quaternion.identity);
                }
            }
        }
    }
}
