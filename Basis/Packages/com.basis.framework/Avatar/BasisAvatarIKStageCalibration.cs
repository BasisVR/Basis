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
            public static void Clear() => LocalOffset.Clear();
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
            BasisHintBiasStore.Clear();
            BasisDeviceManagement.UnassignFBTrackers();
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            //now that we have latest * scale we can run calibration
            BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
            BasisLocalPlayer.Instance.DriveTpose();//update the avatars position.

            Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms = BasisLocalPlayer.Instance.LocalAvatarDriver.StoredRolesTransforms;
            List<BasisBoneTrackedRole> rolesToDiscover = GetAllRolesDesired();
            List<BasisBoneTrackedRole> trackInputRoles = new List<BasisBoneTrackedRole>(23);
            List<BasisCalibrationData> connectors = new List<BasisCalibrationData>(23);

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
            // Stamp each connector with a left/right side based on its world position
            // relative to the hips. The nearest-pair matching loop below uses this to
            // prevent a tracker on one side of the body from being assigned to a role
            // on the other.
            {
                Transform hipsForSide;
                if (storedRoleTransforms.TryGetValue(BasisBoneTrackedRole.Hips, out Transform hipsT) && hipsT != null)
                    hipsForSide = hipsT;
                else
                    hipsForSide = BasisLocalPlayer.Instance.transform;

                float sideDeadZoneMeters = 0.03f * BasisHeightDriver.ScaledToMatchValue;
                Vector3 hipsWorldPos = hipsForSide.position;
                Vector3 hipsWorldRight = hipsForSide.right;
                int connectorsCount = connectors.Count;
                for (int cIdx = 0; cIdx < connectorsCount; cIdx++)
                {
                    BasisCalibrationData conn = connectors[cIdx];
                    if (conn.BasisInput == null) { conn.SideSign = 0; continue; }
                    Vector3 fromHips = conn.BasisInput.transform.position - hipsWorldPos;
                    float sd = Vector3.Dot(fromHips, hipsWorldRight);
                    if (sd > sideDeadZoneMeters) conn.SideSign = 1;
                    else if (sd < -sideDeadZoneMeters) conn.SideSign = -1;
                    else conn.SideSign = 0;
                }
            }

            // Build target list: one entry per trackable role that has a valid bone
            // control, a positive max-distance, and a known avatar T-pose transform.
            int trCount = trackInputRoles.Count;
            List<(BasisBoneTrackedRole role, Vector3 targetPos, float maxDistance)> targets =
                new List<(BasisBoneTrackedRole, Vector3, float)>(trCount);
            for (int Index = 0; Index < trCount; Index++)
            {
                BasisBoneTrackedRole role = trackInputRoles[Index];
                if (!BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl control, role))
                {
                    BasisDebug.LogError($"Missing bone control for role {role}");
                    continue;
                }
                float maxDistance = MaxDistanceBeforeTrackerIsIrrelivant(role) * SMModuleCalibration.GetSphereScale(role) * BasisHeightDriver.ScaledToMatchValue;
                if (maxDistance <= 0f)
                {
                    continue; // role does not participate in distance matching
                }
                Vector3 targetPos;
                if (storedRoleTransforms.TryGetValue(role, out Transform roleT) && roleT != null)
                {
                    targetPos = roleT.position;
                }
                else
                {
                    BasisDebug.LogError($"Missing Mapping in Roles Transforms {role}");
                    targetPos = control.OutgoingWorldData.position;
                }
                targets.Add((role, targetPos, maxDistance));
            }

            // Cache tracker world positions once so successive loop iterations don't
            // re-read them. ApplyTrackerCalibration doesn't move the tracker so this
            // stays valid for the whole matching pass.
            int connectorsCached = connectors.Count;
            Vector3[] connectorPositions = new Vector3[connectorsCached];
            for (int cIdx = 0; cIdx < connectorsCached; cIdx++)
            {
                BasisCalibrationData conn = connectors[cIdx];
                connectorPositions[cIdx] = conn.BasisInput != null ? conn.BasisInput.transform.position : Vector3.zero;
            }

            // Global nearest-pair assignment: each iteration picks the smallest-distance
            // (target, tracker) pair that still respects the target's radius cap AND the
            // side filter. Repeat until nothing is left to match. This replaces the old
            // order-sensitive greedy pass — a tracker closer to role A than to role B
            // always wins A, regardless of which role appears first in desiredOrder.
            bool[] targetMatched = new bool[targets.Count];
            bool[] connectorUsed = new bool[connectorsCached];
            while (true)
            {
                float bestDist = float.MaxValue;
                int bestTarget = -1;
                int bestConnector = -1;
                for (int tIdx = 0; tIdx < targets.Count; tIdx++)
                {
                    if (targetMatched[tIdx]) continue;
                    var t = targets[tIdx];
                    int requiredSide = t.role.SideSign();
                    for (int cIdx = 0; cIdx < connectorsCached; cIdx++)
                    {
                        if (connectorUsed[cIdx]) continue;
                        BasisCalibrationData conn = connectors[cIdx];
                        if (conn.BasisInput == null) continue;
                        if (requiredSide != 0 && conn.SideSign != 0 && conn.SideSign != requiredSide) continue;
                        float d = Vector3.Distance(t.targetPos, connectorPositions[cIdx]);
                        if (d > t.maxDistance) continue;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestTarget = tIdx;
                            bestConnector = cIdx;
                        }
                    }
                }
                if (bestTarget < 0) break;
                targetMatched[bestTarget] = true;
                connectorUsed[bestConnector] = true;
                HasFBIKTrackers = true;
                connectors[bestConnector].BasisInput.ApplyTrackerCalibration(targets[bestTarget].role);
            }


            // 8) IMPORTANT: simulate once AFTER assignments so the bone controls reflect new tracker bindings.
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            ComputeHints(storedRoleTransforms);

            BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
            BasisLocalPlayer.Instance.LocalRigDriver.RigLayer.active = true;
            BasisLocalPlayer.Instance.LocalAnimatorDriver.AssignHipsFBTracker();
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
                // Radii are generous upper bounds — the matcher picks the GLOBALLY
                // closest tracker-role pair each step, so bumping these does not
                // cause cross-role confusion. Side filtering guards left/right.
                case BasisBoneTrackedRole.Chest:
                    return 0.4f;
                case BasisBoneTrackedRole.Hips:
                    return 0.5f;

                case BasisBoneTrackedRole.LeftLowerLeg:
                    return 0.55f;
                case BasisBoneTrackedRole.RightLowerLeg:
                    return 0.55f;

                case BasisBoneTrackedRole.LeftFoot:
                    return 0.4f;
                case BasisBoneTrackedRole.RightFoot:
                    return 0.4f;

                case BasisBoneTrackedRole.LeftShoulder:
                    return 0.45f;
                case BasisBoneTrackedRole.RightShoulder:
                    return 0.45f;

                case BasisBoneTrackedRole.LeftUpperLeg:
                    return 0.3f;
                case BasisBoneTrackedRole.RightUpperLeg:
                    return 0.3f;

                case BasisBoneTrackedRole.LeftLowerArm:
                    return 0.55f;
                case BasisBoneTrackedRole.RightLowerArm:
                    return 0.55f;

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
        // Tightest-radius / most-specific roles first. Center roles (Hips, Chest)
        // are last so side-specific bones get first pick of their candidates —
        // without this, Chest's 0.35m radius can steal a shoulder tracker.
        public static BasisBoneTrackedRole[] desiredOrder = new BasisBoneTrackedRole[]
        {
        BasisBoneTrackedRole.LeftHand,
        BasisBoneTrackedRole.RightHand,

        BasisBoneTrackedRole.LeftToes,
        BasisBoneTrackedRole.RightToes,

        BasisBoneTrackedRole.LeftShoulder,
        BasisBoneTrackedRole.RightShoulder,

        BasisBoneTrackedRole.RightFoot,
        BasisBoneTrackedRole.LeftFoot,

        BasisBoneTrackedRole.LeftLowerArm,
        BasisBoneTrackedRole.RightLowerArm,

        BasisBoneTrackedRole.LeftLowerLeg,
        BasisBoneTrackedRole.RightLowerLeg,

        BasisBoneTrackedRole.Hips,
        BasisBoneTrackedRole.Chest,
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
            public int SideSign; // -1 left, +1 right, 0 center/unknown
        }
    }
}
