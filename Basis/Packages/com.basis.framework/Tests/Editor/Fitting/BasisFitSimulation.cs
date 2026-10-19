using System;
using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    [Flags]
    public enum BasisFitTrackers
    {
        None = 0,
        Hips = 1,
        Feet = 2,
        Knees = 4,
        Elbows = 8,
        Chest = 16,
    }
    public enum BasisFitPose
    {
        TPose,
        Idle,
        ReachForward,
        ReachUp,
        LookDown,
        Crouch,
        LookAtFeet,
        LookUp,
        HeadRoll,
        HeadYaw,
        Rise,
        Bow,
        StepUp,
        LookPitch,
        DesktopPitch,
    }
    public enum BasisFitPipeline
    {
        Legacy,
        Current,
    }
    public struct BasisFitScenario
    {
        public BasisFitBodySpec Player, Avatar;
        public BasisFitTrackers Trackers;
        public BasisSelectedHeightMode Mode;
        public BasisFitPipeline Pipeline;
        public float AvatarScale, FitDeviation, FloorError;
        public bool CalibrateLookingDown;
        public string Label => $"{Pipeline}|{Mode}|{TrackerLabel(Trackers)}|{Player.Name}|{Avatar.Name}|k{AvatarScale:0.##}{(FloorError != 0f ? $"|floor{FloorError:+0.##;-0.##}" : "")}";
        public static string TrackerLabel(BasisFitTrackers t)
        {
            int count = 3 + (t.HasFlag(BasisFitTrackers.Hips) ? 1 : 0) + (t.HasFlag(BasisFitTrackers.Feet) ? 2 : 0) + (t.HasFlag(BasisFitTrackers.Knees) ? 2 : 0) + (t.HasFlag(BasisFitTrackers.Elbows) ? 2 : 0) + (t.HasFlag(BasisFitTrackers.Chest) ? 1 : 0);
            return count + "pt";
        }
    }
    public struct BasisFitSetupResult
    {
        public float UniformScale, EyeRatio, DeviceScale, Grounding, ArmScale, LegScale, TorsoScale, ArmResidual, ShoulderMismatch;
        public BasisScaleFitStatus Status;
    }
    public struct BasisFitMetrics
    {
        public float HeadError, EyeError, NeckStretch, VisibleNeckStretch, HandErrorL, HandErrorR, ElbowAngleDiff, FootGapL, FootGapR, FootErrorL, FootErrorR, HipsError, ShoulderHeightError, HipsHeightError, PostureError, KneeBend;
        public float HandError => Mathf.Max(HandErrorL, HandErrorR);
        public float FootGap => Mathf.Abs(FootGapL) > Mathf.Abs(FootGapR) ? FootGapL : FootGapR;
        public float FootError => Mathf.Max(FootErrorL, FootErrorR);
    }
    public sealed class BasisFitSimulation : IDisposable
    {
        struct Mount
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Valid;
        }
        public BasisFitTestRig Player, Avatar;
        public BasisFitScenario Scenario;
        public BasisFitSetupResult Setup;
        Mount hips, chest, leftFoot, rightFoot, leftKnee, rightKnee, leftElbow, rightElbow;
        Vector3 headFromEye, plantedLeft, plantedRight, authoredHipsFromHead, playerRestLeftFoot, deskHead, deskHips, deskEyeFromNeck;
        float avatarEye, neckRest, visibleNeckRest, shoulderRestY, authoredFootY, authoredHipsY;
        public const float Dt = 1f / 90f;
        public const int SolveFrames = 8;
        public static BasisFitSimulation Create(in BasisFitScenario scenario)
        {
            var sim = new BasisFitSimulation { Scenario = scenario };
            sim.Player = BasisFitTestRig.Build(scenario.Player, withSolver: false);
            sim.Avatar = BasisFitTestRig.Build(scenario.Avatar, withSolver: true);
            sim.Fit();
            sim.Calibrate();
            return sim;
        }
        bool Has(BasisFitTrackers t) => (Scenario.Trackers & t) != 0;
        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        void Fit()
        {
            PosePlayer(Player, BasisFitPose.TPose);
            playerRestLeftFoot = Player.LeftFoot.position;
            BasisFitTestRig a = Avatar;
            float playerSpan = Flat(Player.LeftHand.position, Player.RightHand.position), measuredEye = Player.Eye.position.y + Scenario.FloorError;
            float playerEye = Scenario.Pipeline == BasisFitPipeline.Current ? BasisCalibrationMath.SizingEyeHeight(measuredEye, playerSpan, true, 0f) : measuredEye;
            float playerHip = Has(BasisFitTrackers.Hips) ? Player.HipsTracker.position.y : 0f;
            avatarEye = a.Eye.position.y;
            float shoulders = Flat(a.LeftUpperArm.position, a.RightUpperArm.position);
            float flatSpan = Flat(a.LeftHand.position, a.RightHand.position);
            float segmentSpan = shoulders + Vector3.Distance(a.LeftUpperArm.position, a.LeftLowerArm.position) + Vector3.Distance(a.LeftLowerArm.position, a.LeftHand.position) + Vector3.Distance(a.RightUpperArm.position, a.RightLowerArm.position) + Vector3.Distance(a.RightLowerArm.position, a.RightHand.position);
            float legSpan = a.LeftUpperLeg.position.y - a.LeftFoot.position.y, spineSpan = a.Head.position.y - a.Hips.position.y;
            float k = Scenario.AvatarScale > 0f ? Scenario.AvatarScale : 1f, deviation = Scenario.FitDeviation;
            authoredFootY = (a.LeftFoot.position.y + a.RightFoot.position.y) * 0.5f * k;
            authoredHipsY = a.Hips.position.y * k;
            authoredHipsFromHead = (a.Hips.position - a.Head.position) * k;
            Setup.EyeRatio = avatarEye / playerEye;
            BasisBodyFitResult fit;
            if (Scenario.Pipeline == BasisFitPipeline.Current)
            {
                BasisScaleState state = BasisScaleResolver.Resolve(new BasisScaleInputs
                {
                    Mode = Scenario.Mode,
                    PlayerEye = playerEye,
                    PlayerTrackedEye = measuredEye,
                    PlayerSpan = playerSpan,
                    AvatarEye = avatarEye,
                    AvatarSpan = segmentSpan,
                    AvatarShoulderWidth = shoulders,
                    AppliedUpScale = k,
                    FitDeviation = deviation,
                    SpanUsable = true,
                });
                Setup.UniformScale = state.UniformScale;
                Setup.DeviceScale = state.DeviceScale;
                Setup.Grounding = state.GroundingOffset;
                Setup.Status = state.BestFit.IsValid ? state.BestFit.Status : BasisScaleFitStatus.NoData;
                var measurements = new BasisBodyFitMeasurements
                {
                    PlayerEyeHeight = playerEye,
                    PlayerArmSpan = playerSpan,
                    AvatarEyeHeight = avatarEye,
                    AvatarArmSpan = segmentSpan,
                    AvatarShoulderWidth = shoulders,
                    UniformScale = state.UniformScale,
                };
                fit = BasisBodyFitCore.Solve(in measurements, deviation);
                BasisBodyFitApply.Apply(a.Skeleton, a.Mapping, fit);
                Setup.ArmResidual = ((playerSpan * state.UniformScale - shoulders) * 0.5f) / Mathf.Max(1e-4f, (segmentSpan - shoulders) * 0.5f);
            }
            else
            {
                fit = LegacyFit(playerEye, playerSpan, playerHip, avatarEye, flatSpan, shoulders, a.Hips.position.y, legSpan, spineSpan, k, deviation);
                a.Skeleton.ResetFit();
                SetScale(a.LeftLowerArm, fit.ArmScale); SetScale(a.LeftHand, fit.ArmScale); SetScale(a.RightLowerArm, fit.ArmScale); SetScale(a.RightHand, fit.ArmScale);
                SetScale(a.LeftLowerLeg, fit.LegScale); SetScale(a.LeftFoot, fit.LegScale); SetScale(a.RightLowerLeg, fit.LegScale); SetScale(a.RightFoot, fit.LegScale);
                SetScale(a.Spine, fit.TorsoScale); SetScale(a.Chest, fit.TorsoScale); SetScale(a.UpperChest, fit.TorsoScale); SetScale(a.Neck, fit.TorsoScale); SetScale(a.Head, fit.TorsoScale);
            }
            Setup.ArmScale = fit.HasArmFit ? fit.ArmScale : 1f;
            Setup.LegScale = fit.HasBodyFit ? fit.LegScale : 1f;
            Setup.TorsoScale = fit.HasBodyFit ? fit.TorsoScale : 1f;
            a.Skeleton.WriteFittedLocalPositions();
            a.Job.RescaleTposeFit(Setup.ArmScale, Setup.TorsoScale);
            a.Wrapper.transform.localScale = Vector3.one * k;
            a.Skeleton.RefreshRootFromTransform();
            a.Job.RescaleTposeScalars(k);
            a.Job.minHeadSpineHeight = a.Job.RalivRestChainLength();
            a.ResetToRestPose();
            Transform neckBase = a.Neck != null ? a.Neck : a.UpperChest != null ? a.UpperChest : a.Chest;
            BasisFitBodySpec spec = Scenario.Avatar;
            neckRest = (spec.HeadY - (spec.HasNeck ? spec.NeckY : spec.HasUpperChest ? spec.UpperChestY : spec.ChestY)) * k;
            visibleNeckRest = (spec.HeadY - spec.ShoulderRootY) * k;
            Setup.ShoulderMismatch = (spec.EyeY - spec.ShoulderJointY) * k - (Scenario.Player.EyeY - Scenario.Player.ShoulderJointY) * Setup.DeviceScale;
            shoulderRestY = a.LeftUpperArm.position.y;
            _ = neckBase;
        }
        void SetScale(Transform bone, float scale)
        {
            if (bone != null && !Mathf.Approximately(scale, 1f))
            {
                Avatar.Skeleton.SetFitScale(bone, scale);
            }
        }
        BasisBodyFitResult LegacyFit(float playerEye, float playerSpan, float playerHip, float avatarEye, float avatarSpan, float shoulders, float avatarHip, float legSpan, float spineSpan, float k, float deviation)
        {
            float eyeRatio = avatarEye / playerEye, toAvatar = eyeRatio;
            float armOnly = Mathf.Max(0f, avatarSpan - shoulders);
            BasisSelectedHeightMode mode = Scenario.Mode == BasisSelectedHeightMode.Auto ? BasisSelectedHeightMode.BestFit : Scenario.Mode;
            if (mode == BasisSelectedHeightMode.BestFit)
            {
                var input = new BasisScaleFitInput
                {
                    MaxEyeDeviation = 0.15f,
                    Eye = new BasisScaleFitSample { Player = playerEye, Avatar = avatarEye, Weight = BasisScaleFitCore.EyeWeight },
                    ArmSpan = new BasisScaleFitSample { Player = playerSpan, Avatar = avatarSpan, Slack = deviation * armOnly, Weight = BasisScaleFitCore.ArmSpanWeight },
                    HipHeight = playerHip > 0f ? new BasisScaleFitSample { Player = playerHip, Avatar = avatarHip, Slack = deviation * Mathf.Min(legSpan, spineSpan), Weight = BasisScaleFitCore.HipWeight } : BasisScaleFitSample.None,
                };
                BasisScaleFitResult s = BasisScaleFitCore.Solve(in input);
                if (s.IsValid)
                {
                    toAvatar = s.Scale;
                    Setup.Status = s.Status;
                }
            }
            BasisBodyFitResult fit = BasisBodyFitResult.Identity;
            float playerArm = (playerSpan * toAvatar - shoulders) * 0.5f, avatarArm = armOnly * 0.5f;
            if (avatarArm > 0.05f && playerArm > 0.05f && playerArm / avatarArm >= 0.5f && playerArm / avatarArm <= 2f)
            {
                fit.ArmScale = Mathf.Clamp(playerArm / avatarArm, 1f - deviation, 1f + deviation);
                fit.ArmStatus = BasisBodyFitStatus.Fitted;
            }
            if (playerHip > 0.05f)
            {
                float hipBand = deviation * Mathf.Min(legSpan, spineSpan), shift = Mathf.Clamp(playerHip * toAvatar - avatarHip, -hipBand, hipBand);
                fit.LegScale = 1f + shift / legSpan;
                fit.TorsoScale = 1f - shift / spineSpan;
                fit.BodyStatus = BasisBodyFitStatus.Fitted;
            }
            float u = toAvatar;
            if (mode == BasisSelectedHeightMode.ArmSpan)
            {
                float fitted = shoulders + armOnly * (fit.HasArmFit ? fit.ArmScale : 1f);
                u = fitted / playerSpan;
            }
            Setup.UniformScale = u;
            Setup.DeviceScale = u * k;
            Setup.Grounding = mode == BasisSelectedHeightMode.EyeHeight ? 0f : Mathf.Max(0f, avatarEye * k / Setup.DeviceScale - playerEye);
            Setup.ArmResidual = playerArm / Mathf.Max(1e-4f, avatarArm);
            return fit;
        }
        Vector3 Map(Vector3 p) => (p + Vector3.up * (Scenario.FloorError + Setup.Grounding)) * Setup.DeviceScale;
        Vector3 Fitted(Transform bone) => Avatar.Wrapper.transform.TransformVector(Avatar.RestLocal(bone));
        void Calibrate()
        {
            PosePlayer(Player, BasisFitPose.TPose);
            if (Scenario.CalibrateLookingDown)
            {
                BendLegs(Player, 15f);
                Player.Spine.rotation = Quaternion.Euler(8f, 0f, 0f);
                Player.Neck?.SetPositionAndRotation(Player.Neck.position, Quaternion.Euler(25f, 0f, 0f));
                Player.Head.rotation = Quaternion.Euler(35f, 0f, 0f);
                ArmsLevel(Player);
            }
            Avatar.ResetToRestPose();
            headFromEye = Avatar.Head.position - Avatar.Eye.position;
            deskEyeFromNeck = Avatar.Eye.position - (Avatar.Neck != null ? Avatar.Neck : Avatar.Head.parent).position;
            Vector3 headTarget = Map(Player.Eye.position) + Player.Eye.rotation * headFromEye;
            BasisCalibrationMath.ComputeTposeAnchor(headTarget, Player.Eye.rotation, Fitted(Avatar.Head), out Vector3 anchorPos, out Quaternion anchorRot);
            Vector3 Reference(Transform bone) => anchorPos + anchorRot * Fitted(bone);
            deskHead = Reference(Avatar.Head);
            deskHips = Reference(Avatar.Hips);
            Mount Capture(Transform tracker, Transform bone)
            {
                Vector3 trackerPos = Map(tracker.position), reference = Reference(bone);
                if (Scenario.Pipeline == BasisFitPipeline.Current && (bone == Avatar.LeftFoot || bone == Avatar.RightFoot))
                {
                    reference.y = authoredFootY;
                }
                BasisCalibrationMath.ComputeInverseOffset(trackerPos, tracker.rotation, reference, bone.rotation, out Vector3 position, out Quaternion rotation);
                return new Mount { Position = position, Rotation = rotation, Valid = true };
            }
            hips = Capture(Has(BasisFitTrackers.Hips) ? Player.HipsTracker : Player.Hips, Avatar.Hips);
            chest = Capture(Player.ChestTracker != null ? Player.ChestTracker : Player.Chest, Avatar.Chest);
            leftFoot = Capture(Player.LeftFootTracker, Avatar.LeftFoot);
            rightFoot = Capture(Player.RightFootTracker, Avatar.RightFoot);
            leftKnee = Capture(Player.LeftKneeTracker, Avatar.LeftLowerLeg);
            rightKnee = Capture(Player.RightKneeTracker, Avatar.RightLowerLeg);
            leftElbow = Capture(Player.LeftElbowTracker, Avatar.LeftLowerArm);
            rightElbow = Capture(Player.RightElbowTracker, Avatar.RightLowerArm);
            plantedLeft = Reference(Avatar.LeftFoot);
            plantedRight = Reference(Avatar.RightFoot);
            float leg = Vector3.Distance(Fitted(Avatar.LeftUpperLeg), Fitted(Avatar.LeftLowerLeg)) + Vector3.Distance(Fitted(Avatar.LeftLowerLeg), Fitted(Avatar.LeftFoot));
            BasisLocalFootDriver.DeriveFootHeights(authoredFootY - Fitted(Avatar.LeftToe).y, authoredFootY, Fitted(Avatar.LeftUpperLeg).y - Fitted(Avatar.LeftFoot).y, leg, 0.2f, out float planted, out float hang);
            plantedLeft.y = plantedRight.y = Scenario.Pipeline == BasisFitPipeline.Current ? planted : hang;
        }
        Vector3 Apply(in Mount m, Transform tracker, out Quaternion rotation)
        {
            BasisCalibrationMath.ApplyInverseOffset(Map(tracker.position), tracker.rotation, m.Position, m.Rotation, out Vector3 position, out rotation);
            return position;
        }
        public static bool IsDesktop(BasisFitPose pose) => pose == BasisFitPose.DesktopPitch;
        public BasisFitMetrics Solve(BasisFitPose pose, float pitch = 0f)
        {
            PosePlayer(Player, pose, pitch);
            BasisFitTestRig a = Avatar;
            ref BasisEerieMovement job = ref a.Job;
            bool desktop = IsDesktop(pose);
            Vector3 hmd = Map(Player.Eye.position);
            Quaternion hmdRot = Player.Eye.rotation;
            Vector3 headTarget = hmd + hmdRot * headFromEye;
            Vector3 hipsTarget = Apply(in hips, Has(BasisFitTrackers.Hips) ? Player.HipsTracker : Player.Hips, out Quaternion hipsRot);
            if (desktop)
            {
                hmdRot = Quaternion.Euler(pitch, 0f, 0f);
                BasisHeadPitchSwingCore.Solve(pitch, 0f, deskEyeFromNeck, 0.35f, out Vector3 swing, out _);
                headTarget = deskHead + swing;
                hmd = headTarget - hmdRot * headFromEye;
                hipsTarget = deskHips;
                hipsRot = Quaternion.identity;
            }
            Vector3 chestTarget = Apply(in chest, Player.ChestTracker, out Quaternion chestRot);
            Vector3 leftHand = Map(Player.LeftHand.position), rightHand = Map(Player.RightHand.position);
            Vector3 leftFootTarget = plantedLeft, rightFootTarget = plantedRight;
            Quaternion leftFootRot = Quaternion.identity, rightFootRot = Quaternion.identity;
            if (Has(BasisFitTrackers.Feet))
            {
                leftFootTarget = Apply(in leftFoot, Player.LeftFootTracker, out leftFootRot);
                rightFootTarget = Apply(in rightFoot, Player.RightFootTracker, out rightFootRot);
            }
            else if (pose == BasisFitPose.StepUp)
            {
                leftFootTarget += Map(Player.LeftFoot.position) - Map(playerRestLeftFoot);
            }
            Vector3 leftKneeHint = Apply(in leftKnee, Player.LeftKneeTracker, out _), rightKneeHint = Apply(in rightKnee, Player.RightKneeTracker, out _);
            Vector3 leftElbowHint = Apply(in leftElbow, Player.LeftElbowTracker, out _), rightElbowHint = Apply(in rightElbow, Player.RightElbowTracker, out _);
            var facts = new BasisEerieFrameFacts
            {
                deltaTime = Dt,
                footSimReady = true,
                footIKSetting = true,
                upright = true,
                hipsTracked = Has(BasisFitTrackers.Hips),
                chestTracked = Has(BasisFitTrackers.Chest),
                leftFootTracked = Has(BasisFitTrackers.Feet),
                rightFootTracked = Has(BasisFitTrackers.Feet),
                leftLegTracked = Has(BasisFitTrackers.Feet),
                rightLegTracked = Has(BasisFitTrackers.Feet),
                leftKneeTracked = Has(BasisFitTrackers.Knees),
                rightKneeTracked = Has(BasisFitTrackers.Knees),
                leftElbowTracked = Has(BasisFitTrackers.Elbows),
                rightElbowTracked = Has(BasisFitTrackers.Elbows),
                leftHandWeight = desktop ? 0f : 1f,
                rightHandWeight = desktop ? 0f : 1f,
                leftFootSim = Has(BasisFitTrackers.Feet) ? 0f : 1f,
                rightFootSim = Has(BasisFitTrackers.Feet) ? 0f : 1f,
            };
            BasisEeriePlanner.Frame(ref job, in facts);
            float standingHead = Fitted(a.Head).y;
            job.playerUp = Vector3.up;
            job.targetPositionHead = headTarget;
            job.targetRotationHead = hmdRot;
            job.targetPositionHips = hipsTarget;
            job.targetRotationHips = hipsRot;
            job.targetPositionChest = chestTarget;
            job.targetPositionChestRaw = chestTarget;
            job.targetRotationChest = chestRot;
            job.targetPositionLeftHand = leftHand;
            job.targetRotationLeftHand = Player.LeftHand.rotation;
            job.targetPositionRightHand = rightHand;
            job.targetRotationRightHand = Player.RightHand.rotation;
            job.hintPositionLeftHand = leftElbowHint;
            job.hintPositionRightHand = rightElbowHint;
            job.hintRotationLeftHand = Quaternion.identity;
            job.hintRotationRightHand = Quaternion.identity;
            job.targetPositionLeftLowerLeg = leftFootTarget;
            job.targetRotationLeftLowerLeg = leftFootRot;
            job.targetPositionRightLowerLeg = rightFootTarget;
            job.targetRotationRightLowerLeg = rightFootRot;
            job.hintPositionLeftLowerLeg = leftKneeHint;
            job.hintPositionRightLowerLeg = rightKneeHint;
            job.hintRotationLeftLowerLeg = Quaternion.identity;
            job.hintRotationRightLowerLeg = Quaternion.identity;
            job.kneeBendPrefLeft = hipsRot * Vector3.right;
            job.kneeBendPrefRight = hipsRot * Vector3.right;
            job.kneeAnteriorRef = hipsRot * Vector3.right;
            job.leftDrivenTargetRot = Quaternion.identity;
            job.rightDrivenTargetRot = Quaternion.identity;
            job.standingHeadHeight = standingHead;
            job.crouchDepth = Mathf.Max(0f, Mathf.Min(standingHead - headTarget.y, standingHead - headFromEye.y - hmd.y));
            for (int i = 0; i < job.armState.Length; i++) job.armState[i] = default;
            for (int i = 0; i < job.legState.Length; i++) job.legState[i] = default;
            for (int i = 0; i < job.chestSpring.Length; i++) job.chestSpring[i] = default;
            for (int frame = 0; frame < SolveFrames; frame++)
            {
                a.ResetToRestPose();
                a.Skeleton.ApplyFit();
                job.poseStream = a.Skeleton.Stream;
                job.poseStream.deltaTime = Dt;
                job.Execute();
                a.Skeleton.ScatterNow();
            }
            BasisFitMetrics m = default;
            m.HeadError = Vector3.Distance(a.Head.position, headTarget);
            m.EyeError = Vector3.Distance(a.Eye.position, hmd);
            Transform neckBase = a.Neck != null ? a.Neck : a.UpperChest != null ? a.UpperChest : a.Chest;
            m.NeckStretch = neckRest > 1e-4f ? Vector3.Distance(a.Head.position, neckBase.position) / neckRest - 1f : 0f;
            Vector3 shoulderMid = (a.LeftShoulder.position + a.RightShoulder.position) * 0.5f;
            m.VisibleNeckStretch = visibleNeckRest > 1e-4f ? Vector3.Distance(a.Head.position, shoulderMid) / visibleNeckRest - 1f : 0f;
            m.HandErrorL = Vector3.Distance(a.LeftHand.position, leftHand);
            m.HandErrorR = Vector3.Distance(a.RightHand.position, rightHand);
            m.ElbowAngleDiff = 0.5f * (Mathf.Abs(ElbowAngle(a.LeftUpperArm, a.LeftLowerArm, a.LeftHand) - ElbowAngle(Player.LeftUpperArm, Player.LeftLowerArm, Player.LeftHand)) + Mathf.Abs(ElbowAngle(a.RightUpperArm, a.RightLowerArm, a.RightHand) - ElbowAngle(Player.RightUpperArm, Player.RightLowerArm, Player.RightHand)));
            float ankle = Scenario.Avatar.AnkleY * (Scenario.AvatarScale > 0f ? Scenario.AvatarScale : 1f);
            m.FootGapL = a.LeftFoot.position.y - ankle;
            m.FootGapR = a.RightFoot.position.y - ankle;
            m.FootErrorL = Vector3.Distance(a.LeftFoot.position, leftFootTarget);
            m.FootErrorR = Vector3.Distance(a.RightFoot.position, rightFootTarget);
            m.HipsError = Has(BasisFitTrackers.Hips) ? Vector3.Distance(a.Hips.position, hipsTarget) : 0f;
            m.ShoulderHeightError = 0.5f * (a.LeftUpperArm.position.y + a.RightUpperArm.position.y) - shoulderRestY;
            m.HipsHeightError = a.Hips.position.y - authoredHipsY;
            m.PostureError = Vector3.Distance(a.Hips.position - a.Head.position, authoredHipsFromHead);
            m.KneeBend = Mathf.Max(ElbowAngle(a.LeftUpperLeg, a.LeftLowerLeg, a.LeftFoot), ElbowAngle(a.RightUpperLeg, a.RightLowerLeg, a.RightFoot));
            return m;
        }
        static float ElbowAngle(Transform upper, Transform lower, Transform hand) => Vector3.Angle(lower.position - upper.position, hand.position - lower.position);
        public static void PosePlayer(BasisFitTestRig p, BasisFitPose pose, float pitch = 0f)
        {
            BasisFitBodySpec s = p.Spec;
            foreach (Transform t in p.Wrapper.GetComponentsInChildren<Transform>())
            {
                if (t != p.Wrapper.transform && !t.name.EndsWith("Tracker") && t != p.Eye)
                {
                    t.localRotation = Quaternion.identity;
                }
            }
            p.Hips.position = new Vector3(0f, s.HipsY, 0f);
            switch (pose)
            {
                case BasisFitPose.Idle:
                    Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
                    break;
                case BasisFitPose.ReachForward:
                    Arms(p, new Vector3(-0.1f, 0f, 1f), new Vector3(-0.1f, 0f, 1f));
                    break;
                case BasisFitPose.ReachUp:
                    Arms(p, new Vector3(-0.15f, 1f, 0.1f), new Vector3(-0.1f, 1f, 0.15f));
                    break;
                case BasisFitPose.LookDown:
                    Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
                    p.Neck?.SetPositionAndRotation(p.Neck.position, Quaternion.Euler(15f, 0f, 0f));
                    p.Head.rotation = Quaternion.Euler(40f, 0f, 0f);
                    break;
                case BasisFitPose.LookAtFeet:
                    Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
                    p.Neck?.SetPositionAndRotation(p.Neck.position, Quaternion.Euler(30f, 0f, 0f));
                    p.Head.rotation = Quaternion.Euler(70f, 0f, 0f);
                    break;
                case BasisFitPose.Crouch:
                    Arms(p, new Vector3(-0.15f, -0.6f, 0.6f), new Vector3(-0.1f, -0.2f, 1f));
                    BendLegs(p, 50f);
                    break;
                case BasisFitPose.LookUp:
                    Look(p, Quaternion.Euler(-15f, 0f, 0f), Quaternion.Euler(-40f, 0f, 0f));
                    break;
                case BasisFitPose.HeadRoll:
                    Look(p, Quaternion.Euler(0f, 0f, 10f), Quaternion.Euler(0f, 0f, 30f));
                    break;
                case BasisFitPose.HeadYaw:
                    Look(p, Quaternion.Euler(0f, 20f, 0f), Quaternion.Euler(0f, 70f, 0f));
                    break;
                case BasisFitPose.LookPitch:
                    Look(p, Quaternion.Euler(pitch * 0.4f, 0f, 0f), Quaternion.Euler(pitch, 0f, 0f));
                    break;
                case BasisFitPose.Rise:
                    Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
                    p.Hips.position = new Vector3(0f, s.HipsY + 0.06f, 0f);
                    break;
                case BasisFitPose.Bow:
                    p.Spine.rotation = Quaternion.Euler(35f, 0f, 0f);
                    Arms(p, new Vector3(-0.1f, -1f, 0.3f), new Vector3(-0.1f, -1f, 0.45f));
                    break;
                case BasisFitPose.StepUp:
                    Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
                    p.LeftUpperLeg.rotation = Quaternion.Euler(-30f, 0f, 0f);
                    p.LeftLowerLeg.rotation = Quaternion.Euler(30f, 0f, 0f);
                    p.LeftFoot.rotation = Quaternion.identity;
                    break;
            }
        }
        static void Look(BasisFitTestRig p, Quaternion neck, Quaternion head)
        {
            Arms(p, new Vector3(-0.15f, -1f, 0.05f), new Vector3(-0.12f, -0.94f, 0.33f));
            p.Neck?.SetPositionAndRotation(p.Neck.position, neck);
            p.Head.rotation = head;
        }
        static void BendLegs(BasisFitTestRig p, float degrees)
        {
            BasisFitBodySpec s = p.Spec;
            float angle = degrees * Mathf.Deg2Rad, thigh = s.HipJointY - s.KneeY, shin = s.KneeY - s.AnkleY;
            float drop = (thigh + shin) * (1f - Mathf.Cos(angle)), back = (thigh - shin) * Mathf.Sin(angle);
            p.Hips.position = new Vector3(0f, s.HipsY - drop, -back);
            Vector3 thighDir = new Vector3(0f, -Mathf.Cos(angle), Mathf.Sin(angle)), shinDir = new Vector3(0f, -Mathf.Cos(angle), -Mathf.Sin(angle));
            p.LeftUpperLeg.rotation = Quaternion.FromToRotation(Vector3.down, thighDir);
            p.RightUpperLeg.rotation = Quaternion.FromToRotation(Vector3.down, thighDir);
            p.LeftLowerLeg.rotation = Quaternion.FromToRotation(Vector3.down, shinDir);
            p.RightLowerLeg.rotation = Quaternion.FromToRotation(Vector3.down, shinDir);
            p.LeftFoot.rotation = Quaternion.identity;
            p.RightFoot.rotation = Quaternion.identity;
        }
        static void ArmsLevel(BasisFitTestRig p)
        {
            p.LeftUpperArm.rotation = Quaternion.identity;
            p.LeftLowerArm.rotation = Quaternion.identity;
            p.RightUpperArm.rotation = Quaternion.identity;
            p.RightLowerArm.rotation = Quaternion.identity;
        }
        static void Arms(BasisFitTestRig p, Vector3 upperLeft, Vector3 lowerLeft)
        {
            Vector3 upperRight = new Vector3(-upperLeft.x, upperLeft.y, upperLeft.z), lowerRight = new Vector3(-lowerLeft.x, lowerLeft.y, lowerLeft.z);
            p.LeftUpperArm.rotation = Quaternion.FromToRotation(Vector3.left, upperLeft.normalized);
            p.LeftLowerArm.rotation = Quaternion.FromToRotation(Vector3.left, lowerLeft.normalized);
            p.RightUpperArm.rotation = Quaternion.FromToRotation(Vector3.right, upperRight.normalized);
            p.RightLowerArm.rotation = Quaternion.FromToRotation(Vector3.right, lowerRight.normalized);
            p.LeftHand.rotation = p.LeftLowerArm.rotation;
            p.RightHand.rotation = p.RightLowerArm.rotation;
        }
        public void Dispose()
        {
            Player?.Dispose();
            Avatar?.Dispose();
        }
    }
}
