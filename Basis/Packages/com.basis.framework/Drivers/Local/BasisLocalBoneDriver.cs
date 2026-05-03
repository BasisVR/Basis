using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Manages per-bone controls for a local avatar and drives their simulation,
    /// world destinations, and optional gizmo visualization.
    /// </summary>
    /// <remarks>
    /// This driver discovers standard tracked roles, stores <see cref="BasisLocalBoneControl"/>s,
    /// sequences their per-frame updates, and renders debug gizmos/lines when enabled.
    /// </remarks>
    [System.Serializable]
    public class BasisLocalBoneDriver
    {
        /// <summary>
        /// RGB scalar applied to each bone color before it becomes a calibration
        /// sphere tint. The gizmo material is additive (One/One blend), so a
        /// lower value means a less-blown-out sphere on the avatar.
        /// </summary>
        private const float CalibrationSphereTint = 0.25f;

        /// <summary>Cached control for the neck bone.</summary>
        public static BasisLocalBoneControl NeckControl;

        /// <summary>Cached control for the head bone.</summary>
        public static BasisLocalBoneControl HeadControl;

        /// <summary>Cached control for the spine bone.</summary>
        public static BasisLocalBoneControl SpineControl;

        /// <summary>Cached control for the hips bone.</summary>
        public static BasisLocalBoneControl HipsControl;

        /// <summary>Cached control for the center eye (gaze) target.</summary>
        public static BasisLocalBoneControl EyeControl;

        /// <summary>Cached control for the mouth bone.</summary>
        public static BasisLocalBoneControl MouthControl;

        /// <summary>Cached control for the left foot bone.</summary>
        public static BasisLocalBoneControl LeftFootControl;

        /// <summary>Cached control for the right foot bone.</summary>
        public static BasisLocalBoneControl RightFootControl;

        /// <summary>Cached control for the left hand bone.</summary>
        public static BasisLocalBoneControl LeftHandControl;

        /// <summary>Cached control for the right hand bone.</summary>
        public static BasisLocalBoneControl RightHandControl;

        /// <summary>Cached control for the chest bone.</summary>
        public static BasisLocalBoneControl ChestControl;

        /// <summary>Cached control for the left upper leg (thigh).</summary>
        public static BasisLocalBoneControl LeftUpperLegControl;

        /// <summary>Cached control for the right upper leg (thigh).</summary>
        public static BasisLocalBoneControl RightUpperLegControl;

        /// <summary>Cached control for the left lower leg (shin).</summary>
        public static BasisLocalBoneControl LeftLowerLegControl;

        /// <summary>Cached control for the right lower leg (shin).</summary>
        public static BasisLocalBoneControl RightLowerLegControl;

        /// <summary>Cached control for the left lower arm (forearm).</summary>
        public static BasisLocalBoneControl LeftLowerArmControl;

        /// <summary>Cached control for the right lower arm (forearm).</summary>
        public static BasisLocalBoneControl RightLowerArmControl;

        /// <summary>Cached control for the left toe bones.</summary>
        public static BasisLocalBoneControl LeftToeControl;

        /// <summary>Cached control for the right toe bones.</summary>
        public static BasisLocalBoneControl RightToeControl;

        /// <summary>Cached control for the left toe bones.</summary>
        public static BasisLocalBoneControl LeftShoulderControl;

        /// <summary>Cached control for the right toe bones.</summary>
        public static BasisLocalBoneControl RightShoulderControl;

        /// <summary>True if an eye control was found during <see cref="Initialize"/>.</summary>
        public static bool HasEye;

        /// <summary>Number of active controls (mirrors <see cref="Controls"/> length).</summary>
        public int ControlsLength;

        /// <summary>All bone controls managed by this driver, indexed by <see cref="trackedRoles"/>.</summary>
        [SerializeField]
        public BasisLocalBoneControl[] Controls;

        /// <summary>Tracked roles corresponding to <see cref="Controls"/> indices.</summary>
        [SerializeField]
        public BasisBoneTrackedRole[] trackedRoles;

        /// <summary>Whether <see cref="CreateInitialArrays"/> has been called and controls are ready.</summary>
        public bool HasControls = false;

        /// <summary>Default gizmo sphere size (scaled by avatar height).</summary>
        public static float DefaultGizmoSize = 0.035f;

        /// <summary>Gizmo size for hand-related visuals (scaled by avatar height).</summary>
        public static float HandGizmoSize = 0.02f;

        // ===== Burst/Jobs simulation backing store =====
        // Per-frame inputs (incoming pose, has-tracker flags, target index, etc).
        private NativeArray<BasisBoneSimInput> _simInputs;
        // Per-frame outputs/persistent state (outgoing pose, last-run, world).
        private NativeArray<BasisBoneSimState> _simStates;
        // Cached target indices: _cachedTargetIndices[i] = array index of Controls[i].Target, or -1.
        private int[] _cachedTargetIndices = Array.Empty<int>();

        // Chain index arrays — built once after calibration.
        // Spine chain runs first (serially). The four limb chains run in parallel after spine.
        private NativeArray<int> _spineChainIndices;
        private NativeArray<int> _leftArmChainIndices;
        private NativeArray<int> _rightArmChainIndices;
        private NativeArray<int> _leftLegChainIndices;
        private NativeArray<int> _rightLegChainIndices;
        // Any controls not in the standard skeleton chains — processed serially after spine.
        private NativeArray<int> _otherChainIndices;

        private bool _nativeAllocated;
        private int _nativeCapacity;
        private bool _chainsBuilt;

        private static readonly BasisBoneTrackedRole[] SpineChainOrder =
        {
            BasisBoneTrackedRole.CenterEye,
            BasisBoneTrackedRole.Head,
            BasisBoneTrackedRole.Neck,
            BasisBoneTrackedRole.Chest,
            BasisBoneTrackedRole.Spine,
            BasisBoneTrackedRole.Hips,
            BasisBoneTrackedRole.Mouth,
        };
        private static readonly BasisBoneTrackedRole[] LeftArmChainOrder =
        {
            BasisBoneTrackedRole.LeftShoulder,
            BasisBoneTrackedRole.LeftUpperArm,
            BasisBoneTrackedRole.LeftLowerArm,
            BasisBoneTrackedRole.LeftHand,
        };
        private static readonly BasisBoneTrackedRole[] RightArmChainOrder =
        {
            BasisBoneTrackedRole.RightShoulder,
            BasisBoneTrackedRole.RightUpperArm,
            BasisBoneTrackedRole.RightLowerArm,
            BasisBoneTrackedRole.RightHand,
        };
        private static readonly BasisBoneTrackedRole[] LeftLegChainOrder =
        {
            BasisBoneTrackedRole.LeftUpperLeg,
            BasisBoneTrackedRole.LeftLowerLeg,
            BasisBoneTrackedRole.LeftFoot,
            BasisBoneTrackedRole.LeftToes,
        };
        private static readonly BasisBoneTrackedRole[] RightLegChainOrder =
        {
            BasisBoneTrackedRole.RightUpperLeg,
            BasisBoneTrackedRole.RightLowerLeg,
            BasisBoneTrackedRole.RightFoot,
            BasisBoneTrackedRole.RightToes,
        };

        /// <summary>
        /// Discovers common tracked roles and assigns cached references (e.g., head, spine).
        /// </summary>
        public void Initialize()
        {
            HasEye = FindBone(out EyeControl, BasisBoneTrackedRole.CenterEye);
            FindBone(out SpineControl, BasisBoneTrackedRole.Spine);
            FindBone(out NeckControl, BasisBoneTrackedRole.Neck);
            FindBone(out HeadControl, BasisBoneTrackedRole.Head);
            FindBone(out HipsControl, BasisBoneTrackedRole.Hips);
            FindBone(out MouthControl, BasisBoneTrackedRole.Mouth);
            FindBone(out LeftFootControl, BasisBoneTrackedRole.LeftFoot);
            FindBone(out RightFootControl, BasisBoneTrackedRole.RightFoot);
            FindBone(out LeftHandControl, BasisBoneTrackedRole.LeftHand);
            FindBone(out RightHandControl, BasisBoneTrackedRole.RightHand);
            FindBone(out ChestControl, BasisBoneTrackedRole.Chest);
            FindBone(out LeftUpperLegControl, BasisBoneTrackedRole.LeftUpperLeg);
            FindBone(out RightUpperLegControl, BasisBoneTrackedRole.RightUpperLeg);
            FindBone(out LeftLowerLegControl, BasisBoneTrackedRole.LeftLowerLeg);
            FindBone(out RightLowerLegControl, BasisBoneTrackedRole.RightLowerLeg);
            FindBone(out LeftLowerArmControl, BasisBoneTrackedRole.LeftLowerArm);
            FindBone(out RightLowerArmControl, BasisBoneTrackedRole.RightLowerArm);
            FindBone(out LeftToeControl, BasisBoneTrackedRole.LeftToes);
            FindBone(out RightToeControl, BasisBoneTrackedRole.RightToes);
            FindBone(out LeftShoulderControl, BasisBoneTrackedRole.LeftShoulder);
            FindBone(out RightShoulderControl, BasisBoneTrackedRole.RightShoulder);
        }

        /// <summary>
        /// Simulates all bone controls for a frame using the given parent matrix and delta time.
        /// Runs the spine chain serially in a Burst job, then schedules four limb chains
        /// (left/right arm, left/right leg) in parallel with a dependency on the spine chain.
        /// Any non-skeleton bones are processed serially in an "other" chain.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update (seconds).</param>
        /// <param name="parentMatrix">Parent transform matrix that seeds world-space computation.</param>
        public void Simulate(float deltaTime, Matrix4x4 parentMatrix)
        {
            RunSimulation(parentMatrix, deltaTime, seedLastRunFromOutgoing: false);
            // Gizmo drawing intentionally deferred to AfterSimulateOnRender —
            // running it here would read bone Transform.position before Unity's
            // Animator has updated bones for this frame, leaving the spheres a
            // frame stale relative to the avatar.
        }

        /// <summary>
        /// Simulates all bone controls but seeds their "last run" data from current outgoing data,
        /// effectively skipping interpolation/lerp for this frame.
        /// </summary>
        /// <param name="parentMatrix">Parent transform matrix for world calculations.</param>
        public void SimulateWithoutLerp(Matrix4x4 parentMatrix)
        {
            RunSimulation(parentMatrix, Time.deltaTime, seedLastRunFromOutgoing: true);
            if (SMModuleDebugOptions.UseGizmos)
            {
                DrawGizmos();
            }
        }

        private void RunSimulation(Matrix4x4 parentMatrix, float deltaTime, bool seedLastRunFromOutgoing)
        {
            if (ControlsLength == 0)
            {
                return;
            }

            EnsureNativeAllocated();
            if (!_chainsBuilt)
            {
                BuildChainData();
            }

            PackInputsAndStates(seedLastRunFromOutgoing);

            float4x4 parentMatrix44 = parentMatrix;
            quaternion parentRot = parentMatrix.rotation;

            // Schedule: spine first; limb chains depend on spine; "other" depends on spine.
            JobHandle spineHandle = default;
            if (_spineChainIndices.IsCreated && _spineChainIndices.Length > 0)
            {
                spineHandle = MakeJob(_spineChainIndices, parentMatrix44, parentRot, deltaTime).Schedule();
            }

            JobHandle leftArmHandle = default;
            JobHandle rightArmHandle = default;
            JobHandle leftLegHandle = default;
            JobHandle rightLegHandle = default;
            JobHandle otherHandle = default;

            if (_leftArmChainIndices.IsCreated && _leftArmChainIndices.Length > 0)
            {
                leftArmHandle = MakeJob(_leftArmChainIndices, parentMatrix44, parentRot, deltaTime).Schedule(spineHandle);
            }
            if (_rightArmChainIndices.IsCreated && _rightArmChainIndices.Length > 0)
            {
                rightArmHandle = MakeJob(_rightArmChainIndices, parentMatrix44, parentRot, deltaTime).Schedule(spineHandle);
            }
            if (_leftLegChainIndices.IsCreated && _leftLegChainIndices.Length > 0)
            {
                leftLegHandle = MakeJob(_leftLegChainIndices, parentMatrix44, parentRot, deltaTime).Schedule(spineHandle);
            }
            if (_rightLegChainIndices.IsCreated && _rightLegChainIndices.Length > 0)
            {
                rightLegHandle = MakeJob(_rightLegChainIndices, parentMatrix44, parentRot, deltaTime).Schedule(spineHandle);
            }
            if (_otherChainIndices.IsCreated && _otherChainIndices.Length > 0)
            {
                otherHandle = MakeJob(_otherChainIndices, parentMatrix44, parentRot, deltaTime).Schedule(spineHandle);
            }

            JobHandle armsCombined = JobHandle.CombineDependencies(leftArmHandle, rightArmHandle);
            JobHandle legsCombined = JobHandle.CombineDependencies(leftLegHandle, rightLegHandle);
            JobHandle limbsCombined = JobHandle.CombineDependencies(armsCombined, legsCombined);
            JobHandle final = JobHandle.CombineDependencies(limbsCombined, otherHandle);
            final.Complete();

            UnpackStates();
        }

        private BasisBoneSimChainJob MakeJob(NativeArray<int> chain, float4x4 parentMatrix44, quaternion parentRot, float deltaTime)
        {
            return new BasisBoneSimChainJob
            {
                ChainIndices = chain,
                Inputs = _simInputs,
                States = _simStates,
                ParentMatrix = parentMatrix44,
                ParentRotation = parentRot,
                DeltaTime = deltaTime,
                TrackerSmooth = BasisLocalBoneControl.trackersmooth,
                QuaternionLerp = BasisLocalBoneControl.QuaternionLerp,
                QuaternionLerpFastMovement = BasisLocalBoneControl.QuaternionLerpFastMovement,
                AngleBeforeSpeedup = BasisLocalBoneControl.AngleBeforeSpeedup,
                PositionLerpAmount = BasisLocalBoneControl.PositionLerpAmount,
            };
        }

        private void EnsureNativeAllocated()
        {
            if (_nativeAllocated && _nativeCapacity == ControlsLength)
            {
                return;
            }

            DisposeNative();
            _simInputs = new NativeArray<BasisBoneSimInput>(ControlsLength, Allocator.Persistent);
            _simStates = new NativeArray<BasisBoneSimState>(ControlsLength, Allocator.Persistent);
            _cachedTargetIndices = new int[ControlsLength];
            _nativeCapacity = ControlsLength;
            _nativeAllocated = true;
            _chainsBuilt = false;
        }

        private void BuildChainData()
        {
            // Cache target indices once. Targets are wired at calibration time.
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                _cachedTargetIndices[i] = (c.Target != null) ? Array.IndexOf(Controls, c.Target) : -1;
            }

            DisposeChainArrays();

            HashSet<int> covered = new HashSet<int>();
            _spineChainIndices = BuildChainIndices(SpineChainOrder, covered);
            _leftArmChainIndices = BuildChainIndices(LeftArmChainOrder, covered);
            _rightArmChainIndices = BuildChainIndices(RightArmChainOrder, covered);
            _leftLegChainIndices = BuildChainIndices(LeftLegChainOrder, covered);
            _rightLegChainIndices = BuildChainIndices(RightLegChainOrder, covered);

            // Any remaining controls (e.g. dynamically added via AddRange) go in "other".
            List<int> other = new List<int>();
            for (int i = 0; i < ControlsLength; i++)
            {
                if (!covered.Contains(i))
                {
                    other.Add(i);
                }
            }
            _otherChainIndices = ToNativeIntArray(other);
            _chainsBuilt = true;
        }

        private NativeArray<int> BuildChainIndices(BasisBoneTrackedRole[] order, HashSet<int> covered)
        {
            List<int> list = new List<int>(order.Length);
            for (int i = 0; i < order.Length; i++)
            {
                int idx = Array.IndexOf(trackedRoles, order[i]);
                if (idx >= 0 && idx < ControlsLength && covered.Add(idx))
                {
                    list.Add(idx);
                }
            }
            return ToNativeIntArray(list);
        }

        private static NativeArray<int> ToNativeIntArray(List<int> list)
        {
            NativeArray<int> arr = new NativeArray<int>(list.Count, Allocator.Persistent);
            for (int i = 0; i < list.Count; i++)
            {
                arr[i] = list[i];
            }
            return arr;
        }

        private unsafe void PackInputsAndStates(bool seedLastRunFromOutgoing)
        {
            // Direct pointers into NativeArray storage skip the per-index bounds and
            // atomic-safety wrappers that the indexer goes through (significant in
            // editor mode, small win in release).
            BasisBoneSimInput* inputs = (BasisBoneSimInput*)_simInputs.GetUnsafePtr();
            BasisBoneSimState* states = (BasisBoneSimState*)_simStates.GetUnsafePtr();
            int len = ControlsLength;

            for (int i = 0; i < len; i++)
            {
                BasisLocalBoneControl c = Controls[i];

                // Input slot: write fields directly, no struct copy back through the indexer.
                BasisBoneSimInput* inp = inputs + i;
                int targetIdx = _cachedTargetIndices[i];
                inp->IncomingPosition = c.IncomingData.position;
                inp->IncomingRotation = c.IncomingData.rotation;
                inp->InverseOffsetPosition = c.InverseOffsetFromBone.position;
                inp->InverseOffsetRotation = c.InverseOffsetFromBone.rotation;
                inp->ScaledOffset = c.ScaledOffset;
                inp->TargetIndex = targetIdx;
                inp->HasTracker = (c.HasTracked == BasisHasTracked.HasTracker) ? (byte)1 : (byte)0;
                inp->UseInverseOffset = c.UseInverseOffset ? (byte)1 : (byte)0;
                inp->HasVirtualOverride = c.HasVirtualOverride ? (byte)1 : (byte)0;
                inp->HasTarget = (targetIdx >= 0) ? (byte)1 : (byte)0;

                // State slot: same pattern.
                BasisBoneSimState* st = states + i;
                Vector3 outPos = c.OutGoingData.position;
                Quaternion outRot = c.OutGoingData.rotation;
                st->OutgoingPosition = outPos;
                st->OutgoingRotation = outRot;
                if (seedLastRunFromOutgoing)
                {
                    st->LastRunPosition = outPos;
                    st->LastRunRotation = outRot;
                }
                else
                {
                    st->LastRunPosition = c.LastRunData.position;
                    st->LastRunRotation = c.LastRunData.rotation;
                }
                st->OutgoingWorldPosition = c.OutgoingWorldData.position;
                st->OutgoingWorldRotation = c.OutgoingWorldData.rotation;
            }
        }

        private unsafe void UnpackStates()
        {
            // Read-only access to the state slots via raw pointer.
            BasisBoneSimState* states = (BasisBoneSimState*)_simStates.GetUnsafeReadOnlyPtr();
            int len = ControlsLength;

            for (int i = 0; i < len; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                BasisBoneSimState* st = states + i;

                c.OutGoingData.position = st->OutgoingPosition;
                c.OutGoingData.rotation = st->OutgoingRotation;
                c.LastRunData.position = st->LastRunPosition;
                c.LastRunData.rotation = st->LastRunRotation;
                c.OutgoingWorldData.position = st->OutgoingWorldPosition;
                c.OutgoingWorldData.rotation = st->OutgoingWorldRotation;
            }
        }

        /// <summary>
        /// Forces the chain-index cache to be rebuilt on the next <see cref="Simulate"/>.
        /// Call after any change that affects target wiring or controls membership.
        /// </summary>
        public void InvalidateChainData()
        {
            _chainsBuilt = false;
        }

        /// <summary>
        /// Releases all native containers. Call when the driver is being torn down.
        /// </summary>
        public void Dispose()
        {
            DisposeChainArrays();
            if (_simInputs.IsCreated) _simInputs.Dispose();
            if (_simStates.IsCreated) _simStates.Dispose();
            _nativeAllocated = false;
            _nativeCapacity = 0;
            _chainsBuilt = false;
        }

        private void DisposeNative()
        {
            DisposeChainArrays();
            if (_simInputs.IsCreated) _simInputs.Dispose();
            if (_simStates.IsCreated) _simStates.Dispose();
            _nativeAllocated = false;
            _nativeCapacity = 0;
        }

        private void DisposeChainArrays()
        {
            if (_spineChainIndices.IsCreated) _spineChainIndices.Dispose();
            if (_leftArmChainIndices.IsCreated) _leftArmChainIndices.Dispose();
            if (_rightArmChainIndices.IsCreated) _rightArmChainIndices.Dispose();
            if (_leftLegChainIndices.IsCreated) _leftLegChainIndices.Dispose();
            if (_rightLegChainIndices.IsCreated) _rightLegChainIndices.Dispose();
            if (_otherChainIndices.IsCreated) _otherChainIndices.Dispose();
            _chainsBuilt = false;
        }

        /// <summary>
        /// Draws gizmos for all controls using the current avatar scale. Lines (skeleton) and
        /// calibration spheres are gated by independent sub-toggles under the ShowGizmos master.
        /// </summary>
        public void DrawGizmos()
        {
            if (SMModuleDebugOptions.UseSkeletonLines)
            {
                for (int Index = 0; Index < ControlsLength; Index++)
                {
                    DrawGizmos(Controls[Index]);
                }
            }
            if (SMModuleDebugOptions.UseCalibrationSpheres
                && GizmoBones.Count > 0
                && BasisAvatarIKStageCalibration.TryGetCalibrationVisualizationFrame(
                    out Vector3 bodyOrigin,
                    out Quaternion bodyRot,
                    out float eyeHeight,
                    out _))
            {
                // Body frame is recomputed every frame from the live HMD pose so
                // the regions track wherever the player is now. The visualization
                // helper picks snapshot priors when a calibration has run, default
                // priors otherwise — either way the gizmos appear immediately.

                for (int i = 0; i < GizmoBones.Count; i++)
                {
                    GizmoBone GizmoBone = GizmoBones[i];

                    // Center of the acceptance region in body-local playspace:
                    // X = lateral * eye, Y = vertical * eye, Z = 0 (depth is not
                    // scored, so we anchor on the body-frame plane).
                    Vector3 localOffset = new Vector3(
                        GizmoBone.ExpectedLateral * eyeHeight,
                        GizmoBone.ExpectedHeight * eyeHeight,
                        0f);
                    Vector3 worldPos = bodyOrigin + bodyRot * localOffset;

                    // 3σ acceptance radius in each scoring axis × 2 = diameter for
                    // the unit-diameter sphere mesh. Depth is unconstrained by
                    // ScoreSampleAgainstRole, so we use the larger of the two
                    // scoring axes — keeps the ellipsoid visible from any angle
                    // without implying depth gates classification.
                    float latDiameter = 6f * GizmoBone.LateralSigma * eyeHeight;
                    float vertDiameter = 6f * GizmoBone.HeightSigma * eyeHeight;
                    float depthDiameter = Mathf.Max(latDiameter, vertDiameter);

                    // Per-bone CalibSphereScale stays useful as a visualization
                    // size knob — multiplies all three axes uniformly.
                    float vizMul = SMModuleCalibration.GetSphereScale(GizmoBone.Control);
                    Vector3 scale = new Vector3(latDiameter, vertDiameter, depthDiameter) * vizMul;

                    BasisGizmoManager.UpdateSphereGizmo(
                        GizmoBone.GizmoReference,
                        worldPos,
                        bodyRot,
                        scale);
                }
            }
        }

        /// <summary>
        /// Invokes pre-sim callbacks on the player and simulates without interpolation.
        /// </summary>
        /// <param name="Player">The owning player.</param>
        public void SimulateAndApplyWithoutLerp(BasisLocalPlayer Player)
        {
            Player.OnLateSimulateBones(Player);
            Player.OnRenderSimulateBones(Player);
            Player.ApplyVirtualData(Player);
            SimulateWithoutLerp(BasisLocalPlayer.localToWorldMatrix);
        }

        /// <summary>
        /// Computes world-space destinations for outgoing local positions using a parent matrix.
        /// </summary>
        /// <param name="localToWorldMatrix">The parent local-to-world transform.</param>
        public void SimulateWorldDestinations(Matrix4x4 localToWorldMatrix)
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                // Apply local transform to parent's world transform
                Controls[Index].OutgoingWorldData.position = localToWorldMatrix.MultiplyPoint3x4(Controls[Index].OutGoingData.position);
            }
        }

        /// <summary>
        /// Removes all rig-change listeners from controls and clears the static event flag.
        /// </summary>
        public void RemoveAllListeners()
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].OnHasRigChanged = null;
            }
            BasisLocalBoneControl.HasEvents = false;
        }

        /// <summary>
        /// Appends a batch of controls and roles to the current arrays and updates <see cref="ControlsLength"/>.
        /// </summary>
        /// <param name="newControls">Controls to add.</param>
        /// <param name="newRoles">Roles corresponding to <paramref name="newControls"/>.</param>
        public void AddRange(BasisLocalBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            Controls = Controls.Concat(newControls).ToArray();
            trackedRoles = trackedRoles.Concat(newRoles).ToArray();
            ControlsLength = Controls.Length;
            // Force EnsureNativeAllocated and BuildChainData to re-run on the next Simulate.
            _chainsBuilt = false;
        }

        /// <summary>
        /// Looks up a control by its tracked <paramref name="Role"/>.
        /// </summary>
        /// <param name="control">Out: control if found; default instance if not.</param>
        /// <param name="Role">The role to locate.</param>
        /// <returns>True if found; otherwise false.</returns>
        public bool FindBone(out BasisLocalBoneControl control, BasisBoneTrackedRole Role)
        {
            int Index = Array.IndexOf(trackedRoles, Role);

            if (Index >= 0 && Index < ControlsLength)
            {
                control = Controls[Index];
                return true;
            }
            control = new BasisLocalBoneControl();
            return false;
        }

        /// <summary>
        /// Finds the tracked role for a given <paramref name="control"/>.
        /// </summary>
        /// <param name="control">The control to query.</param>
        /// <param name="Role">Out: matching role if found.</param>
        /// <returns>True if the control is known; otherwise false.</returns>
        public bool FindTrackedRole(BasisLocalBoneControl control, out BasisBoneTrackedRole Role)
        {
            int Index = Array.IndexOf(Controls, control);

            if (Index >= 0 && Index < ControlsLength)
            {
                Role = trackedRoles[Index];
                return true;
            }

            Role = BasisBoneTrackedRole.CenterEye;
            return false;
        }

        /// <summary>
        /// Creates and populates the <see cref="Controls"/> and <see cref="trackedRoles"/> arrays,
        /// generating a rainbow palette and optional remote-only subset.
        /// </summary>
        /// <param name="IsLocal">When true, includes all enum roles; when false, a limited subset plus an extra role at index 22.</param>
        public void CreateInitialArrays(bool IsLocal)
        {
            trackedRoles = new BasisBoneTrackedRole[] { };
            Controls = new BasisLocalBoneControl[] { };
            int Length;
            if (IsLocal)
            {
                Length = Enum.GetValues(typeof(BasisBoneTrackedRole)).Length;
            }
            else
            {
                Length = 6;
            }
            Color[] Colors = GenerateRainbowColors(Length);
            List<BasisLocalBoneControl> newControls = new List<BasisLocalBoneControl>();
            List<BasisBoneTrackedRole> Roles = new List<BasisBoneTrackedRole>();
            for (int Index = 0; Index < Length; Index++)
            {
                SetupRole(Index, Colors[Index], out BasisLocalBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            if (IsLocal == false)
            {
                // Adds a specific role by enum index for remote avatars.
                SetupRole(22, Color.blue, out BasisLocalBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            AddRange(newControls.ToArray(), Roles.ToArray());
            HasControls = true;
            InitializeGizmos();
        }

        /// <summary>
        /// Initializes a single role/control pair at the given enum <paramref name="Index"/>.
        /// </summary>
        /// <param name="Index">Enum index for <see cref="BasisBoneTrackedRole"/>.</param>
        /// <param name="Color">Debug color to assign.</param>
        /// <param name="BasisBoneControl">Out: created control.</param>
        /// <param name="role">Out: resolved role.</param>
        public void SetupRole(int Index, Color Color, out BasisLocalBoneControl BasisBoneControl, out BasisBoneTrackedRole role)
        {
            role = (BasisBoneTrackedRole)Index;
            BasisBoneControl = new BasisLocalBoneControl();
            BasisBoneControl.OutgoingWorldData.position = Vector3.zero;
            BasisBoneControl.OutgoingWorldData.rotation = Quaternion.identity;
            BasisBoneControl.LastRunData.position = BasisBoneControl.OutGoingData.position;
            BasisBoneControl.LastRunData.rotation = BasisBoneControl.OutGoingData.rotation;
            FillOutBasicInformation(BasisBoneControl, role.ToString(), Color);
        }

        // Priority for the per-frame gizmo render callback. Runs late so the
        // Animator (PreLateUpdate) and any IK/movement effectors that mutate
        // bone Transforms have already settled by the time we read them.
        private const int RenderGizmosPriority = 250;

        /// <summary>
        /// Subscribes to gizmo usage changes and the render-time callback so
        /// this driver can create/update gizmos. Render-time is required so
        /// gizmo position/rotation reflect the post-Animator bone Transforms
        /// instead of last frame's stale values.
        /// </summary>
        public void InitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;
            BasisLocalPlayer.AfterSimulateOnRender.AddAction(RenderGizmosPriority, RenderGizmos);
        }

        /// <summary>
        /// Unsubscribes from gizmo usage changes and the render-time callback.
        /// </summary>
        public void DeInitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
            BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(RenderGizmosPriority, RenderGizmos);
        }

        /// <summary>
        /// Per-frame render-time gizmo refresh, gated by the master ShowGizmos
        /// toggle so the no-op early out is cheap.
        /// </summary>
        private void RenderGizmos()
        {
            if (!SMModuleDebugOptions.UseGizmos) return;
            DrawGizmos();
        }

        /// <summary>
        /// Creates or updates gizmos/lines for each control when <paramref name="State"/> is true;
        /// clears flags when false. Actual updates per-frame occur in <see cref="DrawGizmos(BasisLocalBoneControl, float)"/>.
        /// </summary>
        /// <param name="State">Whether gizmo rendering should be active.</param>
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone Driver Gizmos", BasisDebug.LogTag.Gizmo);
            if (!State)
            {
                // Manager wiped its pool when ShowGizmos went off — drop cached
                // calibration-sphere IDs so DrawGizmos doesn't UpdateSphereGizmo
                // a stale entry next frame.
                GizmoBones.Clear();
                return;
            }

            float Size = BasisHeightDriver.ScaledToMatchValue;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                BasisLocalBoneControl Control = Controls[Index];
                Vector3 BonePosition = Control.OutgoingWorldData.position;
                if (Control.HasTarget)
                {
                    if (BasisGizmoManager.CreateLineGizmo(trackedRoles[Index].ToString(), out Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position, 0.05f * Size, Control.Color))
                    {
                        Control.HasLineDraw = true;
                    }
                }
            }

            // Lines are created visible — match the sub-toggle's current state.
            ApplySkeletonLineVisibility();
            RebuildCalibrationSpheres();
        }

        /// <summary>
        /// Drops and recreates the per-role calibration spheres drawn around each
        /// tracked avatar bone. Idempotent: safe to call from <see cref="UpdateGizmoUsage"/>
        /// (when ShowGizmos turns on) and from
        /// <see cref="BasisAvatarIKStageCalibration.FullBodyCalibration"/>
        /// (so an avatar reload swaps to the new bone transforms). Skips creation
        /// silently when the master ShowGizmos toggle is off — the toggle path will
        /// call back in here once it's flipped on.
        /// </summary>
        public void RebuildCalibrationSpheres()
        {
            // DestroyGizmo on a stale ID logs a warning, so only call it when the
            // manager's pool actually still holds the IDs we cached.
            if (SMModuleDebugOptions.UseGizmos)
            {
                for (int i = 0; i < GizmoBones.Count; i++)
                {
                    BasisGizmoManager.DestroyGizmo(GizmoBones[i].GizmoReference);
                }
            }
            GizmoBones.Clear();

            if (!SMModuleDebugOptions.UseGizmos)
            {
                return;
            }

            // Pull the prior list via the visualization helper so we get either
            // snapshot priors (when a calibration has run) or default-armReach
            // priors (when it hasn't). Body frame from the helper is unused here
            // — it's recomputed every frame in DrawGizmos so regions track the
            // live HMD pose.
            if (!BasisAvatarIKStageCalibration.TryGetCalibrationVisualizationFrame(
                out _,
                out _,
                out _,
                out IReadOnlyList<BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior> priors))
            {
                return;
            }

            // Build a role → bone-control lookup so each region picks up the
            // matching bone's rainbow color. Some priors (e.g. arm roles) may
            // not have a 1:1 control in the avatar; those fall through to a
            // neutral tint instead of pulling a wrong color.
            Dictionary<BasisBoneTrackedRole, BasisLocalBoneControl> controlByRole = new Dictionary<BasisBoneTrackedRole, BasisLocalBoneControl>(ControlsLength);
            for (int i = 0; i < ControlsLength; i++)
            {
                controlByRole[trackedRoles[i]] = Controls[i];
            }

            for (int i = 0; i < priors.Count; i++)
            {
                BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior prior = priors[i];
                // We deliberately do NOT skip disabled priors here. The body-tracking
                // calibration toggles control whether the *classifier* tries to bind
                // a tracker to a role, but for the visualization we want users to
                // see the full acceptance map — including regions for roles they
                // could opt in to (e.g. shoulders, which default off). Skipping them
                // hides parts of the body the user might want to enable.

                Color regionColor;
                if (controlByRole.TryGetValue(prior.Role, out BasisLocalBoneControl Control))
                {
                    // GizmoMaterial uses additive blending — RGB intensity directly
                    // controls brightness. Bone colors at full intensity stack
                    // opaquely on top of the avatar, so dim them to keep regions
                    // legible without drowning the view.
                    regionColor = Control.Color * CalibrationSphereTint;
                    regionColor.a = Control.Color.a;
                }
                else
                {
                    regionColor = new Color(CalibrationSphereTint, CalibrationSphereTint, CalibrationSphereTint, 1f);
                }

                AddCalibrationRegion(
                    $"{prior.Role} Calibration Region",
                    prior.Role,
                    prior.ExpectedHeight,
                    prior.ExpectedLateral,
                    prior.HeightSigma,
                    prior.LateralSigma,
                    regionColor);
            }

            // Honor the sub-toggle's current state — gizmos are created visible,
            // so hide them now if the user already had regions turned off.
            ApplyCalibrationSphereVisibility();
        }

        /// <summary>
        /// Toggles visibility on every cached calibration-sphere gizmo. Called by
        /// <see cref="SMModuleDebugOptions"/> when the calibration-spheres sub-toggle
        /// flips so the spheres actually appear and disappear in the scene.
        /// </summary>
        public void ApplyCalibrationSphereVisibility()
        {
            bool visible = SMModuleDebugOptions.UseCalibrationSpheres;
            for (int i = 0; i < GizmoBones.Count; i++)
            {
                BasisGizmoManager.SetGizmoActive(GizmoBones[i].GizmoReference, visible);
            }
        }

        /// <summary>
        /// Toggles visibility on every per-control skeleton-line gizmo. Mirror of
        /// <see cref="ApplyCalibrationSphereVisibility"/> — flag alone gates the
        /// per-frame UpdateLineGizmo call, but the line GameObjects need an
        /// explicit hide/show to actually appear/disappear.
        /// </summary>
        public void ApplySkeletonLineVisibility()
        {
            bool visible = SMModuleDebugOptions.UseSkeletonLines;
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl Control = Controls[i];
                if (Control.HasLineDraw)
                {
                    BasisGizmoManager.SetGizmoActive(Control.LineDrawIndex, visible);
                }
            }
        }

        /// <summary>
        /// Sets common display info on a control (name and color).
        /// </summary>
        /// <param name="Control">The control to update.</param>
        /// <param name="Name">A human-friendly name.</param>
        /// <param name="Color">Assigned debug color.</param>
        public void FillOutBasicInformation(BasisLocalBoneControl Control, string Name, Color Color)
        {
            Control.name = Name;
            Control.Color = Color;
        }

        /// <summary>
        /// Generates a rainbow palette with <paramref name="RequestColorCount"/> distinct hues.
        /// </summary>
        /// <param name="RequestColorCount">Number of colors to produce.</param>
        /// <returns>Array of HSV-to-RGB converted colors spanning the hue wheel.</returns>
        public Color[] GenerateRainbowColors(int RequestColorCount)
        {
            Color[] rainbowColors = new Color[RequestColorCount];

            for (int Index = 0; Index < RequestColorCount; Index++)
            {
                float hue = Mathf.Repeat(Index / (float)RequestColorCount, 1f);
                rainbowColors[Index] = Color.HSVToRGB(hue, 1f, 1f);
            }

            return rainbowColors;
        }

        /// <summary>
        /// Creates a simple positional lock/constraint between <paramref name="addToBone"/> and <paramref name="target"/>,
        /// capturing offsets in T-pose space and flagging target presence.
        /// </summary>
        /// <param name="addToBone">Bone control to receive the lock.</param>
        /// <param name="target">Target control to follow.</param>
        public void CreateRotationalLock(BasisLocalBoneControl addToBone, BasisLocalBoneControl target)
        {
            addToBone.Target = target;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;
            // Target wiring affects chain index cache and target lookups — rebuild on next Simulate.
            _chainsBuilt = false;
        }

        /// <summary>
        /// Converts a world-space point to the avatar's local space, using <paramref name="Transform"/> as the origin.
        /// </summary>
        /// <param name="Transform">Avatar root transform.</param>
        /// <param name="WorldSpace">World-space point to convert.</param>
        /// <returns>Point expressed in avatar-local coordinates.</returns>
        public static Vector3 ConvertToAvatarSpaceInitial(Transform Transform, Vector3 WorldSpace)
        {
            return BasisHelpers.ConvertToLocalSpace(WorldSpace, Transform.position);
        }
        /// <summary>
        /// One per-role calibration region. Position/rotation/scale are all derived
        /// from the captured body frame + prior data each frame in DrawGizmos —
        /// the gizmo itself stores only the IDs and the prior values, not a bone
        /// Transform, because the classifier scores tracker positions in body-local
        /// playspace, not at the avatar's bone.
        /// </summary>
        [System.Serializable]
        public class GizmoBone
        {
            public int GizmoReference;
            public BasisBoneTrackedRole Control;
            public float ExpectedHeight;   // ratio: y / eyeHeight at the role's expected position
            public float ExpectedLateral;  // ratio: signed x / eyeHeight (+x = body right)
            public float HeightSigma;      // 1σ in HeightRatio space
            public float LateralSigma;     // 1σ in LateralRatio space
        }
        [SerializeField]
        public List<GizmoBone> GizmoBones = new List<GizmoBone>();
        /// <summary>
        /// Updates/creates gizmos for a specific control (sphere and optional line to target).
        /// Also renders a T-pose gizmo if T-posing and the role is FB-tracked.
        /// </summary>
        /// <param name="Control">Control to visualize.</param>
        /// <param name="Size">Avatar scale multiplier for gizmo sizing.</param>
        public void DrawGizmos(BasisLocalBoneControl Control)
        {
            Vector3 BonePosition = Control.OutgoingWorldData.position;
            if (Control.HasTarget && Control.HasLineDraw)
            {
                BasisGizmoManager.UpdateLineGizmo(Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position);
            }
        }
        /// <summary>
        /// Creates a sphere-mesh gizmo whose non-uniform scale renders as an ellipsoid
        /// representing one role's calibration acceptance region. The mesh is unit
        /// diameter, so per-frame scale will be in meters across each axis.
        /// </summary>
        public void AddCalibrationRegion(string Name, BasisBoneTrackedRole role, float expectedHeight, float expectedLateral, float heightSigma, float lateralSigma, Color color)
        {
            // Initial pose is a placeholder; DrawGizmos overwrites it next frame from
            // ConstellationDebug.BodyOrigin/BodyRotation. Scale of 1 keeps the placeholder
            // visually inert if the first DrawGizmos pass is delayed.
            if (BasisGizmoManager.CreateSphereGizmo(Name, out int LinkedID, Vector3.zero, 1f, color))
            {
                GizmoBones.Add(new GizmoBone
                {
                    GizmoReference = LinkedID,
                    Control = role,
                    ExpectedHeight = expectedHeight,
                    ExpectedLateral = expectedLateral,
                    HeightSigma = heightSigma,
                    LateralSigma = lateralSigma,
                });
            }
        }
    }
}
