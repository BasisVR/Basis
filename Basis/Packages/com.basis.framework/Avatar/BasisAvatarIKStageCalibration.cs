using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;
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

            public static float AcceptThreshold => ConstellationAcceptThreshold;

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

        private readonly struct BoneRolePrior
        {
            public readonly BasisBoneTrackedRole Role;
            public readonly float ExpectedHeightRatio;
            public readonly float ExpectedLateralRatio;
            public readonly float HeightSigma;
            public readonly float LateralSigma;

            public BoneRolePrior(BasisBoneTrackedRole role, float h, float lat, float hSigma, float latSigma)
            {
                Role = role;
                ExpectedHeightRatio = h;
                ExpectedLateralRatio = lat;
                HeightSigma = hSigma;
                LateralSigma = latSigma;
            }
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
        /// Builds a tracker→role assignment from the player's T-pose constellation alone.
        /// The avatar is no longer the source of truth for "where should this tracker be";
        /// instead the HMD defines a body frame and each tracker is classified by its
        /// height-above-floor and lateral offset, normalized to the calibrated player eye
        /// height. ComputeHints below still consults the avatar (chest/hips reference
        /// rotations), but the role-matching pass itself is avatar-independent — so the
        /// same trackers map the same way whether the user wears a child avatar or a
        /// three-meter giant.
        /// </summary>
        public static void FullBodyCalibration()
        {
            BasisHeightDriver.OnAvatarFBCalibration();//avatar height is good,player height is needed
            HasFBIKTrackers = false;
            BasisHintBiasStore.Clear();
            BasisDeviceManagement.UnassignFBTrackers();
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            // Avatar still goes into T-pose because ComputeHints reads chest/hips reference
            // rotations from it. The classifier itself doesn't touch the avatar.
            BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
            BasisLocalPlayer.Instance.DriveTpose();

            Dictionary<BasisBoneTrackedRole, Transform> storedRoleTransforms = BasisLocalPlayer.Instance.LocalAvatarDriver.StoredRolesTransforms;

            ClassifyAndAssignTrackersFromTPose();

            // IMPORTANT: simulate once AFTER assignments so the bone controls reflect new tracker bindings.
            BasisLocalPlayer.Instance.LocalBoneDriver.SimulateAndApplyWithoutLerp(BasisLocalPlayer.Instance);

            ComputeHints(storedRoleTransforms);

            BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
            BasisLocalPlayer.Instance.LocalRigDriver.RigLayer.active = true;
            BasisLocalPlayer.Instance.LocalAnimatorDriver.AssignHipsFBTracker();

            // Refresh the per-role calibration spheres so they re-anchor to the
            // newly stored avatar bone transforms. No-op when the ShowGizmos
            // master toggle is off; the toggle path rebuilds when it flips on.
            BasisLocalPlayer.Instance.LocalBoneDriver.RebuildCalibrationSpheres();
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
            if (hmdFwdHoriz.sqrMagnitude < 1e-4f) hmdFwdHoriz = BasisLocalPlayer.Instance.transform.forward;
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

            float armReach = EstimateArmReach(samples);
            ConstellationDebug.ArmReach = armReach;

            BoneRolePrior[] priors = BuildPriors(armReach);
            CapturePriorSnapshots(priors);

            // Honor per-role calibration toggles from the body-tracking settings UI.
            // Roles with their toggle off are dropped from the prior list so the
            // classifier never attempts to bind a tracker to them.
            int kept = 0;
            for (int i = 0; i < priors.Length; i++)
            {
                if (Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(priors[i].Role))
                {
                    priors[kept++] = priors[i];
                }
            }
            if (kept != priors.Length)
            {
                System.Array.Resize(ref priors, kept);
            }
            if (priors.Length == 0)
            {
                ConstellationDebug.Status = "all FB roles disabled in calibration toggles";
                ComputeBestAnyFits(samples);
                ConstellationDebug.HasSnapshot = true;
                return;
            }

            // Greedy global-best assignment: each iteration picks the (sample, role) pair
            // with the highest score that still beats the threshold. One tracker per role,
            // one role per tracker. Trackers that don't fit any role are left unassigned.
            bool[] sampleUsed = new bool[samples.Count];
            bool[] roleUsed = new bool[priors.Length];
            int assignedCount = 0;

            // User-forced overrides come first. Each sample whose tracker (or, for a
            // virtual midpoint, either physical half) has a stored role override is
            // bound straight to that role and removed from the candidate pool — the
            // classifier never scores it. This lets a user lock e.g. a problem hip
            // tracker to Hips so calibration can't reassign it under any pose.
            assignedCount += ApplyForcedRoleOverrides(samples, priors, sampleUsed, roleUsed);
            while (true)
            {
                float bestScore = ConstellationAcceptThreshold;
                int bestSampleIdx = -1;
                int bestRoleIdx = -1;
                for (int s = 0; s < samples.Count; s++)
                {
                    if (sampleUsed[s])
                    {
                        continue;
                    }

                    TrackerSample sample = samples[s];
                    // A near-origin sample means the device never wrote a real pose into
                    // UnscaledDeviceCoord (touch-only inputs, controllers polled before the
                    // first render pose, etc.). At (0,0,0) body-local it scores well for
                    // Toes and would silently win that role, flipping HasFBIKTrackers to
                    // true and tricking the rig driver into engaging foot IK on legs that
                    // have no real tracker.
                    if (sample.NearOrigin)
                    {
                        continue;
                    }
                    for (int r = 0; r < priors.Length; r++)
                    {
                        if (roleUsed[r])
                        {
                            continue;
                        }

                        // Secondary roles only release once their default counterpart is filled.
                        // Hips anchors the torso/legs — Chest and LowerLeg wait on it so a stray
                        // stomach or thigh tracker doesn't snipe its slot. Toes waits on Foot —
                        // otherwise a single low tracker per side wins Toes and leaves
                        // FootControl.HasTracked false, which kicks BasisLocalRigDriver into
                        // procedural foot IK. Shoulder waits on LowerArm so the default elbow
                        // tracker isn't lost to the tighter-to-the-body Shoulder prior.
                        BasisBoneTrackedRole priorrole = priors[r].Role;
                        if (priorrole == BasisBoneTrackedRole.Chest
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.Hips))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.LeftLowerLeg
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.Hips))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.RightLowerLeg
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.Hips))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.LeftToes
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.LeftFoot))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.RightToes
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.RightFoot))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.LeftShoulder
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.LeftLowerArm))
                        {
                            continue;
                        }
                        if (priorrole == BasisBoneTrackedRole.RightShoulder
                            && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.RightLowerArm))
                        {
                            continue;
                        }

                        float score = ScoreSampleAgainstRole(sample, priors[r]);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSampleIdx = s;
                            bestRoleIdx = r;
                        }
                    }
                }
                if (bestSampleIdx < 0)
                {
                    break;
                }

                BasisBoneTrackedRole role = priors[bestRoleIdx].Role;
                TrackerSample chosen = samples[bestSampleIdx];
                BasisDebug.Log($"FBIK constellation: '{chosen.Input.UniqueDeviceIdentifier}' -> {role} (h={chosen.HeightRatio:F2}, lat={chosen.LateralRatio:F2}, score={bestScore:F2})", BasisDebug.LogTag.Input);
                chosen.Input.ApplyTrackerCalibration(role);
                sampleUsed[bestSampleIdx] = true;
                roleUsed[bestRoleIdx] = true;
                HasFBIKTrackers = true;

                RecordAssignment(bestSampleIdx, role, bestScore);
                assignedCount++;
            }

            ComputeBestAnyFits(samples);
            ConstellationDebug.Status = $"{assignedCount} of {samples.Count} tracker(s) assigned";
            ConstellationDebug.HasSnapshot = true;
        }

        /// <summary>
        /// Walks samples, applies any user-set role override for each tracker
        /// directly (skipping the constellation scoring), and marks both the
        /// sample and the chosen role as used so the greedy classifier can't
        /// touch them. For virtual midpoint samples we also accept an override
        /// recorded against either physical half — the user pairs trackers in
        /// the UI by physical id, and the merged virtual is what calibration
        /// sees, so we have to bridge between the two ids here.
        /// </summary>
        private static int ApplyForcedRoleOverrides(List<TrackerSample> samples, BoneRolePrior[] priors, bool[] sampleUsed, bool[] roleUsed)
        {
            int forced = 0;
            for (int s = 0; s < samples.Count; s++)
            {
                if (sampleUsed[s]) continue;
                TrackerSample sample = samples[s];
                if (sample.Input == null) continue;
                if (sample.NearOrigin) continue;

                if (!TryResolveOverride(sample.Input, out BasisBoneTrackedRole forcedRole))
                {
                    continue;
                }

                int roleIdx = -1;
                for (int r = 0; r < priors.Length; r++)
                {
                    if (priors[r].Role == forcedRole)
                    {
                        roleIdx = r;
                        break;
                    }
                }
                // Override targets a role the classifier wouldn't normally
                // assign (toggled off, or off-list like Spine). The user
                // explicitly asked for it, so honor the override and just skip
                // the roleUsed bookkeeping — the classifier was never going to
                // pick that role anyway.
                if (roleIdx >= 0 && roleUsed[roleIdx])
                {
                    BasisDebug.Log($"FBIK constellation: override for '{sample.Input.UniqueDeviceIdentifier}' -> {forcedRole} skipped (role already taken by another override)", BasisDebug.LogTag.Input);
                    continue;
                }

                BasisDebug.Log($"FBIK constellation: '{sample.Input.UniqueDeviceIdentifier}' -> {forcedRole} (forced override)", BasisDebug.LogTag.Input);
                sample.Input.ApplyTrackerCalibration(forcedRole);
                sampleUsed[s] = true;
                if (roleIdx >= 0) roleUsed[roleIdx] = true;
                HasFBIKTrackers = true;

                // ConstellationDebug records the assignment with score=+inf to
                // mark "this didn't go through scoring". Negative scores in the
                // visualizer mean "tight fit"; +inf reads as "no fit needed".
                RecordAssignment(s, forcedRole, float.PositiveInfinity);
                forced++;
            }
            return forced;
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

        private static void CapturePriorSnapshots(BoneRolePrior[] priors)
        {
            for (int i = 0; i < priors.Length; i++)
            {
                BoneRolePrior p = priors[i];
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
        /// Returns the largest absolute lateral ratio among arm-height trackers, or a
        /// typical-adult fallback. Adapts shoulder/elbow priors to the player's actual
        /// arm length so the same code works for kids and tall adults.
        /// </summary>
        private static float EstimateArmReach(List<TrackerSample> samples)
        {
            float maxAbs = 0f;
            int n = samples.Count;
            for (int i = 0; i < n; i++)
            {
                TrackerSample s = samples[i];
                if (s.HeightRatio < ConstellationArmHeightFloor) continue;
                float lAbs = Mathf.Abs(s.LateralRatio);
                if (lAbs < ConstellationArmLateralFloor) continue;
                if (lAbs > maxAbs) maxAbs = lAbs;
            }
            return maxAbs > ConstellationArmLateralFloor ? maxAbs : ConstellationDefaultArmReachRatio;
        }

        private static BoneRolePrior[] BuildPriors(float armReach)
        {
            // Heights are fractions of player eye height. Lateral is signed — negative is
            // the body's left. Sigmas control how forgiving each axis is; bigger sigma
            // means more permissive.
            //
            // Toes sit slightly closer to the floor than a foot-strap tracker (which mounts on
            // top of the shoe / over the laces). Tighter height sigma keeps a foot-strap tracker
            // at h≈0.05 firmly on Foot, while a tracker on the toe at h≈0.02 wins Toes. Greedy
            // global-best handles the disambiguation when both trackers exist on the same side.
            return new BoneRolePrior[]
            {
                // Centered torso bones — height is the discriminator.
                new BoneRolePrior(BasisBoneTrackedRole.Hips,           h: 0.55f, lat: 0f,                 hSigma: 0.10f, latSigma: 0.10f),
                new BoneRolePrior(BasisBoneTrackedRole.Chest,          h: 0.78f, lat: 0f,                 hSigma: 0.08f, latSigma: 0.10f),

                // Legs — toes near floor, feet just above, knees mid-shin.
                new BoneRolePrior(BasisBoneTrackedRole.LeftToes,       h: 0.02f, lat: -0.10f,             hSigma: 0.04f, latSigma: 0.07f),
                new BoneRolePrior(BasisBoneTrackedRole.RightToes,      h: 0.02f, lat: +0.10f,             hSigma: 0.04f, latSigma: 0.07f),
                new BoneRolePrior(BasisBoneTrackedRole.LeftFoot,       h: 0.05f, lat: -0.10f,             hSigma: 0.08f, latSigma: 0.07f),
                new BoneRolePrior(BasisBoneTrackedRole.RightFoot,      h: 0.05f, lat: +0.10f,             hSigma: 0.08f, latSigma: 0.07f),
                new BoneRolePrior(BasisBoneTrackedRole.LeftLowerLeg,   h: 0.27f, lat: -0.10f,             hSigma: 0.10f, latSigma: 0.07f),
                new BoneRolePrior(BasisBoneTrackedRole.RightLowerLeg,  h: 0.27f, lat: +0.10f,             hSigma: 0.10f, latSigma: 0.07f),

                // Arms in T-pose share approximate height; lateral position discriminates
                // shoulder vs elbow vs (the implied hand controller out past the elbow).
                // Lateral priors scale with measured reach so this works for any arm length.
                //
                // Lateral sigma is also scaled by armReach (×0.08) so the 3σ acceptance
                // band can never cross the body midline. Shoulder/lower-arm trackers are
                // physically anchored to one side of the body — letting them score
                // against the opposite-side prior is just noise. Concretely with
                // ×0.08: shoulder 3σ extent = armReach × [0.06, 0.54], lower-arm 3σ
                // extent = armReach × [0.41, 0.89] — both clear of the midline for
                // any armReach value.
                new BoneRolePrior(BasisBoneTrackedRole.LeftShoulder,   h: 0.88f, lat: -armReach * 0.30f,  hSigma: 0.08f, latSigma: armReach * 0.08f),
                new BoneRolePrior(BasisBoneTrackedRole.RightShoulder,  h: 0.88f, lat: +armReach * 0.30f,  hSigma: 0.08f, latSigma: armReach * 0.08f),
                new BoneRolePrior(BasisBoneTrackedRole.LeftLowerArm,   h: 0.88f, lat: -armReach * 0.65f,  hSigma: 0.10f, latSigma: armReach * 0.12f),
                new BoneRolePrior(BasisBoneTrackedRole.RightLowerArm,  h: 0.88f, lat: +armReach * 0.65f,  hSigma: 0.10f, latSigma: armReach * 0.12f),
            };
        }

        // Returns true when dependsOn is already taken, or isn't in the prior list at all
        // (the toggle disabled it, so there's nothing for the dependent role to wait on).
        private static bool IsRolePreconditionMet(BoneRolePrior[] priors, bool[] roleUsed, BasisBoneTrackedRole dependsOn)
        {
            for (int i = 0; i < priors.Length; i++)
            {
                if (priors[i].Role == dependsOn)
                {
                    return roleUsed[i];
                }
            }
            return true;
        }

        private static float ScoreSampleAgainstRole(TrackerSample sample, BoneRolePrior prior)
        {
            float dh = (sample.HeightRatio - prior.ExpectedHeightRatio) / prior.HeightSigma;
            float dl = (sample.LateralRatio - prior.ExpectedLateralRatio) / prior.LateralSigma;
            return -(dh * dh + dl * dl);
        }

        // -9 ≈ a combined 3-sigma fit. A tracker that can't beat this against any role is
        // left unassigned rather than forced into a bad slot.
        private const float ConstellationAcceptThreshold = -9f;
        private const float ConstellationArmHeightFloor = 0.65f;
        private const float ConstellationArmLateralFloor = 0.20f;
        // Anything closer than 1 cm from world (0,0,0) is treated as "the device never wrote
        // a real pose into UnscaledDeviceCoord". A real tracker basically never sits exactly
        // on the playspace origin, so this is a safe smoke-test threshold.
        private const float ConstellationNearOriginEpsilonSqr = 1e-4f;
        // Half arm-span as a fraction of eye height for a typical adult — used as a fallback
        // when no arm-height tracker is present to measure the player's own reach.
        private const float ConstellationDefaultArmReachRatio = 0.55f;
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
            float armReach = ConstellationDefaultArmReachRatio * eyeHeight;
            // Re-build only when armReach moves enough to matter — guards against
            // every-frame allocations from the per-frame DrawGizmos path.
            if (_defaultPriorsArmReach < 0f || Mathf.Abs(armReach - _defaultPriorsArmReach) > 0.001f)
            {
                _defaultPriorsCache.Clear();
                BoneRolePrior[] built = BuildPriors(armReach);
                for (int i = 0; i < built.Length; i++)
                {
                    BoneRolePrior p = built[i];
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
    }
}
