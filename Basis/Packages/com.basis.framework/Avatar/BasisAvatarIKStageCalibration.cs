using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;
using Basis.IK;
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

        // Per-role limb bend-plane normal captured in the tracker's local frame at calibration. Consumed by
        // BasisLocalRigDriver via BasisTrackerBendNormalCore.ResolveWorldNormal so the knee bend plane follows
        // the lower-leg tracker's rotation instead of a fixed hips-frame axis (flip-free, natural bending).
        public static class BasisBendNormalStore
        {
            public static readonly Dictionary<BasisBoneTrackedRole, Vector3> LocalAxis = new();

            public static void Set(BasisBoneTrackedRole role, Vector3 localAxis) => LocalAxis[role] = localAxis;
            public static bool TryGet(BasisBoneTrackedRole role, out Vector3 localAxis) => LocalAxis.TryGetValue(role, out localAxis);
            public static void Clear() => LocalAxis.Clear();
        }

        /// <summary>
        /// Per-role tracker-to-bone rotation reference, captured at calibration: <c>Inverse(trackerWorld) *
        /// boneWorld</c>, so <c>trackerWorldLive * reference</c> is the tracker-implied BONE world rotation.
        /// A limb strap's clock angle around the limb is arbitrary and gets no <c>Recalibrated*</c> offset
        /// (those cover only head/hips/chest/feet/toes/shoulders), so a raw tracker rotation cannot be
        /// compared against a bone directly.
        /// </summary>
        public static class BasisLimbRollStore
        {
            public static readonly Dictionary<BasisBoneTrackedRole, Quaternion> TrackerToBone = new();

            public static void Set(BasisBoneTrackedRole role, Quaternion trackerToBone) => TrackerToBone[role] = trackerToBone;
            public static bool TryGet(BasisBoneTrackedRole role, out Quaternion trackerToBone) => TrackerToBone.TryGetValue(role, out trackerToBone);
            public static void Clear() => TrackerToBone.Clear();
        }

        /// <summary>
        /// Read-only snapshot of the most recent constellation calibration pass. Populated
        /// each time FullBodyCalibration runs and consumed by the editor visualizer. Live
        /// runtime never reads this — flipping any field cannot affect avatar behavior.
        /// </summary>
        public static class ConstellationDebug
        {
            public class DebugSample
            {
                public string DeviceId;
                public Vector3 BodyLocal;        // unscaled, body-relative; z = depth
                public Vector3 RawUnscaled;      // raw world-space unscaled pose at calibration time
                public float HeightRatio;
                public float LateralRatio;
                public bool Assigned;
                public BasisBoneTrackedRole AssignedRole;
                public float AssignedScore;
                public BasisBoneTrackedRole BestAnyRole;   // top-scoring role even if rejected
                public float BestAnyScore;
                public bool NearOrigin;          // raw unscaled was ≈ (0,0,0); strong indicator of a stale/missing device poll
            }

            public class DebugPrior
            {
                public BasisBoneTrackedRole Role;
                public float ExpectedHeight;
                public float ExpectedLateral;
                public float HeightSigma;
                public float LateralSigma;
                public bool Enabled;             // matches BasisSettingsDefaults toggle at calibration time
                public int AssignedSampleIndex;  // -1 if no tracker bound to this role
            }

            public static bool HasSnapshot;
            public static double Timestamp;
            public static string Status = "no calibration captured yet";
            public static float EyeHeight;
            public static float ArmReach;
            public static Vector3 BodyOrigin;
            public static Quaternion BodyRotation;
            public static readonly List<DebugSample> Samples = new List<DebugSample>(16);
            public static readonly List<DebugPrior> Priors = new List<DebugPrior>(16);

            public static float AcceptThreshold => BasisConstellationClassifier.AcceptThreshold;

            public static void Reset(string reason)
            {
                HasSnapshot = false;
                Timestamp = 0;
                Status = reason;
                EyeHeight = 0;
                ArmReach = 0;
                BodyOrigin = Vector3.zero;
                BodyRotation = Quaternion.identity;
                Samples.Clear();
                Priors.Clear();
            }
        }
        private struct TrackerSample
        {
            public BasisInput Input;
            public float HeightRatio;   // y / eyeHeight: 0 ≈ floor, 1 ≈ HMD
            public float LateralRatio;  // signed x / eyeHeight: +x = body's right
            public Vector3 BodyLocal;   // raw body-relative position (unscaled); z = depth, kept for debug visualization only
            public bool NearOrigin;     // tracker's UnscaledDeviceCoord came back ≈ Vector3.zero — almost always a stale/missing poll
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
        /// <summary>
        /// If Any trackers are actively connected to the IK system
        /// </summary>
        public static bool HasFBIKTrackers = false;

        /// <summary>
        /// Do trackers actually pose the LEGS?
        ///
        /// HasFBIKTrackers is a WHOLE-BODY flag -- it is true for a chest, shoulder, elbow or hips tracker just as
        /// readily as for a foot. Asking it a LEG question gives the wrong answer, and the failure is silent and
        /// severe: the animator suppresses the walk cycle "because we're in FBT", while leg IK simultaneously
        /// disables itself during locomotion (so the animation can take over) -- and the legs end up with NO driver
        /// at all. They freeze mid-stride.
        ///
        /// That went unnoticed for as long as the only things producing chest/shoulder/elbow trackers were real FBT
        /// rigs, which carry leg trackers too -- so the suppression happened to be right, for the wrong reason.
        /// MediaPipe spawns chest/shoulder/elbow trackers with no leg trackers anywhere, which is what exposed it.
        ///
        /// A leg question gets a leg answer. Read live so it self-heals on tracker dropout/reconnect.
        /// </summary>
        public static bool HasLegFBIKTrackers =>
               IsRoleTracked(BasisLocalBoneDriver.LeftFootControl)
            || IsRoleTracked(BasisLocalBoneDriver.RightFootControl)
            || IsRoleTracked(BasisLocalBoneDriver.LeftLowerLegControl)
            || IsRoleTracked(BasisLocalBoneDriver.RightLowerLegControl)
            || IsRoleTracked(BasisLocalBoneDriver.LeftUpperLegControl)
            || IsRoleTracked(BasisLocalBoneDriver.RightUpperLegControl);

        /// <summary>
        /// Is the PELVIS specifically tracker-driven?
        ///
        /// Distinct from HasLegFBIKTrackers on purpose: a hip tracker moves the leg ROOT but does not pose the legs,
        /// so it should not silence the walk cycle. It DOES make the pelvis authoritative, so anything that
        /// synthesises pelvis motion (the landing hip-dip, gait bob/sway/pelvis-rotation) must stand down or it
        /// fights the user's real body.
        /// </summary>
        public static bool HasHipsFBIKTracker => IsRoleTracked(BasisLocalBoneDriver.HipsControl);

        private static bool IsRoleTracked(BasisLocalBoneControl control)
        {
            return control != null && control.HasTracked == BasisHasTracked.HasTracker;
        }
        /// <summary>
        /// Builds a tracker→role assignment from the player's T-pose constellation alone.
        /// The avatar is no longer the source of truth for "where should this tracker be";
        /// instead the HMD defines a body frame and each tracker is classified by its
        /// height-above-floor and lateral offset, normalized to the calibrated player eye
        /// height. ComputeHints below still consults the avatar (chest/hips reference
        /// rotations), but the role-matching pass itself is avatar-independent — so the
        /// same trackers map the same way whether the user wears a child avatar or a
        /// three-meter giant.
        /// </summary>
        /// <summary>Raised at the end of <see cref="FullBodyCalibration"/>, after tracker roles have been (re)assigned.</summary>
        public static System.Action OnFullBodyCalibrated;

        public static void FullBodyCalibration()
        {
            BasisCalibrationDebugRecorder.Begin("fbt_recalib");
            try
            {
                BasisHeightDriver.OnAvatarFBCalibration();//avatar height is good,player height is needed
                HasFBIKTrackers = false;
                BasisHintBiasStore.Clear();
                BasisBendNormalStore.Clear();
                BasisLimbRollStore.Clear();
                BasisDeviceManagement.UnassignFBTrackers();
                BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

                // T-pose-free by default: every reference below derives from the load-time raw-joint
                // T-pose snapshot anchored at the head (ComputeTposeAnchor — the same frame DriveTpose
                // used to apply physically), so the avatar no longer snaps into T-pose on recalibrate.
                // Legacy fallback keeps the physical T-pose when the snapshot is missing (abnormal),
                // because the live-bone fallback reads inside CalculateOffset / the rotation recompute /
                // ComputeHints are only valid on a physically T-posed, head-aligned avatar.
                bool hasTposeSnapshot = BasisLocalAvatarDriver.HasTposeBoneSnapshot;
                if (hasTposeSnapshot)
                {
                    // Re-capture avatar height without the T-pose: eye height is the authored marker and
                    // arm span comes from the snapshot — both pose-independent.
                    BasisHeightDriver.CaptureAvatarHeightDuringTpose();
                }
                else
                {
                    BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
                }

                // The height capture above just refreshed the avatar metrics; recompute height/scale now
                // so DeviceScale uses them on THIS pass and classification below runs at the final scale.
                // The OnAvatarFBCalibration() at the top ran against the pre-calibration (often previous-
                // avatar) height, which left the nudge wrong after one calibration and only settled on a
                // second pass.
                BasisHeightDriver.ApplyScaleAndHeight();

                // ApplyScaleAndHeight settles the body fit first (it has to: the arm-span metric divides by
                // the FITTED span), so by here the bones already hold their final lengths and every tracker
                // offset, hint and anchor captured below is measured against the body the avatar will keep.
                // The hip measurement was taken at OnAvatarFBCalibration above while the previous pass's
                // roles were still assigned, and CalculatePlayerHipHeight keeps its last value through
                // UnassignFBTrackers.

                // DeviceScale just changed, but every input's ScaledDeviceCoord (and the bone-control
                // incoming fed from it) was produced by the poll above at the PRE-recompute scale.
                // CalculateOffset (inside ClassifyAndAssignTrackersFromTPose's role application) reads
                // ScaledDeviceCoord against avatar-bone references at the NEW scale, so any scale delta
                // this pass baked a proportional position error (|Δscale| × unscaled pose) into every
                // tracker offset — the "calibrate two or three times until the avatar fits" convergence.
                // SimulateAndApplyWithoutLerp re-polls every device (OnLatePollData), which rescales
                // ScaledDeviceCoord at the final DeviceScale, so the classifier, the anchor derivation
                // and the offset capture all see ONE scale frame.
                BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);
                if (hasTposeSnapshot == false)
                {
                    BasisLocalPlayer.Instance.DriveTpose();
                }

                // Scale-free snapshot of the head anchor the offsets below are captured against, so the
                // position offsets can later be re-derived for a different avatar/DeviceScale
                // (ReprojectTrackerOffsetsForCurrentAvatar) exactly like the rotation references are.
                var headOut = BasisLocalBoneDriver.HeadControl.OutGoingData;
                BasisCalibrationMath.UnscaleDeviceCoord(headOut.position, headOut.rotation, BasisHeightDriver.DeviceScale,
                    BasisInput.OffsetCoords.position, BasisInput.OffsetCoords.rotation, out s_calibHeadUnscaledPos, out s_calibHeadUnscaledRot);
                HasCalibrationHeadSnapshot = true;

                Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms = BasisLocalPlayer.Instance.LocalAvatarDriver.StoredRolesTransforms;

                try
                {
                    ClassifyAndAssignTrackersFromTPose();
                    LogConstellation();

                    // IMPORTANT: simulate once AFTER assignments so the bone controls reflect new tracker bindings.
                    BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

                    LogFbikRotationCalibration();
                    RecomputeFbikRotationCalibration();

                    ComputeHints(storedRoleTransforms);
                }
                finally
                {
                    // Only restore if something actually entered T-pose: the legacy fallback above, or
                    // an outer flow (the calibration UI's get-ready pose) that T-posed before calling us.
                    if (BasisLocalAvatarDriver.CurrentlyTposing)
                    {
                        BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
                        BasisLocalPlayer.Instance.LocalRigDriver.RigLayerActive = true;
                    }
                }

                BasisLocalPlayer.Instance.LocalAnimatorDriver.AssignHipsFBTracker();

                // Refresh the per-role calibration spheres so they re-anchor to the
                // newly stored avatar bone transforms. No-op when the ShowGizmos
                // master toggle is off; the toggle path rebuilds when it flips on.
                BasisLocalPlayer.Instance.LocalBoneDriver.RebuildCalibrationSpheres();

                // Re-measure now that classification has assigned the hips role. On the very first
                // calibration there was no assigned hips tracker to measure earlier, so this is what
                // activates the leg/spine half -- and it settles here, because the pass above will have
                // already applied the identical fit on every later calibration.
                BasisLocalHeightCalculator.CalculatePlayerHipHeight();
                BasisHeightDriver.ApplyScaleAndHeight();

                OnFullBodyCalibrated?.Invoke();
            }
            finally
            {
                BasisCalibrationDebugRecorder.Flush();
            }
        }

        // Issue #531 rotation diagnostic: dumps the FBIK per-effector rotation calibration
        // (frozen at rig setup in CreateBasisFullBodyRIG, never recomputed on recalibration)
        // alongside what a fresh recompute against the current T-pose bone/avatar frames would
        // produce. If 'frozen' stays constant across recalibrations while 'freshRecompute'
        // tracks the moved tracker, the freeze is the rotation residue.
        private static void LogFbikRotationCalibration()
        {
            var rig = BasisLocalPlayer.Instance.LocalRigDriver;
            if (rig == null || !rig.IKDataReady) return;
            ref BasisEerieMovement data = ref rig.IKJob;
            Common.BasisTransformMapping Mapping = BasisLocalAvatarDriver.Mapping;

            LogFbikRole("Head", data.offsetRotationHead, BasisLocalBoneDriver.HeadControl, Mapping.head);
            LogFbikRole("Hips", data.offsetRotationHips, BasisLocalBoneDriver.HipsControl, Mapping.Hips);
            LogFbikRole("Chest", data.offsetRotationChest, BasisLocalBoneDriver.ChestControl, Mapping.chest);
            LogFbikRole("LeftFoot", data.offsetRotationLeftFoot, BasisLocalBoneDriver.LeftFootControl, Mapping.leftFoot);
            LogFbikRole("RightFoot", data.offsetRotationRightFoot, BasisLocalBoneDriver.RightFootControl, Mapping.rightFoot);
            LogFbikRole("LeftToe", data.offsetRotationLeftToe, BasisLocalBoneDriver.LeftToeControl, Mapping.leftToe);
            LogFbikRole("RightToe", data.offsetRotationRightToe, BasisLocalBoneDriver.RightToeControl, Mapping.rightToe);
        }

        private static void LogFbikRole(string label, Quaternion frozen, BasisLocalBoneControl control, Transform avatarBone)
        {
            BasisCalibrationDebugRecorder.Rotation("FbikRotCalib", label, "frozen", frozen);
            if (control != null)
            {
                BasisCalibrationDebugRecorder.Rotation("FbikRotCalib", label, "boneOutWorld", control.OutgoingWorldData.rotation);
            }
            if (avatarBone != null)
            {
                BasisCalibrationDebugRecorder.Rotation("FbikRotCalib", label, "avatarBoneWorld", avatarBone.rotation);
            }
            if (control != null && avatarBone != null)
            {
                Quaternion fresh = Quaternion.Inverse(control.OutgoingWorldData.rotation) * avatarBone.rotation;
                BasisCalibrationDebugRecorder.Rotation("FbikRotCalib", label, "freshRecompute", fresh);
            }
        }

        // FBT per-effector rotation calibration. CreateBasisFullBodyRIG captures uncalibrated, animator-
        // relative offsets at avatar load; calibration overrides them with the head-driven version. The
        // reference captured here -- Inverse(boneSimOutgoing) * AnimatorRoot per effector -- is independent
        // of the avatar bind, so ApplyCalibrationToCurrentAvatar can re-derive the offsets for any avatar's
        // own T-pose bind. That is what lets FBT calibration survive an avatar swap without a T-pose redo.
        public static bool HasCalibrationReference;
        private static Quaternion s_refHead, s_refHips, s_refChest, s_refLeftFoot, s_refRightFoot,
            s_refLeftToe, s_refRightToe, s_refLeftShoulder, s_refRightShoulder;

        // Scale-free head anchor captured at ritual calibration (unscaled device space). The per-tracker
        // geometry pairing lives on each input (BasisInput.CalibratedUnscaledHead*, captured atomically
        // with its tracker snapshot); this global copy records "a ritual calibration exists".
        public static bool HasCalibrationHeadSnapshot;
        private static Vector3 s_calibHeadUnscaledPos;
        private static Quaternion s_calibHeadUnscaledRot = Quaternion.identity;

        /// <summary>
        /// The scale-free head anchor of the last ritual calibration — the reference for "standing in
        /// roughly the calibration pose". Per-tracker snapshot edits must be expressed in that tracker's
        /// OWN capture frame (BasisInput.CalibratedUnscaledHead*), which equals this frame for trackers
        /// captured during the ritual.
        /// </summary>
        public static bool TryGetCalibrationHeadSnapshot(out Vector3 unscaledPosition, out Quaternion unscaledRotation)
        {
            unscaledPosition = s_calibHeadUnscaledPos;
            unscaledRotation = s_calibHeadUnscaledRot;
            return HasCalibrationHeadSnapshot;
        }

        /// <summary>
        /// Re-derives every calibrated FBT tracker's POSITION inverse offset for the CURRENT avatar and
        /// DeviceScale from the scale-free calibration snapshots — the position analog of
        /// <see cref="ApplyCalibrationToCurrentAvatar"/> (which re-derives the per-effector ROTATION
        /// offsets). BasisHeightDriver calls this whenever the height/scale pipeline re-resolves
        /// (avatar swap, scale slider, OSC override), so FBT keeps fitting without redoing the T-pose.
        /// The player's live pose is irrelevant: only the stored calibration geometry and the current
        /// avatar's T-pose bind (TposeLocalScaled) are used. Each tracker rebuilds against the head
        /// anchor it was captured with (BasisInput.CalibratedUnscaledHead*), so a mid-session recapture
        /// keeps its own frame instead of inheriting the ritual one. The offset ROTATION is untouched —
        /// it maps tracker rotation to the bone-sim body frame, which is avatar- and scale-independent.
        /// No-op for trackers without a snapshot.
        /// </summary>
        public static void ReprojectTrackerOffsetsForCurrentAvatar()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (player == null || player.LocalBoneDriver == null || BasisLocalBoneDriver.HeadControl == null)
            {
                return;
            }
            // Head anchor uses the head CONTROL's T-pose (what DriveTpose itself anchors the root with);
            // each bone reference uses the RAW-joint load-time snapshot (what CalculateOffset captures
            // against), scaled by the current avatar scale — the same two sources the live capture uses.
            Vector3 headTpose = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position;
            float avatarScale = player.LocalAvatarDriver != null && player.LocalAvatarDriver.ScaleAvatarModification != null
                ? player.LocalAvatarDriver.ScaleAvatarModification.ApplyScale : 1f;
            if (float.IsNaN(avatarScale) || float.IsInfinity(avatarScale) || avatarScale <= 1e-6f)
            {
                avatarScale = 1f;
            }

            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.AllInputDevices : null;
            if (devices == null)
            {
                return;
            }
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = devices[Index];
                if (input == null || input.HasCalibratedOffsetSnapshot == false || input.HasControl == false || input.Control == null)
                {
                    continue;
                }
                if (input.TryGetRole(out BasisBoneTrackedRole role) == false
                    || BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role) == false
                    || input.Control.UseInverseOffset == false)
                {
                    continue;
                }

                Vector3 boneTpose = BasisLocalAvatarDriver.HasTposeBoneSnapshot
                    && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(role, out var bind)
                    ? bind.position * avatarScale
                    : input.Control.TposeLocalScaled.position;

                BasisCalibrationMath.ReprojectInverseOffsetPosition(
                    input.CalibratedUnscaledPosition, input.CalibratedUnscaledRotation,
                    input.CalibratedUnscaledHeadPosition, input.CalibratedUnscaledHeadRotation,
                    BasisHeightDriver.DeviceScale, BasisInput.OffsetCoords.position, BasisInput.OffsetCoords.rotation,
                    headTpose, boneTpose,
                    out Vector3 inverseOffsetPosition);
                input.Control.SetInverseOffset(inverseOffsetPosition, input.Control.InverseOffsetFromBone.rotation);
            }
        }

        private static void RecomputeFbikRotationCalibration()
        {
            var rig = BasisLocalPlayer.Instance.LocalRigDriver;
            if (rig == null || !rig.IKDataReady) return;
            Common.BasisTransformMapping Mapping = BasisLocalAvatarDriver.Mapping;
            // Calibration body frame: derived from the head (the frame DriveTpose would drive the root
            // to) instead of reading the live avatar root — the live root only matches in the instant
            // after a physical DriveTpose, which the T-pose-free pass no longer performs.
            Quaternion rootRot;
            BasisLocalBoneControl headControl = BasisLocalBoneDriver.HeadControl;
            if (BasisLocalAvatarDriver.HasTposeBoneSnapshot && headControl != null)
            {
                var headWorld = headControl.OutgoingWorldData;
                BasisCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headControl.TposeLocalScaled.position, out _, out rootRot);
            }
            else
            {
                rootRot = Mapping.HasAnimatorRoot ? Mapping.AnimatorRoot.rotation : Quaternion.identity;
            }

            // The head is driven straight from the HMD with no tracker mounting offset, so its calibrated
            // reference is pose-independent: the head effector offset is purely the avatar's head bind.
            // Capturing it like a tracker — against the LIVE head rotation — baked the head's
            // calibration-time pitch/roll into the offset. That's harmless for a level manual calibration
            // (get-ready pose), but automatic calibration fires whenever the trackers announce or a
            // SlimeVR reset lands — usually while you're looking down at the trackers — so the head sat
            // rotated wrong afterward. An identity reference makes the head offset the bind exactly, which
            // is what a perfectly level manual calibration already yields (so no manual regression).
            s_refHead = Quaternion.identity;
            s_refHips = CaptureCalibrationReference(BasisLocalBoneDriver.HipsControl, rootRot);
            s_refChest = CaptureCalibrationReference(BasisLocalBoneDriver.ChestControl, rootRot);
            s_refLeftFoot = CaptureCalibrationReference(BasisLocalBoneDriver.LeftFootControl, rootRot);
            s_refRightFoot = CaptureCalibrationReference(BasisLocalBoneDriver.RightFootControl, rootRot);
            s_refLeftToe = CaptureCalibrationReference(BasisLocalBoneDriver.LeftToeControl, rootRot);
            s_refRightToe = CaptureCalibrationReference(BasisLocalBoneDriver.RightToeControl, rootRot);
            s_refLeftShoulder = CaptureCalibrationReference(BasisLocalBoneDriver.LeftShoulderControl, rootRot);
            s_refRightShoulder = CaptureCalibrationReference(BasisLocalBoneDriver.RightShoulderControl, rootRot);
            HasCalibrationReference = true;

            ApplyCalibrationToCurrentAvatar();
        }

        /// <summary>
        /// Re-derives the per-effector FBT rotation offsets for the CURRENT avatar from the stored
        /// calibration reference, so the calibration survives an avatar swap (no T-pose redo). Called at
        /// calibration and again from the rig (re)build. No-op until a calibration has been captured.
        /// </summary>
        public static void ApplyCalibrationToCurrentAvatar()
        {
            if (!HasCalibrationReference) return;
            var rig = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalRigDriver : null;
            if (rig == null || !rig.IKDataReady) return;
            ref BasisEerieMovement data = ref rig.IKJob;
            Common.BasisTransformMapping Mapping = BasisLocalAvatarDriver.Mapping;
            Quaternion rootInv = Mapping.HasAnimatorRoot ? Quaternion.Inverse(Mapping.AnimatorRoot.rotation) : Quaternion.identity;

            BasisLocalRigDriver.RecalibratedHead = OffsetFromReference(s_refHead, rootInv, Mapping.head, BasisBoneTrackedRole.Head, data.offsetRotationHead);
            BasisLocalRigDriver.RecalibratedHips = OffsetFromReference(s_refHips, rootInv, Mapping.Hips, BasisBoneTrackedRole.Hips, data.offsetRotationHips);
            BasisLocalRigDriver.RecalibratedChest = OffsetFromReference(s_refChest, rootInv, Mapping.chest, BasisBoneTrackedRole.Chest, data.offsetRotationChest);
            BasisLocalRigDriver.RecalibratedLeftFoot = OffsetFromReference(s_refLeftFoot, rootInv, Mapping.leftFoot, BasisBoneTrackedRole.LeftFoot, data.offsetRotationLeftFoot);
            BasisLocalRigDriver.RecalibratedRightFoot = OffsetFromReference(s_refRightFoot, rootInv, Mapping.rightFoot, BasisBoneTrackedRole.RightFoot, data.offsetRotationRightFoot);
            BasisLocalRigDriver.RecalibratedLeftToe = OffsetFromReference(s_refLeftToe, rootInv, Mapping.leftToe, BasisBoneTrackedRole.LeftToes, data.offsetRotationLeftToe);
            BasisLocalRigDriver.RecalibratedRightToe = OffsetFromReference(s_refRightToe, rootInv, Mapping.rightToe, BasisBoneTrackedRole.RightToes, data.offsetRotationRightToe);
            BasisLocalRigDriver.RecalibratedLeftShoulder = OffsetFromReference(s_refLeftShoulder, rootInv, Mapping.leftShoulder, BasisBoneTrackedRole.LeftShoulder, data.offsetRotationLeftShoulder);
            BasisLocalRigDriver.RecalibratedRightShoulder = OffsetFromReference(s_refRightShoulder, rootInv, Mapping.RightShoulder, BasisBoneTrackedRole.RightShoulder, data.offsetRotationRightShoulder);
            BasisLocalRigDriver.HasRecalibratedRotationOffsets = true;
        }

        // Avatar-bind-independent calibration reference: Inverse(boneSimOutgoing) * AnimatorRoot.
        private static Quaternion CaptureCalibrationReference(BasisLocalBoneControl control, Quaternion animatorRootRot)
        {
            return control != null ? Quaternion.Inverse(control.OutgoingWorldData.rotation) * animatorRootRot : Quaternion.identity;
        }

        // offset = reference * (avatar bone relative to its own animator root). On the calibration avatar this
        // reproduces the head-driven Inverse(boneSimOutgoing) * avatarBone exactly; on a swapped-in avatar it
        // re-targets that same calibration onto the new bind. The bind comes from the load-time raw-joint
        // T-pose snapshot (pose-independent); the live read — Inverse(liveRoot) * liveBone — only equals the
        // bind while the avatar is physically T-posed, so it survives solely as the no-snapshot fallback.
        private static Quaternion OffsetFromReference(Quaternion reference, Quaternion animatorRootInv, Transform avatarBone, BasisBoneTrackedRole role, Quaternion current)
        {
            if (BasisLocalAvatarDriver.HasTposeBoneSnapshot
                && BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(role, out var bind))
            {
                return reference * bind.rotation;
            }
            return avatarBone != null ? reference * (animatorRootInv * avatarBone.rotation) : current;
        }

        // Arm-tracker calibration diagnostic: dumps every constellation sample (including the
        // UNASSIGNED ones the OffsetCapture rows can't show) plus the per-role priors, so a tracker
        // that "refused to calibrate" reveals its reason — NearOrigin (stale/zero poll), its best-fit
        // role + score (below the ~-9 accept threshold = didn't fit any role), or which role it lost to.
        private static void LogConstellation()
        {
            if (!BasisCalibrationDebugRecorder.Enabled) return;

            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            var samples = ConstellationDebug.Samples;
            for (int i = 0; i < samples.Count; i++)
            {
                ConstellationDebug.DebugSample s = samples[i];
                string note = $"dev={s.DeviceId} nearOrigin={s.NearOrigin} assigned={s.Assigned} role={s.AssignedRole} bestAny={s.BestAnyRole} bestScore={s.BestAnyScore.ToString("F2", ci)}";
                BasisCalibrationDebugRecorder.Pose("Constellation", "sample" + i.ToString(ci), "sample",
                    new Vector3(s.HeightRatio, s.LateralRatio, s.BestAnyScore), Quaternion.identity, Vector3.one, note);
            }

            var priors = ConstellationDebug.Priors;
            for (int p = 0; p < priors.Count; p++)
            {
                ConstellationDebug.DebugPrior pr = priors[p];
                string note = $"enabled={pr.Enabled} assignedSampleIdx={pr.AssignedSampleIndex}";
                BasisCalibrationDebugRecorder.Pose("Constellation", pr.Role.ToString(), "prior",
                    new Vector3(pr.ExpectedHeight, pr.ExpectedLateral, 0f), Quaternion.identity,
                    new Vector3(pr.HeightSigma, pr.LateralSigma, 0f), note);
            }

            // Every input device, including ones the classifier filters out before scoring (linked
            // pair-half, role-pinned, disabled-role, or polled at origin) so a tracker that never
            // becomes a sample is still visible — that's where a "refused to calibrate" tracker hides.
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.AllInputDevices : null;
            if (devices != null)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    BasisInput input = devices[i];
                    if (input == null) continue;
                    bool hasRole = input.TryGetRole(out BasisBoneTrackedRole r);
                    bool pinned = input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole;
                    bool fbCapable = BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(r);
                    Vector3 uns = input.UnscaledDeviceCoord.position;
                    bool nearOrigin = uns.sqrMagnitude < ConstellationNearOriginEpsilonSqr;
                    string note = $"common={input.CommonDeviceIdentifier} role={(hasRole ? r.ToString() : "(none)")} fbRole={(hasRole && fbCapable)} linked={input.IsLinked} pinned={pinned} nearOrigin={nearOrigin} dev={input.UniqueDeviceIdentifier}";
                    BasisCalibrationDebugRecorder.Pose("Devices", "device" + i.ToString(ci), "device",
                        uns, input.UnscaledDeviceCoord.rotation, Vector3.one, note);
                }
            }

            BasisCalibrationDebugRecorder.Meta("ConstellationStatus", ConstellationDebug.Status);
        }

        /// <summary>
        /// Classifies every free FB-trackable device by its position in the player's T-pose
        /// and assigns roles. Pure geometry — no avatar lookup, no tracker metadata.
        /// </summary>
        private static void ClassifyAndAssignTrackersFromTPose()
        {
            ConstellationDebug.Reset("calibration in progress");
            ConstellationDebug.Timestamp = System.DateTime.UtcNow.ToOADate();

            if (!TryGetHmdPose(out Vector3 hmdUnscaledPos, out Quaternion hmdUnscaledRot, out BasisInput hmdDevice))
            {
                ConstellationDebug.Status = "HMD pose unavailable — no trackers assigned";
                BasisDebug.LogError("FBIK constellation calibration: HMD pose unavailable, no trackers assigned", BasisDebug.LogTag.Input);
                return;
            }

            // All positions in this classifier are read from UnscaledDeviceCoord (raw playspace,
            // pre-scale). PlayerEyeHeight lives in the same frame, so HeightRatios reduce to
            // tracker height above the playspace floor as a fraction of the player's eye height
            // — independent of the avatar's DeviceScale. Reading transform.position here would
            // mix world (post-scale) with PlayerEyeHeight (pre-scale) and cause Hips↔Chest to
            // flip whenever the avatar isn't the same size as the player.
            float eyeHeight = Mathf.Max(BasisHeightDriver.PlayerEyeHeight, 0.5f);
            float floorY = hmdUnscaledPos.y - eyeHeight;

            // Body forward = HMD facing projected onto the horizontal plane. In T-pose the
            // player should be looking straight ahead, so the projection is well defined.
            Vector3 hmdFwdHoriz = hmdUnscaledRot * Vector3.forward;
            hmdFwdHoriz.y = 0f;
            if (hmdFwdHoriz.sqrMagnitude < 1e-4f)
            {
                // Near-vertical gaze (looking down at the trackers is the common calibration pose):
                // recover the facing from the head's up axis, which tips toward the body's forward as
                // the head pitches down (and away from it pitching up). Must stay in the unscaled
                // playspace frame — the player transform's forward is world-space and disagrees after
                // any snap-turn/teleport, flipping LateralRatio signs (left/right role swaps).
                Vector3 headUpHoriz = hmdUnscaledRot * Vector3.up;
                headUpHoriz.y = 0f;
                hmdFwdHoriz = (hmdUnscaledRot * Vector3.forward).y < 0f ? headUpHoriz : -headUpHoriz;
                if (hmdFwdHoriz.sqrMagnitude < 1e-4f) hmdFwdHoriz = Vector3.forward;
            }
            hmdFwdHoriz.Normalize();

            Quaternion bodyRot = Quaternion.LookRotation(hmdFwdHoriz, Vector3.up);
            Quaternion bodyRotInv = Quaternion.Inverse(bodyRot);
            Vector3 bodyOrigin = new Vector3(hmdUnscaledPos.x, floorY, hmdUnscaledPos.z);

            ConstellationDebug.EyeHeight = eyeHeight;
            ConstellationDebug.BodyOrigin = bodyOrigin;
            ConstellationDebug.BodyRotation = bodyRot;

            List<TrackerSample> samples = CollectFreeFbTrackerSamples(bodyOrigin, bodyRotInv, eyeHeight, hmdDevice);
            CaptureSampleSnapshots(samples);

            if (samples.Count == 0)
            {
                ConstellationDebug.Status = "no free FB-trackable devices found";
                ConstellationDebug.HasSnapshot = true;
                return;
            }

            float calibrationTolerance = GetCalibrationTolerance();

            // Map the live samples to the pure classifier's scalar inputs, resolving any user
            // role override per tracker. BasisConstellationClassifier owns the priors, scoring,
            // and greedy assignment (shared with the editor Tracker Placement Sweep); this method
            // just feeds it live device data and applies the roles it returns.
            int sampleCount = samples.Count;
            BasisConstellationSample[] classifierSamples = new BasisConstellationSample[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                TrackerSample ts = samples[i];
                int forcedRole = -1;
                if (ts.Input != null && !ts.NearOrigin && TryResolveOverride(ts.Input, out BasisBoneTrackedRole overrideRole))
                {
                    forcedRole = (int)overrideRole;
                }
                classifierSamples[i] = new BasisConstellationSample
                {
                    HeightRatio = ts.HeightRatio,
                    LateralRatio = ts.LateralRatio,
                    DepthLocal = ts.BodyLocal.z,
                    NearOrigin = ts.NearOrigin,
                    ForcedRole = forcedRole,
                };
            }

            BasisConstellationHand leftHand = ReadHandForClassifier(BasisBoneTrackedRole.LeftHand, bodyOrigin, bodyRotInv, eyeHeight);
            BasisConstellationHand rightHand = ReadHandForClassifier(BasisBoneTrackedRole.RightHand, bodyOrigin, bodyRotInv, eyeHeight);

            BasisConstellationResult classified = BasisConstellationClassifier.Classify(
                classifierSamples, sampleCount, leftHand, rightHand, calibrationTolerance,
                Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration);

            ConstellationDebug.ArmReach = classified.ArmReach;

            // Snapshot the full recentered prior set (pre enabled-filter) for the visualizer and
            // in-VR spheres, exactly as the old inline path did before the toggle filter.
            CapturePriorSnapshots(classified.Priors);

            bool anyRoleEnabled = false;
            for (int i = 0; i < classified.Priors.Length; i++)
            {
                if (Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(classified.Priors[i].Role))
                {
                    anyRoleEnabled = true;
                    break;
                }
            }
            if (!anyRoleEnabled)
            {
                ConstellationDebug.Status = "all FB roles disabled in calibration toggles";
                ComputeBestAnyFits(samples);
                ConstellationDebug.HasSnapshot = true;
                return;
            }

            // Apply the classifier's decisions. Devices were already unassigned in
            // CollectFreeFbTrackerSamples, so one ApplyTrackerCalibration per device lands the
            // final (post toe-swap) role; the bone controls are simulated by the caller next.
            for (int i = 0; i < sampleCount; i++)
            {
                if (!classified.TryGetRole(i, out BasisBoneTrackedRole role))
                {
                    continue;
                }
                BasisInput input = samples[i].Input;
                if (input == null)
                {
                    continue;
                }
                float score = classified.AssignedScore[i];
                BasisDebug.Log($"FBIK constellation: '{input.UniqueDeviceIdentifier}' -> {role} (h={samples[i].HeightRatio:F2}, lat={samples[i].LateralRatio:F2}, score={score:F2}, {classified.AssignedKind[i]})", BasisDebug.LogTag.Input);
                input.ApplyTrackerCalibration(role);
                HasFBIKTrackers = true;
                RecordAssignment(i, role, score);
            }

            ComputeBestAnyFits(samples);
            ConstellationDebug.Status = $"{classified.AssignedCount} of {samples.Count} tracker(s) assigned";
            ConstellationDebug.HasSnapshot = true;
        }

        /// <summary>
        /// Resolves a user-set override for the given input. Plain inputs use
        /// their own id. Virtual midpoints check both physical halves so an
        /// override saved against either tracker before pairing still applies.
        /// </summary>
        private static bool TryResolveOverride(BasisInput input, out BasisBoneTrackedRole role)
        {
            if (BasisTrackerRoleOverride.TryGetOverride(input.UniqueDeviceIdentifier, out role))
            {
                return true;
            }
            if (input is BasisVirtualMidpointInput midpoint)
            {
                if (midpoint.PartnerA != null
                    && BasisTrackerRoleOverride.TryGetOverride(midpoint.PartnerA.UniqueDeviceIdentifier, out role))
                {
                    return true;
                }
                if (midpoint.PartnerB != null
                    && BasisTrackerRoleOverride.TryGetOverride(midpoint.PartnerB.UniqueDeviceIdentifier, out role))
                {
                    return true;
                }
            }
            role = BasisBoneTrackedRole.CenterEye;
            return false;
        }

        private static void CaptureSampleSnapshots(List<TrackerSample> samples)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                TrackerSample s = samples[i];
                string id = s.Input != null ? s.Input.UniqueDeviceIdentifier : null;
                Vector3 raw = s.Input != null ? s.Input.UnscaledDeviceCoord.position : Vector3.zero;
                ConstellationDebug.Samples.Add(new ConstellationDebug.DebugSample
                {
                    DeviceId = string.IsNullOrEmpty(id) ? "(unknown)" : id,
                    BodyLocal = s.BodyLocal,
                    RawUnscaled = raw,
                    HeightRatio = s.HeightRatio,
                    LateralRatio = s.LateralRatio,
                    Assigned = false,
                    NearOrigin = s.NearOrigin,
                });
            }
        }

        private static void CapturePriorSnapshots(BasisConstellationPrior[] priors)
        {
            for (int i = 0; i < priors.Length; i++)
            {
                BasisConstellationPrior p = priors[i];
                ConstellationDebug.Priors.Add(new ConstellationDebug.DebugPrior
                {
                    Role = p.Role,
                    ExpectedHeight = p.ExpectedHeightRatio,
                    ExpectedLateral = p.ExpectedLateralRatio,
                    HeightSigma = p.HeightSigma,
                    LateralSigma = p.LateralSigma,
                    Enabled = Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(p.Role),
                    AssignedSampleIndex = -1,
                });
            }
        }

        private static void RecordAssignment(int sampleIdx, BasisBoneTrackedRole role, float score)
        {
            if (sampleIdx < 0 || sampleIdx >= ConstellationDebug.Samples.Count) return;
            ConstellationDebug.DebugSample ds = ConstellationDebug.Samples[sampleIdx];
            ds.Assigned = true;
            ds.AssignedRole = role;
            ds.AssignedScore = score;
            UpdatePriorAssignment(role, sampleIdx);
        }

        private static void UpdatePriorAssignment(BasisBoneTrackedRole role, int sampleIdx)
        {
            for (int p = 0; p < ConstellationDebug.Priors.Count; p++)
            {
                if (ConstellationDebug.Priors[p].Role == role)
                {
                    ConstellationDebug.Priors[p].AssignedSampleIndex = sampleIdx;
                    break;
                }
            }
        }

        private static void ComputeBestAnyFits(List<TrackerSample> samples)
        {
            // Best-scoring prior for each sample regardless of acceptance — lets the
            // visualizer answer "where would this tracker have gone if nothing else
            // were competing for that role?"
            for (int s = 0; s < samples.Count; s++)
            {
                if (s >= ConstellationDebug.Samples.Count) break;
                TrackerSample sample = samples[s];
                float best = float.NegativeInfinity;
                BasisBoneTrackedRole bestRole = BasisBoneTrackedRole.Hips;
                for (int p = 0; p < ConstellationDebug.Priors.Count; p++)
                {
                    ConstellationDebug.DebugPrior dp = ConstellationDebug.Priors[p];
                    float dh = (sample.HeightRatio - dp.ExpectedHeight) / dp.HeightSigma;
                    float dl = (sample.LateralRatio - dp.ExpectedLateral) / dp.LateralSigma;
                    float score = -(dh * dh + dl * dl);
                    if (score > best) { best = score; bestRole = dp.Role; }
                }
                ConstellationDebug.Samples[s].BestAnyRole = bestRole;
                ConstellationDebug.Samples[s].BestAnyScore = best;
            }
        }

        private static bool TryGetHmdPose(out Vector3 unscaledPos, out Quaternion unscaledRot, out BasisInput hmdDevice)
        {
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance.AllInputDevices;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (!input.TryGetRole(out BasisBoneTrackedRole role)) continue;
                if (role == BasisBoneTrackedRole.CenterEye || role == BasisBoneTrackedRole.Head)
                {
                    // UnscaledDeviceCoord only refreshes when LateDoPollData runs. If FullBodyCalibration
                    // is invoked outside the normal frame loop (UI button during Update, etc.) the cached
                    // value can be stale or zero — force a fresh poll before reading.
                    input.LatePollData();
                    unscaledPos = input.UnscaledDeviceCoord.position;
                    unscaledRot = input.UnscaledDeviceCoord.rotation;
                    hmdDevice = input;
                    return true;
                }
            }
            unscaledPos = Vector3.zero;
            unscaledRot = Quaternion.identity;
            hmdDevice = null;
            return false;
        }

        private static List<TrackerSample> CollectFreeFbTrackerSamples(Vector3 bodyOrigin, Quaternion bodyRotInv, float eyeHeight, BasisInput hmdDevice)
        {
            List<TrackerSample> samples = new List<TrackerSample>(16);
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance.AllInputDevices;
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = devices[Index];
                if (input == null)
                {
                    BasisDebug.LogError("Missing Input this should never occur!", BasisDebug.LogTag.IK);
                    continue;
                }
                // Screen-touch finger inputs aren't spatial trackers — they never populate
                // UnscaledDeviceCoord, so they'd only trip the near-origin diagnostic below.
                if (input is BasisTouchInputDevice) continue;
                // Never reassign the HMD itself — even if it has no role assigned, its
                // position would otherwise score against Chest and we'd happily glue the
                // headset to the player's torso.
                if (input == hmdDevice) continue;

                // Linked half of a tracker pair — the merged virtual midpoint device
                // emits the sample for the pair, so the physical bases bail out here
                // and never compete for a role on their own.
                if (input.IsLinked) continue;

                // Devices the matcher pinned to a role (HMD, named hand controllers) keep
                // their role no matter what.
                if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) continue;

                // Devices currently bound to a role that the user has not enabled for
                // calibration (e.g. controllers acting as hands, or shoulders by default)
                // are off-limits — only roles ticked in the bone editor participate.
                if (input.TryGetRole(out BasisBoneTrackedRole existing) && !Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(existing))
                {
                    continue;
                }

                // UnassignFBTrackers() at the top of FullBodyCalibration already cleared any
                // prior FB role; this is defensive in case a tracker came online late.
                input.UnAssignFullBodyTrackers();

                // Force a fresh poll so UnscaledDeviceCoord reflects the current device pose. A stale
                // (zero) read here would classify the tracker at HeightRatio ≈ 0 and pin it to a foot
                // role, dragging the avatar into the floor.
                input.LatePollData();
                Vector3 unscaledPos = input.UnscaledDeviceCoord.position;
                bool nearOrigin = unscaledPos.sqrMagnitude < ConstellationNearOriginEpsilonSqr;
                if (nearOrigin)
                {
                    string id = string.IsNullOrEmpty(input.UniqueDeviceIdentifier) ? "(unknown)" : input.UniqueDeviceIdentifier;
                    BasisDebug.LogError($"FBIK constellation: tracker '{id}' polled at world origin ({unscaledPos.x:F3},{unscaledPos.y:F3},{unscaledPos.z:F3}). UnscaledDeviceCoord likely never populated — check the device's LateDoPollData. This tracker will not classify into any role.", BasisDebug.LogTag.Input);
                }
                Vector3 local = bodyRotInv * (unscaledPos - bodyOrigin);
                samples.Add(new TrackerSample
                {
                    Input = input,
                    HeightRatio = local.y / eyeHeight,
                    LateralRatio = local.x / eyeHeight,
                    BodyLocal = local,
                    NearOrigin = nearOrigin,
                });
            }
            return samples;
        }

        /// <summary>
        /// User-facing FBIK calibration tolerance: a multiplier applied to every prior's
        /// sigma in <see cref="BasisConstellationClassifier.BuildPriors"/>. 1 = stock behavior (identical acceptance
        /// regions); higher widens every band for players whose proportions, tracker mounts,
        /// or calibration pose fall outside the typical envelope. Clamped so a maxed slider
        /// can't collapse role discrimination entirely.
        /// </summary>
        private static float GetCalibrationTolerance()
        {
            float t = Basis.BasisUI.BasisSettingsDefaults.CalibrationTolerance.RawValue;
            if (t < ConstellationMinCalibrationTolerance) t = ConstellationMinCalibrationTolerance;
            if (t > ConstellationMaxCalibrationTolerance) t = ConstellationMaxCalibrationTolerance;
            return t;
        }

        /// <summary>
        /// Finds the device currently bound to <paramref name="handRole"/> and returns its
        /// body-local height/lateral ratios in the same playspace frame the classifier uses
        /// (UnscaledDeviceCoord, normalized to eye height). Returns false when no such device
        /// exists or it polled at the world origin (a pose it never actually wrote).
        /// </summary>
        private static bool TryGetHandBodyLocalRatios(BasisBoneTrackedRole handRole, Vector3 bodyOrigin, Quaternion bodyRotInv, float eyeHeight, out float heightRatio, out float lateralRatio)
        {
            heightRatio = 0f;
            lateralRatio = 0f;

            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance.AllInputDevices;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (!input.TryGetRole(out BasisBoneTrackedRole role) || role != handRole) continue;

                // Same fresh-poll discipline as the HMD and free-tracker reads in this pass.
                input.LatePollData();
                Vector3 unscaledPos = input.UnscaledDeviceCoord.position;
                if (unscaledPos.sqrMagnitude < ConstellationNearOriginEpsilonSqr)
                {
                    return false; // hand never wrote a real pose — don't anchor the elbow to it
                }

                Vector3 local = bodyRotInv * (unscaledPos - bodyOrigin);
                heightRatio = local.y / eyeHeight;
                lateralRatio = local.x / eyeHeight;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the live hand controller pose for the constellation classifier (the elbow
        /// prior re-center), or returns <see cref="BasisConstellationHand.None"/> when the hand
        /// isn't present / polled at the origin.
        /// </summary>
        private static BasisConstellationHand ReadHandForClassifier(BasisBoneTrackedRole handRole, Vector3 bodyOrigin, Quaternion bodyRotInv, float eyeHeight)
        {
            if (TryGetHandBodyLocalRatios(handRole, bodyOrigin, bodyRotInv, eyeHeight, out float h, out float lat))
            {
                return new BasisConstellationHand { Present = true, HeightRatio = h, LateralRatio = lat };
            }
            return BasisConstellationHand.None;
        }

        // Anything closer than 1 cm from world (0,0,0) is treated as "the device never wrote
        // a real pose into UnscaledDeviceCoord". A real tracker basically never sits exactly
        // on the playspace origin, so this is a safe smoke-test threshold.
        private const float ConstellationNearOriginEpsilonSqr = 1e-4f;
        // Bounds for the user-facing calibration tolerance (a sigma multiplier).
        private const float ConstellationMinCalibrationTolerance = 1.0f;
        private const float ConstellationMaxCalibrationTolerance = 3.0f;
        /// <summary>
        /// gets a roles dictionary with the roles and transforms
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
        /// Per-role radius used for tracker debug gizmos (BasisLocalBoneDriver). The
        /// constellation classifier in FullBodyCalibration no longer consults these
        /// values — they survive only as visualization hints.
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

        // Cache for the default-armReach prior list. Rebuilt only when eyeHeight
        // moves enough to change the armReach scaling — avoids per-frame allocs
        // while still picking up calibration / scale changes.
        private static readonly List<ConstellationDebug.DebugPrior> _defaultPriorsCache = new List<ConstellationDebug.DebugPrior>(16);
        private static float _defaultPriorsArmReach = -1f;

        /// <summary>
        /// Returns the body-frame anchor and per-role priors used to render
        /// calibration acceptance-region gizmos. Always prefers a live HMD pose
        /// so the regions track wherever the player is now (rather than freezing
        /// at the last calibration snapshot). Falls back to the snapshot frame if
        /// no HMD device is available, or returns false if there's no usable
        /// frame source at all.
        /// </summary>
        /// <param name="bodyOrigin">World-space anchor: HMD position projected to the floor.</param>
        /// <param name="bodyRotation">Body forward rotation: HMD facing flattened to horizontal.</param>
        /// <param name="eyeHeight">Eye height (meters), used to convert ratio-space priors to world distances.</param>
        /// <param name="priors">Per-role priors. Snapshot priors when a calibration has run, default-armReach priors otherwise.</param>
        public static bool TryGetCalibrationVisualizationFrame(
            out Vector3 bodyOrigin,
            out Quaternion bodyRotation,
            out float eyeHeight,
            out IReadOnlyList<ConstellationDebug.DebugPrior> priors)
        {
            // The classifier scores in playspace (real-world) ratios of
            // PlayerEyeHeight. After it picks roles, each tracker pose is
            // lifted onto the avatar via DeviceScale (BasisInput.ConvertToScaledDeviceCoord:
            // scaledPos = OffsetCoords + unscaledPos * DeviceScale). The
            // visualization mirrors that pipeline: priors stay cached against
            // PlayerEyeHeight (real-world), but body anchor + region sizing
            // get the same DeviceScale projection real trackers get post-
            // classification. Without the multiply, regions sit at the player's
            // real-world position (e.g. ~0.09 m world) instead of on the avatar.
            float playerEye = Mathf.Max(BasisHeightDriver.PlayerEyeHeight, 1.0f);
            float deviceScale = BasisHeightDriver.DeviceScale;
            if (deviceScale <= 0f) deviceScale = 1f;
            float worldEyeHeight = playerEye * deviceScale;

            IReadOnlyList<ConstellationDebug.DebugPrior> resolvedPriors =
                (ConstellationDebug.HasSnapshot && ConstellationDebug.Priors.Count > 0)
                    ? (IReadOnlyList<ConstellationDebug.DebugPrior>)ConstellationDebug.Priors
                    : BuildDefaultPriorsList(playerEye);

            // Anchor on the HMD pose. Read passively from UnscaledDeviceCoord —
            // we deliberately do NOT call LatePollData because that would
            // re-run the device poll mid-render and stomp on height calibration
            // (and anything else reading device state in the same frame).
            // The cached value reflects the most recent normal frame poll, which
            // is plenty fresh for visualization.
            if (TryReadHmdPosePassive(out Vector3 hmdPosPlayspace, out Quaternion hmdRotPlayspace))
            {
                Vector3 fwd = hmdRotPlayspace * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-4f)
                {
                    fwd = Vector3.forward;
                }
                fwd.Normalize();
                Quaternion bodyRotPlayspace = Quaternion.LookRotation(fwd, Vector3.up);

                // Lift HMD playspace → avatar-world. Multiplying by DeviceScale
                // before the locomotion matrix matches BasisInput.ConvertToScaledDeviceCoord
                // (scaledPos = OffsetCoords + unscaledPos * DeviceScale). The
                // matrix carries position + rotation from teleport / smooth-move;
                // its lossyScale stays 1 because avatar scale lives on the
                // avatar transform inside the root, applied here via DeviceScale.
                Matrix4x4 l2w = BasisLocalPlayer.localToWorldMatrix;
                Vector3 hmdWorld = l2w.MultiplyPoint3x4(hmdPosPlayspace * deviceScale);
                bodyRotation = l2w.rotation * bodyRotPlayspace;
                bodyOrigin = new Vector3(hmdWorld.x, hmdWorld.y - worldEyeHeight, hmdWorld.z);
                eyeHeight = worldEyeHeight;
                priors = resolvedPriors;
                return true;
            }

            // Last-ditch: snapshot frame lifted to world for the case where
            // HMD pose isn't available (e.g., before device discovery).
            if (ConstellationDebug.HasSnapshot)
            {
                Matrix4x4 l2w = BasisLocalPlayer.localToWorldMatrix;
                bodyOrigin = l2w.MultiplyPoint3x4(ConstellationDebug.BodyOrigin * deviceScale);
                bodyRotation = l2w.rotation * ConstellationDebug.BodyRotation;
                eyeHeight = worldEyeHeight;
                priors = ConstellationDebug.Priors;
                return true;
            }

            bodyOrigin = Vector3.zero;
            bodyRotation = Quaternion.identity;
            eyeHeight = 1f;
            priors = null;
            return false;
        }

        /// <summary>
        /// Passive HMD pose read — same lookup as <see cref="TryGetHmdPose"/>
        /// but without the <c>LatePollData()</c> call. Safe to invoke from
        /// per-frame render paths since it never re-runs the device poll.
        /// </summary>
        private static bool TryReadHmdPosePassive(out Vector3 unscaledPos, out Quaternion unscaledRot)
        {
            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                unscaledPos = Vector3.zero;
                unscaledRot = Quaternion.identity;
                return false;
            }

            BasisObservableList<BasisInput> devices = manager.AllInputDevices;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (!input.TryGetRole(out BasisBoneTrackedRole role)) continue;
                if (role == BasisBoneTrackedRole.CenterEye || role == BasisBoneTrackedRole.Head)
                {
                    unscaledPos = input.UnscaledDeviceCoord.position;
                    unscaledRot = input.UnscaledDeviceCoord.rotation;
                    return true;
                }
            }

            unscaledPos = Vector3.zero;
            unscaledRot = Quaternion.identity;
            return false;
        }

        private static IReadOnlyList<ConstellationDebug.DebugPrior> BuildDefaultPriorsList(float eyeHeight)
        {
            float armReach = BasisConstellationClassifier.DefaultArmReachRatio * eyeHeight;
            // Re-build only when armReach moves enough to matter — guards against
            // every-frame allocations from the per-frame DrawGizmos path.
            if (_defaultPriorsArmReach < 0f || Mathf.Abs(armReach - _defaultPriorsArmReach) > 0.001f)
            {
                _defaultPriorsCache.Clear();
                BasisConstellationPrior[] built = BasisConstellationClassifier.BuildPriors(armReach, BasisConstellationClassifier.DefaultStanceRatio * eyeHeight, GetCalibrationTolerance());
                for (int i = 0; i < built.Length; i++)
                {
                    BasisConstellationPrior p = built[i];
                    _defaultPriorsCache.Add(new ConstellationDebug.DebugPrior
                    {
                        Role = p.Role,
                        ExpectedHeight = p.ExpectedHeightRatio,
                        ExpectedLateral = p.ExpectedLateralRatio,
                        HeightSigma = p.HeightSigma,
                        LateralSigma = p.LateralSigma,
                        Enabled = Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(p.Role),
                        AssignedSampleIndex = -1,
                    });
                }
                _defaultPriorsArmReach = armReach;
            }
            return _defaultPriorsCache;
        }

        /// <summary>
        /// Legacy ordered role list from the radius-based matcher. The constellation
        /// classifier no longer reads this — kept public for external consumers that
        /// still rely on it as a "trackable FB roles" enumeration.
        /// </summary>
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

            // Reference rotations of the T-posed chest/hips: derived from the head anchor + the
            // load-time bind snapshot (pose-independent), falling back to the live bones — which are
            // only valid when the avatar is physically T-posed (the legacy no-snapshot path).
            Quaternion chestRefRot = Quaternion.identity;
            Quaternion hipsRefRot = Quaternion.identity;

            BasisLocalBoneControl headAnchorControl = BasisLocalBoneDriver.HeadControl;
            if (BasisLocalAvatarDriver.HasTposeBoneSnapshot && headAnchorControl != null)
            {
                var headWorld = headAnchorControl.OutgoingWorldData;
                BasisCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headAnchorControl.TposeLocalScaled.position, out _, out Quaternion anchorRot);
                if (BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.Chest, out var chestBind))
                {
                    chestRefRot = anchorRot * chestBind.rotation;
                }
                if (BasisLocalAvatarDriver.TposeBoneSnapshot.TryGetValue(BasisBoneTrackedRole.Hips, out var hipsBind))
                {
                    hipsRefRot = anchorRot * hipsBind.rotation;
                }
            }
            else
            {
                if (storedRoleTransforms.TryGetValue(BasisBoneTrackedRole.Chest, out var chestT) && chestT != null)
                {
                    chestRefRot = chestT.rotation;
                }

                if (storedRoleTransforms.TryGetValue(BasisBoneTrackedRole.Hips, out var hipsT) && hipsT != null)
                {
                    hipsRefRot = hipsT.rotation;
                }
            }

            // Push magnitudes are world metres, so they must scale with the avatar's RENDERED size.
            // ScaledToMatchValue is only the authored->target ratio (1.0 for any unscaled avatar, and
            // inversely proportional to the authored size when custom scale is on), so authored units
            // leaked into the push: a 0.5m-authored avatar scaled to 1.6m got a ~33cm knee hint.
            float calibrationScaleY = 1f;
            BasisLocalAvatarDriver hintAvatarDriver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalAvatarDriver : null;
            if (hintAvatarDriver != null && hintAvatarDriver.ScaleAvatarModification != null)
            {
                calibrationScaleY = hintAvatarDriver.ScaleAvatarModification.DuringCalibrationScale.y;
            }
            if (float.IsNaN(calibrationScaleY) || float.IsInfinity(calibrationScaleY) || calibrationScaleY <= 0f)
            {
                calibrationScaleY = 1f;
            }
            float renderedEyeHeight = calibrationScaleY * BasisHeightDriver.AvatarEyeHeight * BasisHeightDriver.AppliedUpScale;
            float hs = (float.IsNaN(renderedEyeHeight) || float.IsInfinity(renderedEyeHeight) || renderedEyeHeight <= 0f)
                ? 1f
                : renderedEyeHeight / BasisHeightDriver.FallbackHeightInMeters;
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

                    if (lll.HasTracked == BasisHasTracked.HasTracker)
                    {
                        BasisBendNormalStore.Set(BasisBoneTrackedRole.LeftLowerLeg,
                            Basis.IK.BasisTrackerBendNormalCore.CaptureLocalAxis(trackerRot, hipsRefRot * Vector3.right));

                        CaptureLimbRoll(storedRoleTransforms, BasisBoneTrackedRole.LeftLowerLeg, trackerRot);
                    }
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

                    if (rll.HasTracked == BasisHasTracked.HasTracker)
                    {
                        BasisBendNormalStore.Set(BasisBoneTrackedRole.RightLowerLeg,
                            Basis.IK.BasisTrackerBendNormalCore.CaptureLocalAxis(trackerRot, hipsRefRot * Vector3.right));

                        CaptureLimbRoll(storedRoleTransforms, BasisBoneTrackedRole.RightLowerLeg, trackerRot);
                    }
                }
            }
            // NOTE: no elbow (lower-arm) hint bias baked here on purpose. A tracker-local offset swings with
            // forearm pronation and pops the elbow (the knees can keep theirs because the knee is a hinge).
            // Elbow-tracker conditioning is handled solver-side (BasisArmSolveCore HintIsTracker trusts a real
            // tracker further before the down-stabilizer overrides), not by a tracker-local offset.
            //
            // A ROLL REFERENCE is a different thing and IS baked, for the same reason the lower legs bake one:
            // BasisArmSolveCore's tracker forearm roll compares the tracker's rotation against the solved
            // forearm, and an elbow strap's clock angle around the limb is arbitrary. Without this the strap
            // angle lands as a constant forearm twist. This stores only a rotation reference -- it does not
            // move the elbow pole, so the note above still holds.
            {
                var lla = BasisLocalBoneDriver.LeftLowerArmControl;
                if (lla != null && lla.HasTracked == BasisHasTracked.HasTracker)
                {
                    CaptureLimbRoll(storedRoleTransforms, BasisBoneTrackedRole.LeftLowerArm, lla.OutgoingWorldData.rotation);
                }

                var rla = BasisLocalBoneDriver.RightLowerArmControl;
                if (rla != null && rla.HasTracked == BasisHasTracked.HasTracker)
                {
                    CaptureLimbRoll(storedRoleTransforms, BasisBoneTrackedRole.RightLowerArm, rla.OutgoingWorldData.rotation);
                }
            }
        }

        /// <summary>
        /// Store <c>Inverse(trackerWorld) * boneWorld</c> for a limb role, so the live path can recover the
        /// tracker-implied BONE rotation as <c>trackerWorldLive * reference</c>. A strap is rigid on the limb,
        /// so the reference is exactly the inverse of the strap offset and cancels it for any mounting angle.
        /// Roles with no entry leave the consumer's zero-quaternion "feature off" sentinel in place.
        /// </summary>
        static void CaptureLimbRoll(Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms,
                                    BasisBoneTrackedRole role, Quaternion trackerWorldRot)
        {
            if (storedRoleTransforms.TryGetValue(role, out Transform bone) && bone != null)
            {
                BasisLimbRollStore.Set(role, Quaternion.Inverse(trackerWorldRot) * bone.rotation);
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
    }
}
