using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;
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
        /// Minimum cutoff for the One Euro filter. Lower = smoother; higher = more responsive.
        /// </summary>
        [Header("Smoothing (One Euro Filter)")]
        [Tooltip("Lower = more smoothing; Higher = more responsive.")]
        [Range(0.01f, 10f)]
        public float MinCutoff = 5.5f;

        /// <summary>
        /// Beta term for the One Euro filter: raises cutoff during fast motion to reduce lag.
        /// </summary>
        [Tooltip("How much to raise cutoff when motion is fast (reduces lag during quick moves).")]
        [Range(0f, 10f)]
        public float Beta = 3.25f;

        /// <summary>
        /// Cutoff for derivative smoothing in the One Euro filter.
        /// </summary>
        [Tooltip("Cutoff for derivative smoothing.")]
        [Range(0.01f, 10f)]
        public float DerivativeCutoff = 3f;
        /// <summary>Left hand two-bone IK (hand variant).</summary>
        public BasisTwoBoneIKConstraintHand LeftHandTwoBoneIK;
        /// <summary>Right hand two-bone IK (hand variant).</summary>
        public BasisTwoBoneIKConstraintHand RightHandTwoBoneIK;

        /// <summary>Left toe translation/rotation damper.</summary>
        public BasisApplyTranslation LeftToeConstraint;
        /// <summary>Right toe translation/rotation damper.</summary>
        public BasisApplyTranslation RightToeConstraint;

        /// <summary>Left toe rig.</summary>
        public Rig LeftToeRig;
        /// <summary>Right toe rig.</summary>
        public Rig RightToeRig;
        /// <summary>Left hand rig group.</summary>
        public Rig LeftHandRig;
        /// <summary>Right hand rig group.</summary>
        public Rig RightHandRig;

        /// <summary>Layer controlling left hand rig.</summary>
        public RigLayer LeftHandLayer;
        /// <summary>Layer controlling right hand rig.</summary>
        public RigLayer RightHandLayer;
        /// <summary>Layer controlling left foot rig.</summary>
        public RigLayer LeftFootLayer;
        /// <summary>Layer controlling right foot rig.</summary>
        public RigLayer RightFootLayer;
        /// <summary>Layer controlling left toe rig.</summary>
        public RigLayer LeftToeLayer;
        /// <summary>Layer controlling right toe rig.</summary>
        public RigLayer RightToeLayer;
        /// <summary>RigBuilder used to manage layers and build the playable graph.</summary>
        public RigBuilder Builder;
        /// <summary>Additional transforms to register with the rig (if needed).</summary>
        public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
        /// <summary>PlayableGraph used for manual rig evaluation.</summary>
        public PlayableGraph PlayableGraph;

        /// <summary>Owning local player instance.</summary>
        private BasisLocalPlayer localPlayer;
        /// <summary>Bone reference mapping (hips, chest, hands, etc.).</summary>
        private BasisTransformMapping references;

        /// <summary>Position filters per tracked role (One Euro).</summary>
        private readonly Dictionary<BasisBoneTrackedRole, OneEuroFilterVector3> posFilters = new();

        /// <summary>Monotonic time accumulator for filter evaluation.</summary>
        private float _timeAccumulator;

        public HumanBodyBones[] HumanBones;
        public bool[] isActive;
        public Rig ConstraintsRig;
        public RigLayer ConstraintsLayer;

        // NEW: batched constraints and slot mapping
        public BasisIK23Constraint[] Batches;

        private BasisConstraintSlotIndex[] _boneToSlot; // size = (int)HumanBodyBones.LastBone
        /// <summary>
        /// Fetches or creates a One Euro position filter for a specific role
        /// and keeps its parameters in sync with the public fields.
        /// </summary>
        private OneEuroFilterVector3 GetPosFilter(BasisBoneTrackedRole role)
        {
            if (!posFilters.TryGetValue(role, out var f))
            {
                f = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
                posFilters[role] = f;
            }
            else
            {
                // keep runtime params in sync if adjusted at runtime
                f.minCutoff = MinCutoff; f.beta = Beta; f.dCutoff = DerivativeCutoff;
            }
            return f;
        }

        /// <summary>
        /// Initializes the rig driver with a local player and bone references.
        /// </summary>
        /// <param name="localPlayer">Local player providing animator and scale context.</param>
        /// <param name="references">Captured bone references for rig construction.</param>
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            this.references = references;
            _timeAccumulator = 0f;
        }

        /// <summary>
        /// Updates IK targets and hints, applies One Euro filtering (hooks left in place but commented),
        /// and manually evaluates the rig playable graph for the given delta time.
        /// </summary>
        /// <param name="DeltaTime">Simulation delta time.</param>
        public void SimulateIKDestinations(float DeltaTime)
        {
            _timeAccumulator += Mathf.Max(DeltaTime, 1e-6f);

            // --- IK Target ---
            // Spine (hips + head targets come from calibrated coords)
            var hipsCoords = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

            var hipsPos = GetPosFilter(BasisBoneTrackedRole.Hips).Filter(hipsCoords.position, _timeAccumulator);

            BasisFullIKConstraint.data.TargetPositionHips = hipsPos;
            BasisFullIKConstraint.data.TargetRotationEulerHips = hipsCoords.rotation;

            // Direction for knee/neck hints relative to hips orientation (unchanged)
            Vector3 Direction = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation * Vector3.right;
            BasisFullIKConstraint.data.m_HintDirection = Direction;

            var data = GetCoordsForRole(BasisBoneTrackedRole.Head);
            BasisFullIKConstraint.data.TargetPositionHead = data.position;
            BasisFullIKConstraint.data.TargetRotationHead = data.rotation;

            data = GetCoordsForRole(BasisBoneTrackedRole.LeftFoot);
            BasisFullIKConstraint.data.TargetPositionLeftLowerLeg = data.position;
            BasisFullIKConstraint.data.TargetRotationLeftLowerLeg = data.rotation;

            data = GetCoordsForRole(BasisBoneTrackedRole.RightFoot);
            BasisFullIKConstraint.data.TargetPositionRightLowerLeg = data.position;
            BasisFullIKConstraint.data.TargetRotationRightLowerLeg = data.rotation;

            data = GetCoordsForRole(BasisBoneTrackedRole.Chest);
            BasisFullIKConstraint.data.HintPositionHead = data.position;
            BasisFullIKConstraint.data.HintRotationHead = data.rotation;

            data = GetCoordsForRole(BasisBoneTrackedRole.LeftLowerLeg);
            BasisFullIKConstraint.data.HintPositionLeftLowerLeg = data.position;
            BasisFullIKConstraint.data.HintRotationLeftLowerLeg = data.rotation;

            data = GetCoordsForRole(BasisBoneTrackedRole.RightLowerLeg);
            BasisFullIKConstraint.data.HintPositionRightLowerLeg = data.position;
            BasisFullIKConstraint.data.HintRotationRightLowerLeg = data.rotation;

            FilterAndApplyHint(LeftHandTwoBoneIK, BasisBoneTrackedRole.LeftLowerArm);
            FilterAndApplyHint(RightHandTwoBoneIK, BasisBoneTrackedRole.RightLowerArm);

            BasisAnimationRiggingHelper.SetHandCollisionScale(LeftHandTwoBoneIK, localPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale);
            BasisAnimationRiggingHelper.SetHandCollisionScale(RightHandTwoBoneIK, localPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale);

            // Hands
            FilterAndApplyTarget(LeftHandTwoBoneIK, BasisBoneTrackedRole.LeftHand);
            FilterAndApplyTarget(RightHandTwoBoneIK, BasisBoneTrackedRole.RightHand);

            // Toes (apply translation constraint)
            FilterAndApplyTarget(LeftToeConstraint, BasisBoneTrackedRole.LeftToes);
            FilterAndApplyTarget(RightToeConstraint, BasisBoneTrackedRole.RightToes);

            if (Builder != null)
            {
                // --- Do IK on animator ---
                Builder.SyncLayers();
                PlayableGraph.Evaluate(DeltaTime);
            }
        }

        /// <summary>
        /// Filters and applies a target to a hand two-bone IK constraint.
        /// </summary>
        private void FilterAndApplyTarget(BasisTwoBoneIKConstraintHand constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
            ApplyBoneIKTarget(constraint, data.position, data.rotation);
        }

        /// <summary>
        /// Filters and applies a target to a translation/rotation damping constraint (toes).
        /// </summary>
        private void FilterAndApplyTarget(BasisApplyTranslation constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
            ApplyBoneIKTarget(constraint, data.position, data.rotation);
        }

        /// <summary>
        /// Filters and applies a hint to a hand two-bone IK constraint.
        /// </summary>
        private void FilterAndApplyHint(BasisTwoBoneIKConstraintHand constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
            ApplyHandBoneIKHint(constraint, data.position, data.rotation);
        }

        /// <summary>
        /// Maps a tracked role to its outgoing world-space calibrated coordinates from the local bone driver.
        /// </summary>
        private BasisCalibratedCoords GetCoordsForRole(BasisBoneTrackedRole role)
        {
            // Map roles to driver controls
            switch (role)
            {
                case BasisBoneTrackedRole.Head: return BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
                case BasisBoneTrackedRole.Hips: return BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftHand: return BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightHand: return BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerArm: return BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerArm: return BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftFoot: return BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightFoot: return BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerLeg: return BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerLeg: return BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftToes: return BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightToes: return BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;

                case BasisBoneTrackedRole.Chest: return BasisLocalBoneDriver.ChestControl.OutgoingWorldData;

                default:
                    // Fallback: return identity to avoid null ref
                    return new BasisCalibratedCoords { position = Vector3.zero, rotation = Quaternion.identity };
            }
        }
        /// <summary>
        /// Applies a two-bone hand IK hint (no custom direction).
        /// </summary>
        public void ApplyHandBoneIKHint(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
        {
            Constraint.data.HintPosition = Position;
            Constraint.data.HintRotation = Rotation;
        }


        /// <summary>
        /// Applies target position/rotation to a translation/rotation damping constraint.
        /// </summary>
        public void ApplyBoneIKTarget(BasisApplyTranslation basisDamped, Vector3 Position, Quaternion Rotation)
        {
            basisDamped.data.TargetPosition = Position;
            basisDamped.data.TargetRotation = Rotation;
        }

        /// <summary>
        /// Applies target position/rotation to a hand two-bone IK constraint.
        /// </summary>
        public void ApplyBoneIKTarget(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
        {
            Constraint.data.TargetPosition = Position;
            Constraint.data.TargetRotation = Rotation;
        }

        /// <summary>
        /// Builds the rig's playable graph from the animator and switches the graph to manual update mode.
        /// </summary>
        public void BuildBuilder()
        {
            PlayableGraph = localPlayer.BasisAvatar.Animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Builder.Build(PlayableGraph);
        }

        /// <summary>
        /// Overload convenience: toggles layers based on current TPose state.
        /// </summary>
        public void OnTPose()
        {
            OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);
        }

        /// <summary>
        /// Enables/disables rig layers during TPose and notifies bone controls when exiting TPose.
        /// </summary>
        /// <param name="currentlyTposing">Whether the avatar is currently in TPose.</param>
        public void OnTPose(bool currentlyTposing)
        {
            if (Builder != null)
            {
                foreach (RigLayer Layer in Builder.layers)
                {
                    if (currentlyTposing)
                    {
                        Layer.active = false;
                    }
                }
                if (currentlyTposing == false)
                {
                    foreach (BasisLocalBoneControl control in BasisLocalPlayer.Instance.LocalBoneDriver.Controls)
                    {
                        control.OnHasRigChanged?.Invoke();
                    }
                }
            }
        }

        /// <summary>
        /// Cleans up created rig GameObjects (head/spine/hands/feet/toes/shoulders) before rebuilding.
        /// </summary>
        public void CleanupBeforeContinue()
        {
            if (MainRig != null)
            {
                GameObject.Destroy(MainRig.gameObject);
            }
            if (LeftHandRig != null)
            {
                GameObject.Destroy(LeftHandRig.gameObject);
            }
            if (RightHandRig != null)
            {
                GameObject.Destroy(RightHandRig.gameObject);
            }
            if (LeftToeRig != null)
            {
                GameObject.Destroy(LeftToeRig.gameObject);
            }
            if (RightToeRig != null)
            {
                GameObject.Destroy(RightToeRig.gameObject);
            }
        }
        public Rig MainRig;
        public RigLayer RigLayer;
        public BasisFullIKConstraint BasisFullIKConstraint;
        /// <summary>
        /// Sets up core body rigs (spine, head, hands, feet, toes) and ensures a <see cref="RigTransform"/> exists on hips.
        /// </summary>
        public void SetBodySettings(BasisLocalBoneDriver driver)
        {
            SetupManipulation(driver);
            LeftHand(driver);
            RightHand(driver);
            LeftToe(driver);
            RightToe(driver);

            SetupOverrides();

            if (references.Hips.gameObject.TryGetComponent<RigTransform>(out RigTransform RigTransform) == false)
            {
                RigTransform Hips = references.Hips.gameObject.AddComponent<RigTransform>();
            }
            BasisLocalBoneControl.HasEvents = true;
        }
        public void SetupManipulation(BasisLocalBoneDriver driver)
        {
            GameObject GameobjectHeadRig = CreateOrGetRig("Main IK", true, out MainRig, out RigLayer);
            Transform[] Root = new Transform[3];
            Transform[] Middle = new Transform[3];
            Transform[] Tip = new Transform[3];
            BasisBoneTrackedRole[] Roles = new BasisBoneTrackedRole[3];
            BasisBoneTrackedRole[] hintRoles = new BasisBoneTrackedRole[3];
            if (references.HasUpperchest)
            {
                Root[0] = references.Upperchest;
                Middle[0] = references.neck;
                Tip[0] = references.head;

                Roles[0] = BasisBoneTrackedRole.Head;
                hintRoles[0] = BasisBoneTrackedRole.Chest;
            }
            else
            {
                if (references.Haschest)
                {
                    Root[0] = references.chest;
                    Middle[0] = references.neck;
                    Tip[0] = references.head;

                    Roles[0] = BasisBoneTrackedRole.Head;
                    hintRoles[0] = BasisBoneTrackedRole.Chest;
                }
                else
                {
                    Root[0] = references.spine;
                    Middle[0] = references.neck;
                    Tip[0] = references.head;

                    Roles[0] = BasisBoneTrackedRole.Head;
                    hintRoles[0] = BasisBoneTrackedRole.Chest;
                }
            }
            Root[1] = references.LeftUpperLeg;
            Middle[1] = references.LeftLowerLeg;
            Tip[1] = references.leftFoot;

            Roles[1] = BasisBoneTrackedRole.LeftFoot;
            hintRoles[1] = BasisBoneTrackedRole.LeftLowerLeg;

            Root[2] = references.RightUpperLeg;
            Middle[2] = references.RightLowerLeg;
            Tip[2] = references.rightFoot;

            Roles[2] = BasisBoneTrackedRole.RightFoot;
            hintRoles[2] = BasisBoneTrackedRole.RightLowerLeg;

            BasisAnimationRiggingHelper.CreateMainIKRIG(localPlayer, GameobjectHeadRig, Root, Middle, Tip, Roles, hintRoles, out BasisFullIKConstraint, references.Hips, BasisBoneTrackedRole.Hips);

            BasisFullIKConstraint.data.enabledHead = true;
            BasisFullIKConstraint.data.enabledHips = true;

            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();

            if (driver.FindBone(out BasisLocalBoneControl LeftFoot, BasisBoneTrackedRole.LeftFoot))
            {
                LeftFoot.OnHasRigChanged += delegate
                {
                    BasisFullIKConstraint.data.EnableLeftLeg = LeftFoot.HasRigLayer == BasisHasRigLayer.HasRigLayer;
                };
                BasisFullIKConstraint.data.EnableLeftLeg = LeftFoot.HasRigLayer == BasisHasRigLayer.HasRigLayer;
            }

            if (driver.FindBone(out BasisLocalBoneControl RightFoot, BasisBoneTrackedRole.RightFoot))
            {
                RightFoot.OnHasRigChanged += delegate
                {
                    BasisFullIKConstraint.data.EnableRightLeg = RightFoot.HasRigLayer == BasisHasRigLayer.HasRigLayer;
                };
                BasisFullIKConstraint.data.EnableRightLeg = RightFoot.HasRigLayer == BasisHasRigLayer.HasRigLayer;
            }

            if (driver.FindBone(out BasisLocalBoneControl head, BasisBoneTrackedRole.Head))
            {
                head.OnHasRigChanged += delegate
                {
                    RigLayer.active = head.HasRigLayer == BasisHasRigLayer.HasRigLayer;
                };
                RigLayer.active = head.HasRigLayer == BasisHasRigLayer.HasRigLayer;
            }
        }
        /// <summary>
        /// Builds the “override constraints” rig and packs per-bone overrides into ⌈N/23⌉ BasisIK23Constraint batches.
        /// Caches O(1) lookup so hot paths stay GC-free.
        /// </summary>
        public void SetupOverrides()
        {
            var isActiveList = new List<bool>(55);
            var humanBodyBonesList = new List<HumanBodyBones>(55);

            GameObject rigGO = CreateOrGetRig("Override Constraints", true, out ConstraintsRig, out ConstraintsLayer);

            // Choose bones (skip eyes/fingers/LastBone) — matches old behavior
            foreach (HumanBodyBones bone in (HumanBodyBones[])Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (UseableBodyBone(bone))
                {
                    // only include if transform exists on current avatar
                    if (ResolveHumanoidBoneTransform(bone) != null)
                    {
                        humanBodyBonesList.Add(bone);
                        isActiveList.Add(false);
                    }
                }
            }

            HumanBones = humanBodyBonesList.ToArray();
            isActive = isActiveList.ToArray();

            // Build direct slot lookup table
            _boneToSlot = new BasisConstraintSlotIndex[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < _boneToSlot.Length; i++) _boneToSlot[i].Batch = -1;

            // Create batches
            int count = HumanBones.Length;
            int per = BasisIK23ConstraintData.Count;
            int batchCount = (count + per - 1) / per;
            Batches = new BasisIK23Constraint[batchCount];

            BasisIK23ConstraintTargetBinder.InitReflectionCache();

            int boneIdx = 0;
            for (int b = 0; b < batchCount; b++)
            {
                var go = new GameObject($"IK23 Batch {b:00}");
                go.transform.SetParent(rigGO.transform, false);

                var comp = go.AddComponent<BasisIK23Constraint>();
                var data = comp.data; // struct copy

                for (int slot = 0; slot < per && boneIdx < count; slot++, boneIdx++)
                {
                    HumanBodyBones bone = HumanBones[boneIdx];

                    Transform t = ResolveHumanoidBoneTransform(bone);
                    // write private m_targetN quickly
                    BasisIK23ConstraintTargetBinder.SetTargetTransform(ref data, slot, t);

                    // default disabled
                    data.SetWeight(slot, false);
                    data.SetOffsetRotation(slot, t.rotation);
                    data.SetTargetRotation(slot, t.rotation);
                    // map bone -> slot
                    _boneToSlot[(int)bone] = new BasisConstraintSlotIndex { Batch = (short)b, Slot = (short)slot };
                }

                comp.data = data; // push back for binder
                Batches[b] = comp;
            }

            DisableOverrides(); // start inactive until any bone is enabled
            BasisDebug.Log($"Built override batches: {batchCount}", BasisDebug.LogTag.Avatar);
        }

        /// <summary>
        /// we will automatically disable the overrides when you switch a avatar.
        /// you will need to listen for a avatar change event and reanable.
        /// </summary>
        public void DisableOverrides()
        {
            ConstraintsLayer.active = false;
            BasisDebug.Log("Disabling Overrides of Avatar Constraints", BasisDebug.LogTag.Avatar);
        }

        /// <summary>
        /// Returns whether a humanoid bone should have an override constraint generated for it.
        /// </summary>
        public static bool UseableBodyBone(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.LastBone:
                case HumanBodyBones.LeftEye:
                case HumanBodyBones.RightEye:
                    return false;

                // Left hand fingers
                case HumanBodyBones.LeftThumbProximal:
                case HumanBodyBones.LeftThumbIntermediate:
                case HumanBodyBones.LeftThumbDistal:
                case HumanBodyBones.LeftIndexProximal:
                case HumanBodyBones.LeftIndexIntermediate:
                case HumanBodyBones.LeftIndexDistal:
                case HumanBodyBones.LeftMiddleProximal:
                case HumanBodyBones.LeftMiddleIntermediate:
                case HumanBodyBones.LeftMiddleDistal:
                case HumanBodyBones.LeftRingProximal:
                case HumanBodyBones.LeftRingIntermediate:
                case HumanBodyBones.LeftRingDistal:
                case HumanBodyBones.LeftLittleProximal:
                case HumanBodyBones.LeftLittleIntermediate:
                case HumanBodyBones.LeftLittleDistal:

                // Right hand fingers
                case HumanBodyBones.RightThumbProximal:
                case HumanBodyBones.RightThumbIntermediate:
                case HumanBodyBones.RightThumbDistal:
                case HumanBodyBones.RightIndexProximal:
                case HumanBodyBones.RightIndexIntermediate:
                case HumanBodyBones.RightIndexDistal:
                case HumanBodyBones.RightMiddleProximal:
                case HumanBodyBones.RightMiddleIntermediate:
                case HumanBodyBones.RightMiddleDistal:
                case HumanBodyBones.RightRingProximal:
                case HumanBodyBones.RightRingIntermediate:
                case HumanBodyBones.RightRingDistal:
                case HumanBodyBones.RightLittleProximal:
                case HumanBodyBones.RightLittleIntermediate:
                case HumanBodyBones.RightLittleDistal:
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Enable/disable a bone override (per-slot weight 1/0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideUsage(HumanBodyBones bone, bool enabled)
        {
            if (!TryGetSlot(bone, out int bi, out int si)) return;

            int idx = Array.IndexOf(HumanBones, bone);
            if (idx >= 0) isActive[idx] = enabled;

            var comp = Batches[bi];
            var d = comp.data;
            d.SetWeight(si, enabled);
            comp.data = d;

            // activate layer only if any slot is active
            ConstraintsLayer.active = isActive.Any(x => x);
            if (!ConstraintsLayer.active) DisableOverrides();
        }

        /// <summary>
        /// Writes world-space target for a bone’s override slot (position + rotation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideData(HumanBodyBones bone, in Vector3 position, in Quaternion rotation)
        {
            if (!TryGetSlot(bone, out int bi, out int si)) return;

            var comp = Batches[bi];
            var d = comp.data;
            d.SetTargetPosition(si, position);
            d.SetTargetRotation(si, rotation);
            comp.data = d;
        }

        // === Helpers used by the override system ===

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetSlot(HumanBodyBones bone, out int batch, out int slot)
        {
            int raw = (int)bone;
            if (_boneToSlot == null || (uint)raw >= (uint)_boneToSlot.Length)
            {
                batch = slot = -1; return false;
            }
            var s = _boneToSlot[raw];
            if (s.Batch < 0) { batch = slot = -1; return false; }
            batch = s.Batch; slot = s.Slot; return true;
        }

        private Transform ResolveHumanoidBoneTransform(HumanBodyBones bone)
        {
            // Prefer your references map if available
            if (BasisLocalAvatarDriver.References != null &&
                BasisLocalAvatarDriver.References.GetTransform(bone, out Transform refT))
                return refT;

            // Fallback to Animator
            var animator = localPlayer?.BasisAvatar?.Animator;
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }

        /// <summary>
        /// Sets up left hand two-bone IK and layer events for hand/lower arm controls.
        /// </summary>
        public void LeftHand(BasisLocalBoneDriver driver)
        {
            GameObject Hands = CreateOrGetRig("LeftUpperArm, LeftLowerArm, LeftHand", false, out LeftHandRig, out LeftHandLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl LeftHand, BasisBoneTrackedRole.LeftHand))
            {
                controls.Add(LeftHand);
            }
            if (driver.FindBone(out BasisLocalBoneControl LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm))
            {
                controls.Add(LeftLowerArm);
            }
            WriteUpEvents(controls, LeftHandLayer);
            BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.leftUpperArm, references.leftLowerArm, references.leftHand, references.TposeLeftHand.rotation, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftLowerArm, true, out LeftHandTwoBoneIK);
        }

        /// <summary>
        /// Sets up right hand two-bone IK and layer events for hand/lower arm controls.
        /// </summary>
        public void RightHand(BasisLocalBoneDriver driver)
        {
            GameObject Hands = CreateOrGetRig("RightUpperArm, RightLowerArm, RightHand", false, out RightHandRig, out RightHandLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl RightHand, BasisBoneTrackedRole.RightHand))
            {
                controls.Add(RightHand);
            }
            if (driver.FindBone(out BasisLocalBoneControl RightLowerArm, BasisBoneTrackedRole.RightLowerArm))
            {
                controls.Add(RightLowerArm);
            }
            WriteUpEvents(controls, RightHandLayer);
            BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.RightUpperArm, references.RightLowerArm, references.rightHand, references.TposeRightHand.rotation, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightLowerArm, true, out RightHandTwoBoneIK);
        }
        /// <summary>
        /// Sets up left toe damping rig and registers layer toggling for the left toe control.
        /// </summary>
        public void LeftToe(BasisLocalBoneDriver driver)
        {
            GameObject LeftToe = CreateOrGetRig("LeftToe", false, out LeftToeRig, out LeftToeLayer);
            if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.LeftToes))
            {
                WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, LeftToeLayer);
            }
            LeftToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, LeftToe, references.leftToes, BasisBoneTrackedRole.LeftToes);
        }

        /// <summary>
        /// Sets up right toe damping rig and registers layer toggling for the right toe control.
        /// </summary>
        public void RightToe(BasisLocalBoneDriver driver)
        {
            GameObject RightToe = CreateOrGetRig("RightToe", false, out RightToeRig, out RightToeLayer);
            if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.RightToes))
            {
                WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, RightToeLayer);
            }
            RightToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, RightToe, references.rightToes, BasisBoneTrackedRole.RightToes);
        }

        /// <summary>
        /// Sets hint weights based on connected input devices and clears all hints first.
        /// </summary>
        public void CalibrateRoles()
        {
            foreach (BasisBoneTrackedRole Role in Enum.GetValues(typeof(BasisBoneTrackedRole)))
            {
                ApplyHint(Role, false);
            }
            for (int Index = 0; Index < BasisDeviceManagement.Instance.AllInputDevices.Count; Index++)
            {
                Device_Management.Devices.BasisInput BasisInput = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (BasisInput.TryGetRole(out BasisBoneTrackedRole Role))
                {
                    ApplyHint(Role, true);
                }
            }
        }

        /// <summary>
        /// Applies a hint weight to the appropriate constraint given a tracked role.
        /// </summary>
        public void ApplyHint(BasisBoneTrackedRole RoleWithHint, bool weight)
        {
            try
            {
                switch (RoleWithHint)
                {
                    case BasisBoneTrackedRole.Chest:
                        BasisFullIKConstraint.data.hintWeightHead = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerLeg:
                        BasisFullIKConstraint.data.hintWeightLeftLowerLeg = weight;
                        break;

                    case BasisBoneTrackedRole.LeftLowerLeg:
                        BasisFullIKConstraint.data.hintWeightRightLowerLeg = weight;
                        break;

                    case BasisBoneTrackedRole.RightUpperArm:
                        RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.LeftUpperArm:
                        LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    case BasisBoneTrackedRole.LeftLowerArm:
                        RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerArm:
                        LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.Log($"{e.Message} {e.StackTrace}");
            }
        }

        /// <summary>
        /// Wires change events from controls to a rig layer so the layer auto-activates
        /// when any control reports an active rig layer.
        /// </summary>
        public void WriteUpEvents(List<BasisLocalBoneControl> Controls, RigLayer Layer)
        {
            foreach (var control in Controls)
            {
                control.OnHasRigChanged += delegate { UpdateLayerActiveState(Controls, Layer); };
            }
            UpdateLayerActiveState(Controls, Layer);
        }

        /// <summary>
        /// Updates a layer's active flag based on whether any control reports an active rig layer.
        /// </summary>
        void UpdateLayerActiveState(List<BasisLocalBoneControl> Controls, RigLayer Layer)
        {
            Layer.active = Controls.Any(control => control.HasRigLayer == BasisHasRigLayer.HasRigLayer);
        }

        /// <summary>
        /// Creates a new rig GameObject and layer (or retrieves an existing one) under the animator.
        /// </summary>
        public GameObject CreateOrGetRig(string Role, bool Enabled, out Rig Rig, out RigLayer RigLayer)
        {
            foreach (RigLayer Layer in Builder.layers)
            {
                if (Layer.rig.name == $"Rig {Role}")
                {
                    RigLayer = Layer;
                    Rig = Layer.rig;
                    return Layer.rig.gameObject;
                }
            }
            GameObject RigGameobject = BasisAnimationRiggingHelper.CreateAndSetParent(localPlayer.BasisAvatar.Animator.transform, $"Rig {Role}");
            Rig = BasisHelpers.GetOrAddComponent<Rig>(RigGameobject);
            RigLayer = new RigLayer(Rig, Enabled);
            Builder.layers.Add(RigLayer);
            return RigGameobject;
        }
    }
}
