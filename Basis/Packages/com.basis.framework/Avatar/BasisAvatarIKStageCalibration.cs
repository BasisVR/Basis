using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// this class handles tracker calibration onto the IK system.
    /// </summary>
    public static class BasisAvatarIKStageCalibration
    {
        public static class BasisHintBiasStore
        {
            public static readonly Dictionary<BasisBoneTrackedRole, Vector3> LocalOffset = new();

            public static void Set(BasisBoneTrackedRole role, Vector3 localOffset) => LocalOffset[role] = localOffset;
            public static bool TryGet(BasisBoneTrackedRole role, out Vector3 localOffset) => LocalOffset.TryGetValue(role, out localOffset);
        }
        /// <summary>
        /// If Any trackers are actively connected to the IK system
        /// </summary>
        public static bool HasFBIKTrackers = false;
        /// <summary>
        /// gets all roles in a desired order
        /// </summary>
        /// <returns></returns>
        private static List<BasisBoneTrackedRole> GetAllRolesDesired()
        {
            List<BasisBoneTrackedRole> rolesToDiscover = new List<BasisBoneTrackedRole>(23);
            foreach (BasisBoneTrackedRole role in desiredOrder)
            {
                rolesToDiscover.Add(role);
            }
            // Create a dictionary for quick index lookup
            Dictionary<BasisBoneTrackedRole, int> orderLookup = new Dictionary<BasisBoneTrackedRole, int>();
            for (int Index = 0; Index < desiredOrder.Length; Index++)
            {
                orderLookup[desiredOrder[Index]] = Index;
            }

            // Assign a large index value to roles not in the desired order
            int largeIndex = desiredOrder.Length;

            // Sort the list based on the desired order
            rolesToDiscover.Sort((x, y) =>
            {
                int indexX = orderLookup.ContainsKey(x) ? orderLookup[x] : largeIndex;
                int indexY = orderLookup.ContainsKey(y) ? orderLookup[y] : largeIndex;
                return indexX.CompareTo(indexY);
            });

            return rolesToDiscover;
        }
        /// <summary>
        /// does calibration of trackers
        /// </summary>
        public static void FullBodyCalibration()
        {
            BasisHeightDriver.OnAvatarFBCalibration();//avatar height is good,player height is needed
            HasFBIKTrackers = false;
            BasisDeviceManagement.UnassignFBTrackers();
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            //now that we have latest * scale we can run calibration
            BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
            BasisLocalPlayer.Instance.DriveTpose();//update the avatars position.

            Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms = BasisLocalPlayer.Instance.LocalAvatarDriver.StoredRolesTransforms;
            List<BasisBoneTrackedRole> rolesToDiscover = GetAllRolesDesired();
            List<BasisBoneTrackedRole> trackInputRoles = new List<BasisBoneTrackedRole>(23);
            List<BasisCalibrationData> connectors = new List<BasisCalibrationData>(23);
            List<BasisTrackerMapping> boneTransformMappings = new List<BasisTrackerMapping>(23);
            List<BasisBoneTrackedRole> roles = new List<BasisBoneTrackedRole>(23);
            List<BasisInput> BasisInputs = new List<BasisInput>(23);

            int count = rolesToDiscover.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisBoneTrackedRole Role = rolesToDiscover[Index];
                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(Role))
                {
                    trackInputRoles.Add(Role);
                }
            }
            int AllInputDevicesCount = BasisDeviceManagement.Instance.AllInputDevices.Count;
            for (int Index = 0; Index < AllInputDevicesCount; Index++)
            {
                BasisInput baseInput = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (baseInput.TryGetRole(out BasisBoneTrackedRole role))
                {
                    if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                    {
                        //in use un assign first
                        baseInput.UnAssignFullBodyTrackers();
                        BasisCalibrationData calibrationConnector = new BasisCalibrationData
                        {
                            BasisInput = baseInput,
                            Distance = float.MaxValue
                        };
                        connectors.Add(calibrationConnector);
                    }
                }
                else//no assigned role
                {
                    BasisCalibrationData calibrationConnector = new BasisCalibrationData
                    {
                        BasisInput = baseInput,
                        Distance = float.MaxValue
                    };
                    //tracker was a uncalibrated type
                    connectors.Add(calibrationConnector);
                }
            }
            int Count = trackInputRoles.Count;
            Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms = BasisLocalPlayer.Instance.LocalAvatarDriver.StoredRolesTransforms;
            for (int Index = 0; Index < Count; Index++)
            {
                BasisBoneTrackedRole role = trackInputRoles[Index];
                if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl control, role))
                {
                    //0.3f * 1 
                    float ScaledDistance = MaxDistanceBeforeTrackerIsIrrelivant(role) * BasisHeightDriver.ScaledToMatchValue;
                    if (StoredRolesTransforms.TryGetValue(role, out Transform Transform))
                    {
                        //  BasisLocalPlayer.Instance.LocalBoneDriver.AddGizmo($"{control.name} IK Calibration with Scaler Distance {ScaledDistance}", Transform, ScaledDistance, control.Color, role);
                        BasisTrackerMapping mapping = new BasisTrackerMapping(control, Transform, role, connectors, ScaledDistance);
                        boneTransformMappings.Add(mapping);
                    }
                    else
                    {
                        BasisDebug.LogError($"Missing Mapping in Roles Transforms {role}");
                    }
                }
                else
                {
                    BasisDebug.LogError($"Missing bone control for role {role}");
                }
            }
            int cachedCount = boneTransformMappings.Count;
            // Find optimal matches
            for (int Index = 0; Index < cachedCount; Index++)
            {
                BasisTrackerMapping mapping = boneTransformMappings[Index];
                if (mapping.TargetControl != null)
                {
                    FindTrackersFromInputs(mapping, ref BasisInputs, ref roles);
                }
                else
                {
                    BasisDebug.LogError("Missing Tracker for index " + Index + " with ID " + mapping);
                }
            }


            // 8) IMPORTANT: simulate once AFTER assignments so the bone controls reflect new tracker bindings.
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            ComputeHints(storedRoleTransforms);

            BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
            BasisLocalPlayer.Instance.LocalRigDriver.RigLayer.active = true;
            BasisLocalPlayer.Instance.LocalAnimatorDriver.AssignHipsFBTracker();
        }
        /// <summary>
        /// Finds trackers from the basis input system.
        /// </summary>
        /// <param name="mapping"></param>
        /// <param name="BasisInputs"></param>
        /// <param name="roles"></param>
        public static void FindTrackersFromInputs(BasisTrackerMapping mapping, ref List<BasisInput> BasisInputs, ref List<BasisBoneTrackedRole> roles)
        {
            // List to store the calibration actions
            List<Action> calibrationActions = new List<Action>();

            int CandidateCount = mapping.Candidates.Count;
            for (int Index = 0; Index < CandidateCount; Index++)
            {
                BasisCalibrationData Connector = mapping.Candidates[Index];
                if (BasisInputs.Contains(Connector.BasisInput) == false)
                {
                    if (roles.Contains(mapping.BasisBoneControlRole) == false)
                    {
                        roles.Add(mapping.BasisBoneControlRole);
                        BasisInputs.Add(Connector.BasisInput);
                        // Store the calibration action instead of executing it directly
                        calibrationActions.Add(() =>
                        {

                            HasFBIKTrackers = true;
                            Connector.BasisInput.ApplyTrackerCalibration(mapping.BasisBoneControlRole);
                        });

                        // Once we found a valid connector, we can stop the search
                        break;
                    }
                    else
                    {
                        //BasisDebug.Log("we have already assigned role " + mapping.BasisBoneControlRole);
                    }
                }
                else
                {
                    //BasisDebug.Log("Already assigned " + Connector.Tracker);
                }
            }

            // Execute all stored calibration actions
            int Count = calibrationActions.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                Action action = calibrationActions[Index];
                action();
            }
        }
        /// <summary>
        /// gets a roles dictonary with the roles and transforms
        /// </summary>
        /// <returns></returns>
        public static Dictionary<BasisBoneTrackedRole, Transform> GetAllRolesAsTransform()
        {
            Common.BasisTransformMapping Mapping = BasisLocalAvatarDriver.Mapping;
            Dictionary<BasisBoneTrackedRole, Transform> transforms = new Dictionary<BasisBoneTrackedRole, Transform>
    {
        { BasisBoneTrackedRole.Hips,Mapping.Hips },
      //  { BasisBoneTrackedRole.Spine, Mapping.spine },
        { BasisBoneTrackedRole.Chest, Mapping.chest },
    //    { BasisBoneTrackedRole.Upperchest, BasisLocalPlayer.Instance.AvatarDriver.References.Upperchest },
      //  { BasisBoneTrackedRole.Neck, Mapping.neck },
        { BasisBoneTrackedRole.Head, Mapping.head },
       // { BasisBoneTrackedRole.CenterEye, LeftEye },
       // { BasisBoneTrackedRole.RightEye, RightEye },

        { BasisBoneTrackedRole.LeftShoulder, Mapping.leftShoulder },
        { BasisBoneTrackedRole.RightShoulder, Mapping.RightShoulder },

      // { BasisBoneTrackedRole.LeftUpperArm, Mapping.leftUpperArm },
      // { BasisBoneTrackedRole.RightUpperArm,Mapping. RightUpperArm },

        { BasisBoneTrackedRole.RightLowerArm, Mapping.RightLowerArm },
        { BasisBoneTrackedRole.LeftLowerArm, Mapping.leftLowerArm },

        { BasisBoneTrackedRole.LeftHand, Mapping.leftHand },
        { BasisBoneTrackedRole.RightHand, Mapping.rightHand },

      //  { BasisBoneTrackedRole.LeftUpperLeg,Mapping.LeftUpperLeg },
       { BasisBoneTrackedRole.LeftLowerLeg,Mapping. LeftLowerLeg },
      //  { BasisBoneTrackedRole.RightUpperLeg, Mapping.RightUpperLeg },
        { BasisBoneTrackedRole.RightLowerLeg,Mapping. RightLowerLeg },

        { BasisBoneTrackedRole.LeftFoot, Mapping.leftFoot },
        { BasisBoneTrackedRole.LeftToes,Mapping. leftToe },

        { BasisBoneTrackedRole.RightFoot, Mapping.rightFoot },
        { BasisBoneTrackedRole.RightToes,Mapping. rightToe },
            };

            return transforms;
        }
        /// <summary>
        ///  each roles radius before outside of attempt
        /// </summary>
        public static float MaxDistanceBeforeTrackerIsIrrelivant(BasisBoneTrackedRole role)
        {

            switch (role)
            {
                case BasisBoneTrackedRole.CenterEye:
                    return 0;

                case BasisBoneTrackedRole.Head:
                    return 0;

                case BasisBoneTrackedRole.Neck:
                    return 0;
                case BasisBoneTrackedRole.Mouth:
                    return 0;
                case BasisBoneTrackedRole.Spine:
                    return 0;
                case BasisBoneTrackedRole.Chest:
                    return 0.35f;
                case BasisBoneTrackedRole.Hips:
                    return 0.45f;

                case BasisBoneTrackedRole.LeftLowerLeg:
                    return 0.5f;
                case BasisBoneTrackedRole.RightLowerLeg:
                    return 0.5f;

                case BasisBoneTrackedRole.LeftFoot:
                    return 0.35f;
                case BasisBoneTrackedRole.RightFoot:
                    return 0.35f;

                case BasisBoneTrackedRole.LeftShoulder:
                    return 0.3f;
                case BasisBoneTrackedRole.RightShoulder:
                    return 0.3f;

                case BasisBoneTrackedRole.LeftUpperLeg:
                    return 0.3f;
                case BasisBoneTrackedRole.RightUpperLeg:
                    return 0.3f;

                case BasisBoneTrackedRole.LeftLowerArm:
                    return 0.4f;
                case BasisBoneTrackedRole.RightLowerArm:
                    return 0.4f;

                case BasisBoneTrackedRole.LeftHand:
                    return 0.2f;
                case BasisBoneTrackedRole.RightHand:
                    return 0.2f;

                case BasisBoneTrackedRole.LeftToes:
                    return 0.2f;
                case BasisBoneTrackedRole.RightToes:
                    return 0.2f;

                case BasisBoneTrackedRole.LeftUpperArm:
                    return 0;
                case BasisBoneTrackedRole.RightUpperArm:
                    return 0;
                default:
                    BasisDebug.LogError($"Unknown role {role}");
                    return 0;
            }
        }
        /// <summary>
        /// order we should build tracker pairs in
        /// </summary>
        public static BasisBoneTrackedRole[] desiredOrder = new BasisBoneTrackedRole[]
        {
        BasisBoneTrackedRole.Hips,
        BasisBoneTrackedRole.RightFoot,
        BasisBoneTrackedRole.LeftFoot,

        BasisBoneTrackedRole.LeftLowerLeg,
        BasisBoneTrackedRole.RightLowerLeg,
        BasisBoneTrackedRole.LeftLowerArm,
        BasisBoneTrackedRole.RightLowerArm,

    //    BasisBoneTrackedRole.CenterEye,
        BasisBoneTrackedRole.Chest,

       // BasisBoneTrackedRole.Head,
       // BasisBoneTrackedRole.Neck,

        BasisBoneTrackedRole.LeftHand,
        BasisBoneTrackedRole.RightHand,

        BasisBoneTrackedRole.LeftToes,
        BasisBoneTrackedRole.RightToes,

      //  BasisBoneTrackedRole.LeftUpperArm,
       // BasisBoneTrackedRole.RightUpperArm,
      //  BasisBoneTrackedRole.LeftUpperLeg,
       // BasisBoneTrackedRole.RightUpperLeg,
        BasisBoneTrackedRole.LeftShoulder,
        BasisBoneTrackedRole.RightShoulder,
        };
        public static void ComputeHints(Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms)
        {
            // 9) Bake "hint push up/out" offsets at calibration time
            //    We store offsets in tracker-local space so they rotate with the tracker at runtime.
            //    Then BasisLocalRigDriver applies: hintPos = rawPos + rawRot * localOffset;

            // Grab reference rotations from the avatar in T-pose (stable)
            Quaternion chestRefRot = Quaternion.identity;
            Quaternion hipsRefRot = Quaternion.identity;

            if (storedRoleTransforms.TryGetValue(BasisBoneTrackedRole.Chest, out var chestT) && chestT != null)
            {
                chestRefRot = chestT.rotation;
            }

            if (storedRoleTransforms.TryGetValue(BasisBoneTrackedRole.Hips, out var hipsT) && hipsT != null)
            {
                hipsRefRot = hipsT.rotation;
            }

            // Choose push magnitudes (tweakable)
            float hs = BasisHeightDriver.ScaledToMatchValue;

            float elbowPush = 0.12f * hs;
            float kneePush = 0.10f * hs;
            float headPush = 0.08f * hs;

            // Optional clamp so calibration can never store insane offsets
            float maxPush = 0.25f * hs;
            // Chest-as-head-hint bias (push "up" in chest frame)
            {
                var chestCtrl = BasisLocalBoneDriver.ChestControl;
                Quaternion trackerRot = chestCtrl.OutgoingWorldData.rotation;

                Vector3 worldUp = chestRefRot * Vector3.up;
                Vector3 localUp = Quaternion.Inverse(trackerRot) * worldUp;
                Vector3 localOffset = (localUp.sqrMagnitude < 1e-8f ? Vector3.up : localUp.normalized) * headPush;
                localOffset = Vector3.ClampMagnitude(localOffset, maxPush);

                BasisHintBiasStore.Set(BasisBoneTrackedRole.Chest, localOffset);
            }

            // Elbow hints (lower arms)
            {
                {
                    var lla = BasisLocalBoneDriver.LeftLowerArmControl;
                    Quaternion trackerRot = lla.OutgoingWorldData.rotation;
                    Vector3 localOffset = ComputeHintBiasLocal(trackerRot, chestRefRot, isLeft: true, distanceMeters: elbowPush, outWeight: 0.85f, upWeight: 0.35f, fwdWeight: 0.15f);
                    localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                    BasisHintBiasStore.Set(BasisBoneTrackedRole.LeftLowerArm, localOffset);
                }
                {
                    var rla = BasisLocalBoneDriver.RightLowerArmControl;
                    Quaternion trackerRot = rla.OutgoingWorldData.rotation;
                    Vector3 localOffset = ComputeHintBiasLocal(trackerRot, chestRefRot, isLeft: false, distanceMeters: elbowPush, outWeight: 0.85f, upWeight: 0.35f, fwdWeight: 0.15f);
                    localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                    BasisHintBiasStore.Set(BasisBoneTrackedRole.RightLowerArm, localOffset);
                }
            }
            // Knee hints (lower legs) — often better with a touch of forward
            {
                var lll = BasisLocalBoneDriver.LeftLowerLegControl;
                {
                    float fwdWeight = 1;
                    if (lll.HasTracked == BasisHasTracked.HasTracker)
                    {
                        fwdWeight = 0.55f;
                    }
                    Quaternion trackerRot = lll.OutgoingWorldData.rotation;
                    Vector3 localOffset = ComputeHintBiasLocal(trackerRot, hipsRefRot, isLeft: true, distanceMeters: kneePush, outWeight: 0, upWeight: 0.25f, fwdWeight);
                    localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                    BasisHintBiasStore.Set(BasisBoneTrackedRole.LeftLowerLeg, localOffset);
                }

                var rll = BasisLocalBoneDriver.RightLowerLegControl;
                {
                    float fwdWeight = 1;
                    if (rll.HasTracked == BasisHasTracked.HasTracker)
                    {
                         fwdWeight = 0.55f;
                    }
                    Quaternion trackerRot = rll.OutgoingWorldData.rotation;
                    Vector3 localOffset = ComputeHintBiasLocal(trackerRot, hipsRefRot, isLeft: false, distanceMeters: kneePush, outWeight: 0, upWeight: 0.25f, fwdWeight);
                    localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                    BasisHintBiasStore.Set(BasisBoneTrackedRole.RightLowerLeg, localOffset);
                }
            }
        }
        // Helper local function to compute a tracker-local offset vector that points "up and out"
        static Vector3 ComputeHintBiasLocal(
            Quaternion trackerWorldRot,
            Quaternion referenceWorldRot,   // chest for arms, hips for legs
            bool isLeft,
            float distanceMeters,           // already scaled
            float outWeight = 0.85f,
            float upWeight = 0.35f,
            float fwdWeight = 0.00f         // optional: add a bit of forward if you want knees/elbows forward
        )
        {
            Vector3 up = referenceWorldRot * Vector3.up;
            Vector3 outDir = referenceWorldRot * (isLeft ? Vector3.left : Vector3.right);
            Vector3 fwd = referenceWorldRot * Vector3.forward;

            Vector3 worldDir = (outDir * outWeight + up * upWeight + fwd * fwdWeight);
            if (worldDir.sqrMagnitude < 1e-8f) worldDir = up;
            worldDir.Normalize();

            // Convert desired world push into tracker-local direction
            Vector3 localDir = Quaternion.Inverse(trackerWorldRot) * worldDir;
            if (localDir.sqrMagnitude < 1e-8f) localDir = Vector3.up;

            return localDir.normalized * distanceMeters;
        }
        /// <summary>
        /// data for ik calibration
        /// </summary>
        public class BasisCalibrationData
        {
            [SerializeField]
            public BasisInput BasisInput;
            public float Distance;
        }
    }
}
