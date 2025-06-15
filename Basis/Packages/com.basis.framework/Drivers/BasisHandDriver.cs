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

    public List<BasisStoredHandPose> LeftHandPoseData = new List<BasisStoredHandPose>();
    public List<BasisStoredHandPose> RightHandPoseData = new List<BasisStoredHandPose>();


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
           new BasisFingerPoseParams(1f, 1f),
           new BasisFingerPoseParams(0f, 1f),
          new  BasisFingerPoseParams(1f, 0f),
          new  BasisFingerPoseParams(0f, 0f),
          new  BasisFingerPoseParams(-1f, -1f),
          new BasisFingerPoseParams(0f, -1f),
           new BasisFingerPoseParams(-1f, 0f)
        };

        foreach (BasisFingerPoseParams sample in samples)
        {
            SetAndRecordPose(sample, ref LeftHandCoords, ref RightHandCoords);

            LeftHandPoseData.Add(new BasisStoredHandPose(sample, (BasisCalibratedCoords[])LeftHandCoords.Clone()));
            RightHandPoseData.Add(new BasisStoredHandPose(sample, (BasisCalibratedCoords[])RightHandCoords.Clone()));
        }
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
    public void ApplyInterpolatedPose(BasisFingerPoseParams[] LeftHand, BasisFingerPoseParams[] RightHand)
    {
        float convertedLerpSpeed = Time.deltaTime * LerpSpeed;
        ApplyLeftInterpolatedPose(LeftHand, convertedLerpSpeed);
        ApplyRightInterpolatedPose(RightHand, convertedLerpSpeed);
    }
    public void ApplyLeftInterpolatedPose(BasisFingerPoseParams[] poses, float convertedLerpSpeed)
    {
        if (!HasLeftHand) return;

        InterpolatePose(poses, LeftHandPoseData, ref LeftHandDestination);

        for (int index = 0; index < 15; index++)
        {
            if (LeftHandJointsbools[index])
            {
                LeftHandJoints[index].SetLocalPositionAndRotation(LeftHandDestination[index].position, LeftHandDestination[index].rotation);
            }
        }
    }

    public void ApplyRightInterpolatedPose(BasisFingerPoseParams[] poses, float convertedLerpSpeed)
    {
        if (!HasRightHand) return;

        InterpolatePose(poses, RightHandPoseData, ref RightHandDestination);

        for (int index = 0; index < 15; index++)
        {
            if (RightHandJointsbools[index])
            {
                RightHandJoints[index].SetLocalPositionAndRotation(RightHandDestination[index].position, RightHandDestination[index].rotation);
            }
        }
    }
    /// <summary>
    /// Interpolates the hand pose based on targetFingerPoses and stored pose data.
    /// </summary>
    /// <param name="targetPose">Target finger pose params.</param>
    /// <param name="poseData">List of stored hand poses.</param>
    /// <returns>Array of interpolated BasisCalibratedCoords for all finger joints.</returns>
    public void InterpolatePose(BasisFingerPoseParams[] targetPose, List<BasisStoredHandPose> poseData,ref BasisCalibratedCoords[] result)
    {
        int Count = poseData.Count;
        for (int fingerIndex = 0; fingerIndex < fingersCount; fingerIndex++)
        {
            // Reset accumulators for this finger's joints
            for (int j = 0; j < jointsPerFinger; j++)
            {
                weightedPositions[j] = Vector3.zero;
                rotationAccumulator[j] = Vector4.zero;
                totalWeights[j] = 0f;
            }

            Vector2 targetVec = new Vector2(targetPose[fingerIndex].Stretch, targetPose[fingerIndex].Spread);

            for (int poseIndex = 0; poseIndex < Count; poseIndex++)
            {
                BasisStoredHandPose storedPose = poseData[poseIndex];
                Vector2 storedVec = new Vector2(storedPose.FingerPoseForPosition.Stretch, storedPose.FingerPoseForPosition.Spread);

                // Use squared distance to avoid sqrt - adjust weight formula accordingly
                float sqrDist = (targetVec - storedVec).sqrMagnitude;
                float weight = 1f / (Mathf.Sqrt(sqrDist) + epsilon);  // keep original formula but could try alternatives

                for (int jointIndex = 0; jointIndex < jointsPerFinger; jointIndex++)
                {
                    int globalJointIndex = fingerIndex * jointsPerFinger + jointIndex;

                    // Use 'ref' to avoid copying struct if allowed
                    ref BasisCalibratedCoords joint = ref storedPose.FingerJoints[globalJointIndex];

                    Vector3 Position = joint.position;
                    // Accumulate weighted position
                    weightedPositions[jointIndex] += Position * weight;

                    Quaternion Rotation = joint.rotation;
                    // Quaternion blending as Vector4
                    Vector4 quatVec = new Vector4(Rotation.x, Rotation.y, Rotation.z, Rotation.w);
                    Vector4 accVec = rotationAccumulator[jointIndex];

                    // Hemisphere check for quaternion averaging
                    if (Vector4.Dot(accVec, quatVec) < 0f)
                        quatVec = -quatVec;

                    rotationAccumulator[jointIndex] = accVec + quatVec * weight;

                    totalWeights[jointIndex] += weight;
                }
            }

            // Normalize and assign results for this finger's joints
            for (int jointIndex = 0; jointIndex < jointsPerFinger; jointIndex++)
            {
                float w = totalWeights[jointIndex];
                int globalJointIndex = fingerIndex * jointsPerFinger + jointIndex;

                if (w > 0f)
                {
                    Vector3 pos = weightedPositions[jointIndex] / w;

                    // Normalize quaternion accumulator
                    Vector4 accQuatVec = rotationAccumulator[jointIndex] / w;
                    Quaternion rot = new Quaternion(accQuatVec.x, accQuatVec.y, accQuatVec.z, accQuatVec.w);
                    rot = Quaternion.Normalize(rot);

                    result[globalJointIndex] = new BasisCalibratedCoords(pos, rot);
                }
                else
                {
                    result[globalJointIndex] = new BasisCalibratedCoords(Vector3.zero, Quaternion.identity);
                }
            }
        }
    }
}
