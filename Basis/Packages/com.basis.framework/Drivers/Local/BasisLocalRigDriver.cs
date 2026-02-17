using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
using static BasisHeightDriver;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local rig driver that wires up Unity Animation Rigging constraints for a player avatar,
    /// filters tracker noise (One Euro Filter), and manually evaluates the rig graph each frame.
    /// Sets up spine, head, hands, feet, and toes, and toggles layers based on available rigs.
    /// </summary>
    [Serializable]
    public class BasisLocalRigDriver
    {
        /// <summary>
        /// Lower = more smoothing; Higher = more responsive. (0.01f, 10f)
        /// </summary>
        public static float MinCutoff = 5.5f;

        /// <summary>
        /// How much to raise cutoff when motion is fast (reduces lag during quick moves). (0f, 10f)
        /// </summary>
        public static float Beta = 3.25f;

        /// <summary>
        /// Cutoff for derivative smoothing. (0.01f, 10f)
        /// </summary>
        public static float DerivativeCutoff = 3f;

        public RigBuilder Builder;
        public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
        public PlayableGraph PlayableGraph;
        public Rig MainRig;
        public RigLayer RigLayer;
        public BasisFullBodyIK BasisFullIKConstraint;

        private BasisLocalPlayer localPlayer;
        private BasisTransformMapping basisTransformMapping;

        private static readonly OneEuroFilterQuaternion fRotHips = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotHead = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftFoot = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightFoot = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotChest = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftLowerLeg = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightLowerLeg = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftHand = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightHand = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftLowerArm = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightLowerArm = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftToe = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightToe = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotLeftShoulder = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterQuaternion fRotRightShoulder = new OneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);

        private static readonly OneEuroFilterVector3 fPosHips = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosHead = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftFoot = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightFoot = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosChest = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftLowerLeg = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightLowerLeg = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftHand = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightHand = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftLowerArm = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightLowerArm = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftToe = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightToe = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);

        // Keep this order stable forever.
        // These indices drive your toggle arrays AND which filter instance is used.
        public const int S_Hips = 0;
        public const int S_Head = 1;
        public const int S_LeftFoot = 2;
        public const int S_RightFoot = 3;
        public const int S_Chest = 4;
        public const int S_LeftLowerLeg = 5;
        public const int S_RightLowerLeg = 6;
        public const int S_LeftHand = 7;
        public const int S_RightHand = 8;
        public const int S_LeftLowerArm = 9;
        public const int S_RightLowerArm = 10;
        public const int S_LeftToe = 11;
        public const int S_RightToe = 12;
        public const int S_LeftShoulder = 13;
        public const int S_RightShoulder = 14;

        public const int SlotCount = 15;

        // Smoothing enable toggles (position + rotation)
        public static bool[] SmoothPos = new bool[SlotCount];
        public static bool[] SmoothRot = new bool[SlotCount];

        // One Euro enable toggles (position + rotation)
        public static bool[] EuroPos = new bool[SlotCount];
        public static bool[] EuroRot = new bool[SlotCount];

        // Fallback smoothing when smoothing is ON but Euro is OFF
        [Range(0.01f, 60f)] public static float PositionSmoothingHz = 20f;
        [Range(0.01f, 60f)] public static float RotationSmoothingHz = 25f;
        [Serializable]
        public class OneEuroFilterQuaternion
        {
            public float minCutoff;
            public float beta;
            public float dCutoff;

            private bool hasPrev;
            private Quaternion prev;
            private readonly OneEuroFilterVector3 vecFilter;

            public OneEuroFilterQuaternion(float minCutoff, float beta, float dCutoff)
            {
                this.minCutoff = minCutoff;
                this.beta = beta;
                this.dCutoff = dCutoff;
                vecFilter = new OneEuroFilterVector3(minCutoff, beta, dCutoff);
            }

            public void Reset() => hasPrev = false;

            public Quaternion Filter(Quaternion q, double t)
            {
                if (!hasPrev)
                {
                    hasPrev = true;
                    prev = q;
                    return q;
                }

                vecFilter.minCutoff = minCutoff;
                vecFilter.beta = beta;
                vecFilter.dCutoff = dCutoff;

                // shortest path
                if (Quaternion.Dot(prev, q) < 0f)
                    q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

                Quaternion delta = q * Quaternion.Inverse(prev);

                delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (angleDeg > 180f) angleDeg -= 360f;

                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector3 logVec = (axis.sqrMagnitude < 1e-12f) ? Vector3.zero : axis.normalized * angleRad;

                Vector3 filteredLog = vecFilter.Filter(logVec, t);

                float mag = filteredLog.magnitude;
                Quaternion filteredDelta =
                    (mag < 1e-12f) ? Quaternion.identity : Quaternion.AngleAxis(mag * Mathf.Rad2Deg, filteredLog / mag);

                Quaternion outQ = filteredDelta * prev;
                prev = outQ;
                return outQ;
            }
        }

        public double timeAccumulator;

        public static Vector3 sPosHips, sPosHead, sPosLeftFoot, sPosRightFoot, sPosChest, sPosLeftLowerLeg, sPosRightLowerLeg;
        public static Vector3 sPosLeftHand, sPosRightHand, sPosLeftLowerArm, sPosRightLowerArm, sPosLeftToe, sPosRightToe;

        public static Quaternion sRotHips, sRotHead, sRotLeftFoot, sRotRightFoot, sRotChest, sRotLeftLowerLeg, sRotRightLowerLeg;
        public static Quaternion sRotLeftHand, sRotRightHand, sRotLeftLowerArm, sRotRightLowerArm, sRotLeftToe, sRotRightToe;
        public static Quaternion sRotLeftShoulder, sRotRightShoulder;

        public static bool hasFallbackState;
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            basisTransformMapping = references;
            timeAccumulator = 0f;
        }
        public void BuildBuilder()
        {
            if (localPlayer?.BasisAvatar?.Animator == null || Builder == null)
            {
                return;
            }

            PlayableGraph = localPlayer.BasisAvatar.Animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Builder.Build(PlayableGraph);

            ResetSmoothingState();
        }

        public void SetBodySettings()
        {
            var rigGO = CreateOrGetRig("Main IK", true, out MainRig, out RigLayer);
            Spine(rigGO);
            BasisLocalBoneControl.HasEvents = true;
        }

        public void CleanupBeforeContinue()
        {
            if (MainRig == null)
            {
                return;
            }

            GameObject.Destroy(MainRig.gameObject);
            MainRig = null;
            RigLayer = default;
        }
        public void OnTPose() => OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);

        public void OnTPose(bool currentlyTposing)
        {
            if (Builder == null)
            {
                BasisDebug.LogWarning($"{nameof(BasisLocalRigDriver)}: Trying to T-pose while Builder is null!");
                return;
            }

            // While in T-pose, disable all rig layers
            if (currentlyTposing)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer != null)
                    {
                        layer.active = false;
                    }
                }

                return;
            }

            // Notify controls when exiting T-pose
            var driver = BasisLocalPlayer.Instance?.LocalBoneDriver;
            if (driver?.Controls == null)
            {
                return;
            }

            foreach (var control in driver.Controls)
            {
                control?.OnHasRigChanged?.Invoke(true);//true means that the ik detaches
            }
        }
        public void ResetSmoothingState()
        {
            timeAccumulator = 0;
            hasFallbackState = false;

            // Reset Euro filters (rotation)
            fRotHips.Reset();
            fRotHead.Reset();
            fRotLeftFoot.Reset();
            fRotRightFoot.Reset();
            fRotChest.Reset();
            fRotLeftLowerLeg.Reset();
            fRotRightLowerLeg.Reset();
            fRotLeftHand.Reset();
            fRotRightHand.Reset();
            fRotLeftLowerArm.Reset();
            fRotRightLowerArm.Reset();
            fRotLeftToe.Reset();
            fRotRightToe.Reset();
            fRotLeftShoulder.Reset();
            fRotRightShoulder.Reset();

            
        }
        public void SimulateIKDestinations(float deltaTime)
        {
            if (BasisFullIKConstraint == null || Builder == null)
            {
                return;
            }

            if (!PlayableGraph.IsValid())
            {
                return;
            }

            timeAccumulator += Mathf.Max(deltaTime, 1e-6f);

            BasisFullBodyData data = BasisFullIKConstraint.data;

            // Init fallback state once (so first frame doesn't lerp from zero)
            if (!hasFallbackState)
            {
                hasFallbackState = true;

                sPosHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.position;
                sRotHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;

                sPosHead = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.position;
                sRotHead = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation;

                sPosLeftFoot = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.position;
                sRotLeftFoot = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation;

                sPosRightFoot = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.position;
                sRotRightFoot = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation;

                sPosChest = BasisLocalBoneDriver.ChestControl.OutgoingWorldData.position;
                sRotChest = BasisLocalBoneDriver.ChestControl.OutgoingWorldData.rotation;

                sPosLeftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.position;
                sRotLeftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation;

                sPosRightLowerLeg = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.position;
                sRotRightLowerLeg = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation;

                sPosLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.position;
                sRotLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.rotation;

                sPosRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.position;
                sRotRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.rotation;

                sPosLeftLowerArm = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.position;
                sRotLeftLowerArm = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.rotation;

                sPosRightLowerArm = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.position;
                sRotRightLowerArm = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.rotation;

                sPosLeftToe = BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData.position;
                sRotLeftToe = BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData.rotation;

                sPosRightToe = BasisLocalBoneDriver.RightToeControl.OutgoingWorldData.position;
                sRotRightToe = BasisLocalBoneDriver.RightToeControl.OutgoingWorldData.rotation;

                sRotLeftShoulder = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData.rotation;
                sRotRightShoulder = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData.rotation;
            }

            // ---------------- HIPS ----------------
            var hips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

            Vector3 hipsPos = hips.position;
            if (SmoothPos[S_Hips])
                hipsPos = EuroPos[S_Hips] ? fPosHips.Filter(hipsPos, timeAccumulator) : FallbackPos(ref sPosHips, hipsPos, deltaTime);

            Quaternion hipsRot = hips.rotation;
            if (SmoothRot[S_Hips])
                hipsRot = EuroRot[S_Hips] ? fRotHips.Filter(hipsRot, timeAccumulator) : FallbackRot(ref sRotHips, hipsRot, deltaTime);

            data.PositionHips = hipsPos;
            data.RotationHips = hipsRot;

            // ---------------- HEAD ----------------
            var head = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;

            Vector3 headPos = head.position;
            if (SmoothPos[S_Head])
                headPos = EuroPos[S_Head] ? fPosHead.Filter(headPos, timeAccumulator) : FallbackPos(ref sPosHead, headPos, deltaTime);

            Quaternion headRot = head.rotation;
            if (SmoothRot[S_Head])
                headRot = EuroRot[S_Head] ? fRotHead.Filter(headRot, timeAccumulator) : FallbackRot(ref sRotHead, headRot, deltaTime);

            data.PositionHead = headPos;
            data.RotationHead = headRot;

            // ---------------- LEFT FOOT ----------------
            var lf = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;

            Vector3 lfPos = lf.position;
            if (SmoothPos[S_LeftFoot])
                lfPos = EuroPos[S_LeftFoot] ? fPosLeftFoot.Filter(lfPos, timeAccumulator) : FallbackPos(ref sPosLeftFoot, lfPos, deltaTime);

            Quaternion lfRot = lf.rotation;
            if (SmoothRot[S_LeftFoot])
                lfRot = EuroRot[S_LeftFoot] ? fRotLeftFoot.Filter(lfRot, timeAccumulator) : FallbackRot(ref sRotLeftFoot, lfRot, deltaTime);

            data.LeftFootPosition = lfPos;
            data.LeftFootRotation = lfRot;

            // ---------------- RIGHT FOOT ----------------
            var rf = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;

            Vector3 rfPos = rf.position;
            if (SmoothPos[S_RightFoot])
                rfPos = EuroPos[S_RightFoot] ? fPosRightFoot.Filter(rfPos, timeAccumulator) : FallbackPos(ref sPosRightFoot, rfPos, deltaTime);

            Quaternion rfRot = rf.rotation;
            if (SmoothRot[S_RightFoot])
                rfRot = EuroRot[S_RightFoot] ? fRotRightFoot.Filter(rfRot, timeAccumulator) : FallbackRot(ref sRotRightFoot, rfRot, deltaTime);

            data.RightFootPosition = rfPos;
            data.RightFootRotation = rfRot;

            // ---------------- CHEST (head hint) ----------------
            var chest = BasisLocalBoneDriver.ChestControl.OutgoingWorldData;

            Vector3 chestPos = chest.position;
            if (SmoothPos[S_Chest])
                chestPos = EuroPos[S_Chest] ? fPosChest.Filter(chestPos, timeAccumulator) : FallbackPos(ref sPosChest, chestPos, deltaTime);

            Quaternion chestRot = chest.rotation;
            if (SmoothRot[S_Chest])
                chestRot = EuroRot[S_Chest] ? fRotChest.Filter(chestRot, timeAccumulator) : FallbackRot(ref sRotChest, chestRot, deltaTime);

            // Apply "up" hint bias for head hint (Chest role is the hint driver)
            chestPos = ApplyHintBias(BasisBoneTrackedRole.Chest, chestPos, chestRot);

            data.ChestPosition = chestPos;
            data.ChestRotation = chestRot;

            // ---------------- LEFT LOWER LEG (hint) ----------------
            var lll = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;

            Vector3 lllPos = lll.position;
            if (SmoothPos[S_LeftLowerLeg])
                lllPos = EuroPos[S_LeftLowerLeg] ? fPosLeftLowerLeg.Filter(lllPos, timeAccumulator) : FallbackPos(ref sPosLeftLowerLeg, lllPos, deltaTime);

            Quaternion lllRot = lll.rotation;
            if (SmoothRot[S_LeftLowerLeg])
                lllRot = EuroRot[S_LeftLowerLeg] ? fRotLeftLowerLeg.Filter(lllRot, timeAccumulator) : FallbackRot(ref sRotLeftLowerLeg, lllRot, deltaTime);

            // Apply knee "up/out/forward" bias
            lllPos = ApplyHintBias(BasisBoneTrackedRole.LeftLowerLeg, lllPos, lllRot);

            data.PositionLeftLowerLeg = lllPos;
            data.RotationLeftLowerLeg = lllRot;

            // ---------------- RIGHT LOWER LEG (your code writes into RightFoot hint fields) ----------------
            var rll = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;

            Vector3 rllPos = rll.position;
            if (SmoothPos[S_RightLowerLeg])
                rllPos = EuroPos[S_RightLowerLeg] ? fPosRightLowerLeg.Filter(rllPos, timeAccumulator) : FallbackPos(ref sPosRightLowerLeg, rllPos, deltaTime);

            Quaternion rllRot = rll.rotation;
            if (SmoothRot[S_RightLowerLeg])
                rllRot = EuroRot[S_RightLowerLeg] ? fRotRightLowerLeg.Filter(rllRot, timeAccumulator) : FallbackRot(ref sRotRightLowerLeg, rllRot, deltaTime);

            // Apply knee "up/out/forward" bias
            rllPos = ApplyHintBias(BasisBoneTrackedRole.RightLowerLeg, rllPos, rllRot);

            data.PositionRightLowerLeg = rllPos;
            data.RotationRightLowerLeg = rllRot;

            // ---------------- LEFT HAND ----------------
            var lh = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;

            Vector3 lhPos = lh.position;
            if (SmoothPos[S_LeftHand])
                lhPos = EuroPos[S_LeftHand] ? fPosLeftHand.Filter(lhPos, timeAccumulator) : FallbackPos(ref sPosLeftHand, lhPos, deltaTime);

            Quaternion lhRot = lh.rotation;
            if (SmoothRot[S_LeftHand])
                lhRot = EuroRot[S_LeftHand] ? fRotLeftHand.Filter(lhRot, timeAccumulator) : FallbackRot(ref sRotLeftHand, lhRot, deltaTime);

            data.PositionLeftHand = lhPos;
            data.RotationLeftHand = lhRot;

            // ---------------- RIGHT HAND ----------------
            var rh = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;

            Vector3 rhPos = rh.position;
            if (SmoothPos[S_RightHand])
                rhPos = EuroPos[S_RightHand] ? fPosRightHand.Filter(rhPos, timeAccumulator) : FallbackPos(ref sPosRightHand, rhPos, deltaTime);

            Quaternion rhRot = rh.rotation;
            if (SmoothRot[S_RightHand])
                rhRot = EuroRot[S_RightHand] ? fRotRightHand.Filter(rhRot, timeAccumulator) : FallbackRot(ref sRotRightHand, rhRot, deltaTime);

            data.PositionRightHand = rhPos;
            data.RotationRightHand = rhRot;

            // ---------------- LEFT LOWER ARM (hand hint) ----------------
            var lla = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;

            Vector3 llaPos = lla.position;
            if (SmoothPos[S_LeftLowerArm])
                llaPos = EuroPos[S_LeftLowerArm] ? fPosLeftLowerArm.Filter(llaPos, timeAccumulator) : FallbackPos(ref sPosLeftLowerArm, llaPos, deltaTime);

            Quaternion llaRot = lla.rotation;
            if (SmoothRot[S_LeftLowerArm])
                llaRot = EuroRot[S_LeftLowerArm] ? fRotLeftLowerArm.Filter(llaRot, timeAccumulator) : FallbackRot(ref sRotLeftLowerArm, llaRot, deltaTime);

            // Apply elbow "up/out" bias
            llaPos = ApplyHintBias(BasisBoneTrackedRole.LeftLowerArm, llaPos, llaRot);

            // NOTE: keeping your original field mapping exactly
            data.LeftLowerArmPosition = llaPos;
            data.LeftLowerArmRotation = llaRot;

            // ---------------- RIGHT LOWER ARM (hand hint) ----------------
            var rla = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;

            Vector3 rlaPos = rla.position;
            if (SmoothPos[S_RightLowerArm])
                rlaPos = EuroPos[S_RightLowerArm] ? fPosRightLowerArm.Filter(rlaPos, timeAccumulator) : FallbackPos(ref sPosRightLowerArm, rlaPos, deltaTime);

            Quaternion rlaRot = rla.rotation;
            if (SmoothRot[S_RightLowerArm])
                rlaRot = EuroRot[S_RightLowerArm] ? fRotRightLowerArm.Filter(rlaRot, timeAccumulator) : FallbackRot(ref sRotRightLowerArm, rlaRot, deltaTime);

            // Apply elbow "up/out" bias
            rlaPos = ApplyHintBias(BasisBoneTrackedRole.RightLowerArm, rlaPos, rlaRot);

            data.RightLowerArmPosition = rlaPos;
            data.RightLowerArmRotation = rlaRot;

            // ---------------- TOES ----------------
            var lt = BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;

            Vector3 ltPos = lt.position;
            if (SmoothPos[S_LeftToe])
                ltPos = EuroPos[S_LeftToe] ? fPosLeftToe.Filter(ltPos, timeAccumulator) : FallbackPos(ref sPosLeftToe, ltPos, deltaTime);

            Quaternion ltRot = lt.rotation;
            if (SmoothRot[S_LeftToe])
                ltRot = EuroRot[S_LeftToe] ? fRotLeftToe.Filter(ltRot, timeAccumulator) : FallbackRot(ref sRotLeftToe, ltRot, deltaTime);

            data.OutGoingLeftToePosition = ltPos;
            data.OutGoingLeftToeRotation = ltRot;

            var rt = BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;

            Vector3 rtPos = rt.position;
            if (SmoothPos[S_RightToe])
                rtPos = EuroPos[S_RightToe] ? fPosRightToe.Filter(rtPos, timeAccumulator) : FallbackPos(ref sPosRightToe, rtPos, deltaTime);

            Quaternion rtRot = rt.rotation;
            if (SmoothRot[S_RightToe])
                rtRot = EuroRot[S_RightToe] ? fRotRightToe.Filter(rtRot, timeAccumulator) : FallbackRot(ref sRotRightToe, rtRot, deltaTime);

            data.OutGoingRightToePosition = rtPos;
            data.OutGoingRightToeRotation = rtRot;

            // ---------------- SHOULDERS (rotation only in your data) ----------------
            var ls = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData.rotation;
            if (SmoothRot[S_LeftShoulder])
                ls = EuroRot[S_LeftShoulder] ? fRotLeftShoulder.Filter(ls, timeAccumulator) : FallbackRot(ref sRotLeftShoulder, ls, deltaTime);

            data.LeftShoulderRotation = ls;

            var rs = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData.rotation;
            if (SmoothRot[S_RightShoulder])
                rs = EuroRot[S_RightShoulder] ? fRotRightShoulder.Filter(rs, timeAccumulator) : FallbackRot(ref sRotRightShoulder, rs, deltaTime);

            data.RightShoulderRotation = rs;

            Vector3 fwdC = chestRot * Vector3.forward;
            Vector3 outC = chestRot * Vector3.right;
            Vector3 upC = chestRot * Vector3.up;

            data.ElbowBendPrefLeft =
                (fwdC * elbowBendPrefLeftWeights.x +
                 outC * elbowBendPrefLeftWeights.y +
                 upC * elbowBendPrefLeftWeights.z).normalized;

            data.ElbowBendPrefRight =
                (fwdC * elbowBendPrefRightWeights.x +
                 outC * elbowBendPrefRightWeights.y +
                 upC * elbowBendPrefRightWeights.z).normalized;

            Vector3 fwd = hipsRot * Vector3.forward;
            Vector3 outR = hipsRot * Vector3.right;
            Vector3 up = hipsRot * Vector3.up;

            data.KneeBendPrefLeft = (hipsRot * Vector3.right);
            data.KneeBendPrefRight = (hipsRot * Vector3.right);

            data.SpineBendNormal = (fwd * spineBendNormalWeights.x + outR * spineBendNormalWeights.y + up * spineBendNormalWeights.z).normalized;
            // Commit & evaluate
            BasisFullIKConstraint.data = data;

            Builder.SyncLayers();
            PlayableGraph.Evaluate(deltaTime);
        }
        [SerializeField] private Vector3 elbowBendPrefLeftWeights = new Vector3(0, 1, 0);
        [SerializeField] private Vector3 elbowBendPrefRightWeights = new Vector3(0, 1, 0);
        [SerializeField] private Vector3 spineBendNormalWeights = new Vector3(1f, 0f, 0f);
        public static Vector3 ApplyHintBias(BasisBoneTrackedRole hintRole, Vector3 rawPos, Quaternion rawRot)
        {
            if (BasisHintBiasStore.TryGet(hintRole, out var localOffset))
            {
                return rawPos + rawRot * localOffset;
            }

            return rawPos;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ExpAlpha(float hz, float dt)
        {
            return 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(0.0001f, hz) * Mathf.Max(0.000001f, dt));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 FallbackPos(ref Vector3 state, Vector3 raw, float dt)
        {
            float a = ExpAlpha(PositionSmoothingHz, dt);
            state = Vector3.LerpUnclamped(state, raw, a);
            return state;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Quaternion FallbackRot(ref Quaternion state, Quaternion raw, float dt)
        {
            float a = ExpAlpha(RotationSmoothingHz, dt);
            state = Quaternion.Slerp(state, raw, a);
            return state;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateEuroSettings()
        {
            // Position filters
            fPosHips.minCutoff = MinCutoff; fPosHips.beta = Beta; fPosHips.dCutoff = DerivativeCutoff;
            fPosHead.minCutoff = MinCutoff; fPosHead.beta = Beta; fPosHead.dCutoff = DerivativeCutoff;
            fPosLeftFoot.minCutoff = MinCutoff; fPosLeftFoot.beta = Beta; fPosLeftFoot.dCutoff = DerivativeCutoff;
            fPosRightFoot.minCutoff = MinCutoff; fPosRightFoot.beta = Beta; fPosRightFoot.dCutoff = DerivativeCutoff;
            fPosChest.minCutoff = MinCutoff; fPosChest.beta = Beta; fPosChest.dCutoff = DerivativeCutoff;
            fPosLeftLowerLeg.minCutoff = MinCutoff; fPosLeftLowerLeg.beta = Beta; fPosLeftLowerLeg.dCutoff = DerivativeCutoff;
            fPosRightLowerLeg.minCutoff = MinCutoff; fPosRightLowerLeg.beta = Beta; fPosRightLowerLeg.dCutoff = DerivativeCutoff;
            fPosLeftHand.minCutoff = MinCutoff; fPosLeftHand.beta = Beta; fPosLeftHand.dCutoff = DerivativeCutoff;
            fPosRightHand.minCutoff = MinCutoff; fPosRightHand.beta = Beta; fPosRightHand.dCutoff = DerivativeCutoff;
            fPosLeftLowerArm.minCutoff = MinCutoff; fPosLeftLowerArm.beta = Beta; fPosLeftLowerArm.dCutoff = DerivativeCutoff;
            fPosRightLowerArm.minCutoff = MinCutoff; fPosRightLowerArm.beta = Beta; fPosRightLowerArm.dCutoff = DerivativeCutoff;
            fPosLeftToe.minCutoff = MinCutoff; fPosLeftToe.beta = Beta; fPosLeftToe.dCutoff = DerivativeCutoff;
            fPosRightToe.minCutoff = MinCutoff; fPosRightToe.beta = Beta; fPosRightToe.dCutoff = DerivativeCutoff;

            // Rotation filters
            fRotHips.minCutoff = MinCutoff; fRotHips.beta = Beta; fRotHips.dCutoff = DerivativeCutoff;
            fRotHead.minCutoff = MinCutoff; fRotHead.beta = Beta; fRotHead.dCutoff = DerivativeCutoff;
            fRotLeftFoot.minCutoff = MinCutoff; fRotLeftFoot.beta = Beta; fRotLeftFoot.dCutoff = DerivativeCutoff;
            fRotRightFoot.minCutoff = MinCutoff; fRotRightFoot.beta = Beta; fRotRightFoot.dCutoff = DerivativeCutoff;
            fRotChest.minCutoff = MinCutoff; fRotChest.beta = Beta; fRotChest.dCutoff = DerivativeCutoff;
            fRotLeftLowerLeg.minCutoff = MinCutoff; fRotLeftLowerLeg.beta = Beta; fRotLeftLowerLeg.dCutoff = DerivativeCutoff;
            fRotRightLowerLeg.minCutoff = MinCutoff; fRotRightLowerLeg.beta = Beta; fRotRightLowerLeg.dCutoff = DerivativeCutoff;
            fRotLeftHand.minCutoff = MinCutoff; fRotLeftHand.beta = Beta; fRotLeftHand.dCutoff = DerivativeCutoff;
            fRotRightHand.minCutoff = MinCutoff; fRotRightHand.beta = Beta; fRotRightHand.dCutoff = DerivativeCutoff;
            fRotLeftLowerArm.minCutoff = MinCutoff; fRotLeftLowerArm.beta = Beta; fRotLeftLowerArm.dCutoff = DerivativeCutoff;
            fRotRightLowerArm.minCutoff = MinCutoff; fRotRightLowerArm.beta = Beta; fRotRightLowerArm.dCutoff = DerivativeCutoff;
            fRotLeftToe.minCutoff = MinCutoff; fRotLeftToe.beta = Beta; fRotLeftToe.dCutoff = DerivativeCutoff;
            fRotRightToe.minCutoff = MinCutoff; fRotRightToe.beta = Beta; fRotRightToe.dCutoff = DerivativeCutoff;
            fRotLeftShoulder.minCutoff = MinCutoff; fRotLeftShoulder.beta = Beta; fRotLeftShoulder.dCutoff = DerivativeCutoff;
            fRotRightShoulder.minCutoff = MinCutoff; fRotRightShoulder.beta = Beta; fRotRightShoulder.dCutoff = DerivativeCutoff;
        }
        private void OnPlayersHeightChangedNextFrame(HeightModeChange HeightModeChange)
        {
            var Data = BasisFullIKConstraint.data;
            SetHandCollisionScale(ref Data, BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
            BasisFullIKConstraint.data = Data;
        }
        public static void SetHandCollisionScale(ref BasisFullBodyData BodyData, float Scale)
        {
            //1.6m is the default values for below..
            BodyData.HandSkin = 0.03f * Scale;
            BodyData.HandRadius = 0.01f * Scale;
            BodyData.ChestRadius = 0.07f * Scale;
            BodyData.CollisionSkin = 0.05f * Scale;

            var hips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled;
            var spine = BasisLocalBoneDriver.SpineControl.TposeLocalScaled;
            var chest = BasisLocalBoneDriver.ChestControl.TposeLocalScaled;

            var neck = BasisLocalBoneDriver.NeckControl.TposeLocalScaled;
            var head = BasisLocalBoneDriver.HeadControl.TposeLocalScaled;


            float minHeadSpineHeight = 0f;
            minHeadSpineHeight += Vector3.Distance(hips.position, spine.position);
            minHeadSpineHeight += Vector3.Distance(spine.position, chest.position);
            minHeadSpineHeight += Vector3.Distance(chest.position, neck.position);
            minHeadSpineHeight += Vector3.Distance(neck.position, head.position);

            BodyData.minHeadSpineHeight = minHeadSpineHeight;
        }
        public void Spine(GameObject mainRig)
        {
            if (localPlayer == null || mainRig == null)
            {
                return;
            }

            BasisAnimationRiggingHelper.CreateBasisFullBodyRIG(localPlayer,  mainRig, basisTransformMapping, out BasisFullIKConstraint);

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;
            OnPlayersHeightChangedNextFrame( HeightModeChange.OnTpose);

            var data = BasisFullIKConstraint.data;

            // Legs enabled by presence
            BasisLocalBoneDriver.LeftFootControl.OnHasRigChanged += (hasRig) =>
            {
                if(hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnableLeftLeg = HasRigLayer(BasisLocalBoneDriver.LeftFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableLeftLeg = HasRigLayer(BasisLocalBoneDriver.LeftFootControl);

            BasisLocalBoneDriver.RightFootControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnableRightLeg = HasRigLayer(BasisLocalBoneDriver.RightFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableRightLeg = HasRigLayer(BasisLocalBoneDriver.RightFootControl);

            BasisLocalBoneDriver.LeftLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnableLeftLowerLeg = HasRigLayer(BasisLocalBoneDriver.LeftLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableLeftLowerLeg = HasRigLayer(BasisLocalBoneDriver.LeftLowerLegControl);

            BasisLocalBoneDriver.RightLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnableRightLowerLeg = HasRigLayer(BasisLocalBoneDriver.RightLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableRightLowerLeg = HasRigLayer(BasisLocalBoneDriver.RightLowerLegControl);

            // Toes
            BasisLocalBoneDriver.LeftToeControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);

            BasisLocalBoneDriver.RightToeControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);

            // Hands
            BasisLocalBoneDriver.LeftHandControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);

            BasisLocalBoneDriver.RightHandControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);

            // Lower arms (hand hints)
            BasisLocalBoneDriver.LeftLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.HintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);

            BasisLocalBoneDriver.RightLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.HintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);

            // Chest (head hint)
            BasisLocalBoneDriver.ChestControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.HintWeightHead = HasRigLayer(BasisLocalBoneDriver.ChestControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightHead = HasRigLayer(BasisLocalBoneDriver.ChestControl);

            // Chest (head hint)
            BasisLocalBoneDriver.LeftShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);

            // Chest (head hint)
            BasisLocalBoneDriver.RightShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                if (hasRig == false)//we only disable ik on calibration maintaining poses as long as possible
                {
                    return;
                }
                var d = BasisFullIKConstraint.data;
                d.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);

            // Initialize offsets and weights per humanoid bone
            int totalBones = BasisFullBodyData.Count;
            for (int slot = 0; slot < totalBones; slot++)
            {
                var bone = (HumanBodyBones)slot;
                var t = ResolveHumanoidBoneTransform(bone);
                if (t == null)
                {
                    continue;
                }

                data.SetWeight(slot, false);
                data.SetOffsetRotation(slot, t.rotation);
                data.SetTargetRotation(slot, t.rotation);
            }
            data.MaxBendDeg = 90;
            data.MinFactor = 1f;
            data.MaxFactor = 1f;
            data.StruggleStart = 0.9f;
            data.StruggleEnd = 1;
            data.MaxChestDelta = 90;
            data.MaxHipDelta = 90;

            BasisFullIKConstraint.data = data;
        }
        public void DisableAllTrackers()
        {
            if (BasisFullIKConstraint != null)
            {
                var data = BasisFullIKConstraint.data;
                data.EnableLeftLeg = false;
                data.EnableRightLeg = false;
                data.EnableLeftLowerLeg = false;
                data.EnableRightLowerLeg = false;
                data.LeftToeEnabled = false;
                data.RightToeEnabled = false;
                // data.EnabledLeftHand = false;
                // data.EnabledRightHand = false;
                data.HintWeightLeftHand = false;
                data.HintWeightRightHand = false;
                data.HintWeightHead = false;
                data.EnabledLeftShoulder = false;
                data.EnabledRightShoulder = false;
                BasisFullIKConstraint.data = data;
            }
        }
        private static bool HasRigLayer(BasisLocalBoneControl control)
        {
            return control.HasRigLayer == BasisHasRigLayer.HasRigLayer;
        }

        public GameObject CreateOrGetRig(string role, bool enabled, out Rig rig, out RigLayer rigLayer)
        {
            rig = null;
            rigLayer = default;

            if (localPlayer?.BasisAvatar?.Animator == null)
            {
                return null;
            }

            if (Builder != null)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer?.rig != null && layer.rig.name == $"Rig {role}")
                    {
                        rig = layer.rig;
                        rigLayer = layer;
                        return layer.rig.gameObject;
                    }
                }
            }

            var anim = localPlayer.BasisAvatar.Animator;
            GameObject rigGO = BasisAnimationRiggingHelper.CreateAndSetParent(anim.transform, $"Rig {role}");

            rig = BasisHelpers.GetOrAddComponent<Rig>(rigGO);
            rigLayer = new RigLayer(rig, enabled);

            if (Builder == null)
            {
                Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(anim.gameObject);
            }

            Builder.layers.Add(rigLayer);

            return rigGO;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideUsage(HumanBodyBones bone, bool enabled)
        {
            var data = BasisFullIKConstraint.data;
            data.SetWeight((int)bone, enabled);
            BasisFullIKConstraint.data = data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideData(HumanBodyBones bone, in Vector3 position, in Quaternion rotation)
        {
            var data = BasisFullIKConstraint.data;
            data.SetTargetPosition((int)bone, position);
            data.SetTargetRotation((int)bone, rotation);
            BasisFullIKConstraint.data = data;
        }
        private Transform ResolveHumanoidBoneTransform(HumanBodyBones bone)
        {
            // Prefer references map if available
            if (BasisLocalAvatarDriver.Mapping != null && BasisLocalAvatarDriver.Mapping.GetTransform(bone, out Transform refT))
            {
                return refT;
            }
            // Fallback to Animator
            var animator = localPlayer?.BasisAvatar?.Animator;
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }
    }
}
