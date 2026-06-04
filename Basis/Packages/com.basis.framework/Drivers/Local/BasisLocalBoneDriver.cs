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

        // ===== Burst/Jobs simulation backing store (canonical bone data) =====
        // Inputs (incoming pose, has-tracker flags, target index, etc). Written by bone-control
        // setters/devices, read by the chain job. Internal so BasisLocalBoneControl maps into it.
        internal NativeArray<BasisBoneSimInput> _simInputs;
        // Outputs/persistent state (outgoing pose, last-run, world). Source of truth — no scatter.
        internal NativeArray<BasisBoneSimState> _simStates;
        // Cached raw pointers into the stores above — bone-control accessors index these to skip the
        // NativeArray indexer's per-access bounds/safety wrappers. Refreshed on every (re)alloc.
        internal unsafe BasisBoneSimInput* _simInputsPtr;
        internal unsafe BasisBoneSimState* _simStatesPtr;

        // role -> Controls index, rebuilt alongside chain data. -1 = role not present.
        private static readonly int RoleCount = Enum.GetValues(typeof(BasisBoneTrackedRole)).Length;
        private int[] _roleToIndex;

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

        // Per-skeleton-chain gizmo state. Each chain renders as a single multi-point
        // LineRenderer with one position per resolved bone, fading through a Gradient
        // built from the per-bone colors (one key per bone). Cuts the skeleton-line
        // gizmo count from ~20 (one per bone-with-parent) down to 5 (one per chain).
        private const int SkeletonChainCount = 5;
        private readonly int[] _skeletonChainIds = { -1, -1, -1, -1, -1 };
        private readonly BasisLocalBoneControl[][] _skeletonChainControls = new BasisLocalBoneControl[SkeletonChainCount][];
        private readonly Vector3[][] _skeletonChainPositions = new Vector3[SkeletonChainCount][];
        private bool _skeletonChainsCreated;

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

        // Index into SkeletonChainOrders / SkeletonChainNames lines up with the
        // per-chain runtime state arrays above.
        private static readonly BasisBoneTrackedRole[][] SkeletonChainOrders =
        {
            SpineChainOrder, LeftArmChainOrder, RightArmChainOrder, LeftLegChainOrder, RightLegChainOrder,
        };
        private static readonly string[] SkeletonChainNames =
        {
            "Spine", "LeftArm", "RightArm", "LeftLeg", "RightLeg",
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

            if (seedLastRunFromOutgoing)
            {
                SeedLastRunFromOutgoing();
            }

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

            JobHandle worldHandle = new BasisBoneWorldDestinationJob
            {
                States = _simStates,
                ParentMatrix = parentMatrix44,
                ParentRotation = parentRot,
            }.Schedule(ControlsLength, 8, final);
            worldHandle.Complete();
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
            unsafe
            {
                _simInputsPtr = (BasisBoneSimInput*)_simInputs.GetUnsafePtr();
                _simStatesPtr = (BasisBoneSimState*)_simStates.GetUnsafePtr();
            }
            _nativeCapacity = ControlsLength;
            _nativeAllocated = true;
            _chainsBuilt = false;
            WireControlsAndInitStore();
        }

        // Links each control to its store slot and seeds valid (identity) rotations, since a
        // default-initialized NativeArray leaves quaternions at (0,0,0,0).
        private void WireControlsAndInitStore()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                c.Owner = this;
                c.Index = i;

                BasisBoneSimInput inp = _simInputs[i];
                inp.IncomingRotation = quaternion.identity;
                inp.InverseOffsetRotation = quaternion.identity;
                inp.TargetIndex = -1;
                _simInputs[i] = inp;

                BasisBoneSimState st = _simStates[i];
                st.OutgoingRotation = quaternion.identity;
                st.LastRunRotation = quaternion.identity;
                st.OutgoingWorldRotation = quaternion.identity;
                _simStates[i] = st;
            }
        }

        private void BuildChainData()
        {
            // Cache target indices and sync them into the store (no per-frame pack does this now).
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                int targetIdx = c.TargetIndex;

                BasisBoneSimInput inp = _simInputs[i];
                inp.TargetIndex = targetIdx;
                inp.HasTarget = (targetIdx >= 0) ? (byte)1 : (byte)0;
                _simInputs[i] = inp;
            }

            // Cache role -> index for the public request API.
            if (_roleToIndex == null || _roleToIndex.Length < RoleCount)
            {
                _roleToIndex = new int[RoleCount];
            }
            for (int r = 0; r < _roleToIndex.Length; r++)
            {
                _roleToIndex[r] = -1;
            }
            for (int i = 0; i < ControlsLength; i++)
            {
                int role = (int)trackedRoles[i];
                if (role >= 0 && role < _roleToIndex.Length)
                {
                    _roleToIndex[role] = i;
                }
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

        // Seeds each bone's "last run" pose from its current outgoing pose, skipping
        // interpolation for this frame (used by SimulateWithoutLerp).
        private void SeedLastRunFromOutgoing()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisBoneSimState s = _simStates[i];
                s.LastRunPosition = s.OutgoingPosition;
                s.LastRunRotation = s.OutgoingRotation;
                _simStates[i] = s;
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
        /// Resolves a tracked role to its <see cref="Controls"/> index, or -1 if absent.
        /// Available once chain data has been built (post-calibration).
        /// </summary>
        public int GetBoneIndex(BasisBoneTrackedRole role)
        {
            int r = (int)role;
            if (_roleToIndex != null && r >= 0 && r < _roleToIndex.Length)
            {
                return _roleToIndex[r];
            }
            return -1;
        }

        /// <summary>
        /// Requests the latest simulated pose for a bone by role without dereferencing the
        /// managed <see cref="BasisLocalBoneControl"/>. Reads the native sim store once it has
        /// been populated; before the first simulate it falls back to the managed control.
        /// </summary>
        public bool TryGetSimState(BasisBoneTrackedRole role, out BasisBoneSimState state)
        {
            int index = GetBoneIndex(role);
            if (index < 0 || index >= ControlsLength)
            {
                state = default;
                return false;
            }

            if (_nativeAllocated && index < _simStates.Length)
            {
                state = _simStates[index];
                return true;
            }

            BasisLocalBoneControl c = Controls[index];
            state = new BasisBoneSimState
            {
                OutgoingPosition = c.OutGoingData.position,
                OutgoingRotation = c.OutGoingData.rotation,
                LastRunPosition = c.LastRunData.position,
                LastRunRotation = c.LastRunData.rotation,
                OutgoingWorldPosition = c.OutgoingWorldData.position,
                OutgoingWorldRotation = c.OutgoingWorldData.rotation,
            };
            return true;
        }

        /// <summary>
        /// Exposes the native per-bone state buffer for systems that schedule their own Burst
        /// job against bone poses. Returns false until the native store is allocated. The buffer
        /// is refreshed each <see cref="Simulate"/>; index entries via <see cref="GetBoneIndex"/>.
        /// </summary>
        public bool TryGetSimStates(out NativeArray<BasisBoneSimState> states)
        {
            if (_nativeAllocated)
            {
                states = _simStates;
                return true;
            }
            states = default;
            return false;
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
            if (SMModuleDebugOptions.UseSkeletonLines && _skeletonChainsCreated)
            {
                for (int chainIdx = 0; chainIdx < SkeletonChainCount; chainIdx++)
                {
                    UpdateSkeletonChainPositions(chainIdx);
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
            // Allocate/grow the store and wire the new controls to it before anything writes poses.
            EnsureNativeAllocated();
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
                ResetSkeletonChainGizmos();
                return;
            }

            float Size = BasisHeightDriver.ScaledToMatchValue;
            BuildSkeletonChainGizmos(Size);

            // Lines are created visible — match the sub-toggle's current state.
            ApplySkeletonLineVisibility();
            RebuildCalibrationSpheres();
        }

        /// <summary>
        /// Builds one multi-point line gizmo per anatomical chain (spine, arms, legs).
        /// Each chain's color sampling uses a Gradient with a stop per resolved bone so
        /// the line fades through the bones' colors instead of being a single tint —
        /// preserves the per-bone rainbow with far fewer LineRenderers.
        /// </summary>
        private void BuildSkeletonChainGizmos(float size)
        {
            // Defensive: if any chain ID is still live, destroy it first so a
            // re-entrant build doesn't leak a LineRenderer in the manager's pool.
            for (int i = 0; i < SkeletonChainCount; i++)
            {
                if (_skeletonChainIds[i] >= 0)
                {
                    BasisGizmoManager.DestroyGizmo(_skeletonChainIds[i]);
                    _skeletonChainIds[i] = -1;
                }
            }

            float lineWidth = 0.05f * size;

            for (int chainIdx = 0; chainIdx < SkeletonChainCount; chainIdx++)
            {
                BasisBoneTrackedRole[] order = SkeletonChainOrders[chainIdx];
                var resolved = new List<BasisLocalBoneControl>(order.Length);
                for (int i = 0; i < order.Length; i++)
                {
                    if (FindBone(out BasisLocalBoneControl c, order[i]))
                    {
                        resolved.Add(c);
                    }
                }

                if (resolved.Count < 2)
                {
                    _skeletonChainControls[chainIdx] = null;
                    _skeletonChainPositions[chainIdx] = null;
                    _skeletonChainIds[chainIdx] = -1;
                    continue;
                }

                BasisLocalBoneControl[] controls = resolved.ToArray();
                Vector3[] positions = new Vector3[controls.Length];
                for (int i = 0; i < controls.Length; i++)
                {
                    positions[i] = controls[i].OutgoingWorldData.position;
                }

                if (BasisGizmoManager.CreateLineGizmo(
                        $"Skeleton_{SkeletonChainNames[chainIdx]}",
                        out int id,
                        positions,
                        lineWidth,
                        Color.white,
                        loop: false))
                {
                    _skeletonChainIds[chainIdx] = id;
                    _skeletonChainControls[chainIdx] = controls;
                    _skeletonChainPositions[chainIdx] = positions;
                    ApplySkeletonChainGradient(chainIdx);
                }
                else
                {
                    _skeletonChainControls[chainIdx] = null;
                    _skeletonChainPositions[chainIdx] = null;
                    _skeletonChainIds[chainIdx] = -1;
                }
            }
            _skeletonChainsCreated = true;
        }

        private void ApplySkeletonChainGradient(int chainIdx)
        {
            BasisLocalBoneControl[] controls = _skeletonChainControls[chainIdx];
            int id = _skeletonChainIds[chainIdx];
            if (controls == null || id < 0)
            {
                return;
            }
            int count = controls.Length;

            var colorKeys = new GradientColorKey[count];
            var alphaKeys = new GradientAlphaKey[count];
            float denom = count > 1 ? count - 1 : 1;
            for (int i = 0; i < count; i++)
            {
                float t = i / denom;
                Color c = controls[i].Color;
                colorKeys[i] = new GradientColorKey(c, t);
                alphaKeys[i] = new GradientAlphaKey(c.a, t);
            }
            var gradient = new Gradient();
            gradient.SetKeys(colorKeys, alphaKeys);
            BasisGizmoManager.SetLineGizmoGradient(id, gradient);
        }

        private void ResetSkeletonChainGizmos()
        {
            for (int i = 0; i < SkeletonChainCount; i++)
            {
                _skeletonChainIds[i] = -1;
                _skeletonChainControls[i] = null;
                _skeletonChainPositions[i] = null;
            }
            _skeletonChainsCreated = false;
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
        /// Toggles visibility on each skeleton-chain gizmo. Mirror of
        /// <see cref="ApplyCalibrationSphereVisibility"/> — flag alone gates the
        /// per-frame UpdateLineGizmo call, but the line GameObjects need an
        /// explicit hide/show to actually appear/disappear.
        /// </summary>
        public void ApplySkeletonLineVisibility()
        {
            bool visible = SMModuleDebugOptions.UseSkeletonLines;
            for (int i = 0; i < SkeletonChainCount; i++)
            {
                int id = _skeletonChainIds[i];
                if (id >= 0)
                {
                    BasisGizmoManager.SetGizmoActive(id, visible);
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
            addToBone.TargetIndex = target.Index;
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
        /// Pushes the current bone positions for one skeleton chain into its
        /// multi-point LineRenderer. Cheap per-frame call — just reads the cached
        /// control list and updates the shared positions buffer.
        /// </summary>
        private void UpdateSkeletonChainPositions(int chainIdx)
        {
            BasisLocalBoneControl[] controls = _skeletonChainControls[chainIdx];
            Vector3[] positions = _skeletonChainPositions[chainIdx];
            int id = _skeletonChainIds[chainIdx];
            if (controls == null || positions == null || id < 0)
            {
                return;
            }
            for (int i = 0; i < controls.Length; i++)
            {
                positions[i] = controls[i].OutgoingWorldData.position;
            }
            BasisGizmoManager.UpdateLineGizmo(id, positions);
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
