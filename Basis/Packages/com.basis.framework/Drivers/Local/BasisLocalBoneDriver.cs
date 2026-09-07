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
    [System.Serializable]
    public class BasisLocalBoneDriver
    {
        private const float CalibrationSphereTint = 0.25f;
        public static BasisLocalBoneControl NeckControl, HeadControl, SpineControl, HipsControl, EyeControl;
        public static BasisLocalBoneControl MouthControl, LeftFootControl, RightFootControl, LeftHandControl;
        public static BasisLocalBoneControl RightHandControl, ChestControl, LeftUpperLegControl, RightUpperLegControl;
        public static BasisLocalBoneControl LeftLowerLegControl, RightLowerLegControl, LeftLowerArmControl;
        public static BasisLocalBoneControl RightLowerArmControl, LeftToeControl, RightToeControl, LeftShoulderControl;
        public static BasisLocalBoneControl RightShoulderControl;
        public static bool HasEye;
        public int ControlsLength;
        [SerializeField]
        public BasisLocalBoneControl[] Controls;
        [SerializeField]
        public BasisBoneTrackedRole[] trackedRoles;
        public bool HasControls = false;
        public static float DefaultGizmoSize = 0.035f;
        public static float HandGizmoSize = 0.02f;
        internal NativeArray<BasisBoneSimInput> simInputs;
        internal NativeArray<BasisBoneSimState> simStateStore;
        internal unsafe BasisBoneSimInput* simInputPtr;
        internal unsafe BasisBoneSimState* simStatePtr;
        private static readonly int RoleCount = Enum.GetValues(typeof(BasisBoneTrackedRole)).Length;
        private int[] roleToIndex;
        private NativeArray<int> allChainIndices;
        private bool nativeAllocated;
        // internal: BasisLocalBoneControl.HasStore range-checks its Index against the store, which
        // it reaches through simInputPtr/simStatePtr with no bounds check of its own.
        internal int nativeCapacity;
        private bool chainsBuilt;
        private const int SkeletonChainCount = 5;
        private readonly int[] skeletonChainIds = { -1, -1, -1, -1, -1 };
        private readonly BasisLocalBoneControl[][] skeletonChainControls = new BasisLocalBoneControl[SkeletonChainCount][];
        private readonly Vector3[][] skeletonChainPositions = new Vector3[SkeletonChainCount][];
        private bool skeletonChainsCreated;
        private readonly int[] skeletonChainLabelIds = { -1, -1, -1, -1, -1 };
        private const float LabelBaseScale = 0.02f;
        private static readonly Color SkeletonLabelColor = Color.white;
        private static readonly Color CalibrationLabelColor = new Color(1f, 0.85f, 0.4f, 1f);
        private const float CalibrationBallScale = 1.6f;
        private const float CalibrationBallMinDiameterFrac = 0.06f;
        private const float CalibrationLatchLineWidthFrac = 0.004f;
        private static readonly Color CalibrationLatchColor = new Color(0.4f, 1f, 0.6f, 1f);
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
        private static readonly BasisBoneTrackedRole[][] SkeletonChainOrders =
        {
            SpineChainOrder, LeftArmChainOrder, RightArmChainOrder, LeftLegChainOrder, RightLegChainOrder,
        };
        private static readonly string[] SkeletonChainNames =
        {
            "Spine", "LeftArm", "RightArm", "LeftLeg", "RightLeg",
        };
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
        public void Simulate(float deltaTime, Matrix4x4 parentMatrix)
        {
            RunSimulation(parentMatrix, deltaTime, seedLastRunFromOutgoing: false, instantSnap: false);

        }
        public void SimulateWithoutLerp(Matrix4x4 parentMatrix)
        {
            RunSimulation(parentMatrix, Time.deltaTime, seedLastRunFromOutgoing: true, instantSnap: true);
            if (SMModuleDebugOptions.UseGizmos)
            {
                DrawGizmos();
            }
        }
        private void RunSimulation(Matrix4x4 parentMatrix, float deltaTime, bool seedLastRunFromOutgoing, bool instantSnap)
        {
            if (ControlsLength == 0)
            {
                return;
            }

            EnsureNativeAllocated();
            if (!chainsBuilt)
            {
                BuildChainData();
            }

            if (seedLastRunFromOutgoing)
            {
                SeedLastRunFromOutgoing();
            }

            float4x4 parentMatrix44 = parentMatrix;
            quaternion parentRot = parentMatrix.rotation;
            byte snap = instantSnap ? (byte)1 : (byte)0;

            if (allChainIndices.IsCreated && allChainIndices.Length > 0)
            {
                MakeJob(allChainIndices, parentMatrix44, parentRot, deltaTime, snap).Run();
            }

            new BasisBoneWorldDestinationJob
            {
                States = simStateStore,
                ParentMatrix = parentMatrix44,
                ParentRotation = parentRot,
            }.Run(ControlsLength);
        }
        private BasisBoneSimChainJob MakeJob(NativeArray<int> chain, float4x4 parentMatrix44, quaternion parentRot, float deltaTime, byte instantSnap)
        {
            return new BasisBoneSimChainJob
            {
                ChainIndices = chain,
                Inputs = simInputs,
                States = simStateStore,
                ParentMatrix = parentMatrix44,
                ParentRotation = parentRot,
                DeltaTime = deltaTime,
                InstantSnap = instantSnap,
            };
        }
        private void EnsureNativeAllocated()
        {
            if (nativeAllocated && nativeCapacity == ControlsLength)
            {
                return;
            }

            DisposeNative();
            simInputs = new NativeArray<BasisBoneSimInput>(ControlsLength, Allocator.Persistent);
            simStateStore = new NativeArray<BasisBoneSimState>(ControlsLength, Allocator.Persistent);
            unsafe
            {
                simInputPtr = (BasisBoneSimInput*)simInputs.GetUnsafePtr();
                simStatePtr = (BasisBoneSimState*)simStateStore.GetUnsafePtr();
            }
            nativeCapacity = ControlsLength;
            nativeAllocated = true;
            chainsBuilt = false;
            WireControlsAndInitStore();
        }
        private void WireControlsAndInitStore()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                c.Owner = this;
                c.Index = i;

                BasisBoneSimInput inp = simInputs[i];
                inp.IncomingRotation = quaternion.identity;
                inp.InverseOffsetRotation = quaternion.identity;
                inp.TargetIndex = -1;
                simInputs[i] = inp;

                BasisBoneSimState st = simStateStore[i];
                st.OutgoingRotation = quaternion.identity;
                st.LastRunRotation = quaternion.identity;
                st.OutgoingWorldRotation = quaternion.identity;
                simStateStore[i] = st;
            }
        }
        private void BuildChainData()
        {

            for (int i = 0; i < ControlsLength; i++)
            {
                BasisLocalBoneControl c = Controls[i];
                int targetIdx = c.TargetIndex;

                BasisBoneSimInput inp = simInputs[i];
                inp.TargetIndex = targetIdx;
                inp.HasTarget = (targetIdx >= 0) ? (byte)1 : (byte)0;
                simInputs[i] = inp;
            }

            if (roleToIndex == null || roleToIndex.Length < RoleCount)
            {
                roleToIndex = new int[RoleCount];
            }
            for (int r = 0; r < roleToIndex.Length; r++)
            {
                roleToIndex[r] = -1;
            }
            for (int i = 0; i < ControlsLength; i++)
            {
                int role = (int)trackedRoles[i];
                if (role >= 0 && role < roleToIndex.Length)
                {
                    roleToIndex[role] = i;
                }
            }

            DisposeChainArrays();

            HashSet<int> covered = new HashSet<int>();
            List<int> ordered = new List<int>(ControlsLength);
            AppendChainIndices(SpineChainOrder, covered, ordered);
            AppendChainIndices(LeftArmChainOrder, covered, ordered);
            AppendChainIndices(RightArmChainOrder, covered, ordered);
            AppendChainIndices(LeftLegChainOrder, covered, ordered);
            AppendChainIndices(RightLegChainOrder, covered, ordered);

            for (int i = 0; i < ControlsLength; i++)
            {
                if (!covered.Contains(i))
                {
                    ordered.Add(i);
                }
            }
            allChainIndices = ToNativeIntArray(ordered);
            chainsBuilt = true;
        }
        private void AppendChainIndices(BasisBoneTrackedRole[] order, HashSet<int> covered, List<int> ordered)
        {
            for (int i = 0; i < order.Length; i++)
            {
                int idx = Array.IndexOf(trackedRoles, order[i]);
                if (idx >= 0 && idx < ControlsLength && covered.Add(idx))
                {
                    ordered.Add(idx);
                }
            }
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
        private void SeedLastRunFromOutgoing()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                BasisBoneSimState s = simStateStore[i];
                s.LastRunPosition = s.OutgoingPosition;
                s.LastRunRotation = s.OutgoingRotation;
                simStateStore[i] = s;
            }
        }
        public void InvalidateChainData()
        {
            chainsBuilt = false;
        }
        public int GetBoneIndex(BasisBoneTrackedRole role)
        {
            int r = (int)role;
            if (roleToIndex != null && r >= 0 && r < roleToIndex.Length)
            {
                return roleToIndex[r];
            }
            return -1;
        }
        public bool TryGetSimStates(out NativeArray<BasisBoneSimState> states)
        {
            if (nativeAllocated)
            {
                states = simStateStore;
                return true;
            }
            states = default;
            return false;
        }
        public void Dispose()
        {
            DisposeChainArrays();
            if (simInputs.IsCreated) simInputs.Dispose();
            if (simStateStore.IsCreated) simStateStore.Dispose();
            unsafe
            {
                simInputPtr = null;
                simStatePtr = null;
            }
            nativeAllocated = false;
            nativeCapacity = 0;
            chainsBuilt = false;
        }
        private void DisposeNative()
        {
            DisposeChainArrays();
            if (simInputs.IsCreated) simInputs.Dispose();
            if (simStateStore.IsCreated) simStateStore.Dispose();
            unsafe
            {
                simInputPtr = null;
                simStatePtr = null;
            }
            nativeAllocated = false;
            nativeCapacity = 0;
        }
        private void DisposeChainArrays()
        {
            if (allChainIndices.IsCreated) allChainIndices.Dispose();
            chainsBuilt = false;
        }
        public void DrawGizmos()
        {
            bool labels = SMModuleDebugOptions.UseGizmoLabels;
            Vector3 camPos = BasisLocalCameraDriver.Position;
            float labelScale = LabelBaseScale * Mathf.Max(0.01f, BasisHeightDriver.ScaledToMatchValue);

            if (SMModuleDebugOptions.UseSkeletonLines && skeletonChainsCreated)
            {
                for (int chainIdx = 0; chainIdx < SkeletonChainCount; chainIdx++)
                {
                    UpdateSkeletonChainPositions(chainIdx);
                }
                if (labels)
                {
                    UpdateSkeletonChainLabels(camPos, labelScale);
                }
                else
                {
                    DestroySkeletonLabels();
                }
            }
            else
            {
                DestroySkeletonLabels();
            }

            if (SMModuleDebugOptions.UseCalibrationSpheres && GizmoBones.Count > 0 && BasisAvatarIKStageCalibration.TryGetCalibrationVisualizationFrame( out Vector3 bodyOrigin, out Quaternion bodyRot, out float eyeHeight, out _))
            {

                for (int i = 0; i < GizmoBones.Count; i++)
                {
                    GizmoBone GizmoBone = GizmoBones[i];

                    Vector3 localOffset = new Vector3( GizmoBone.ExpectedLateral * eyeHeight, GizmoBone.ExpectedHeight * eyeHeight, 0f);
                    Vector3 priorPos = bodyOrigin + bodyRot * localOffset;

                    Vector3 bonePos = GizmoBone.BoneControl != null ? GizmoBone.BoneControl.OutgoingWorldData.position : Vector3.zero;
                    bool hasBone = GizmoBone.BoneControl != null && bonePos.sqrMagnitude > 1e-8f;
                    Vector3 spherePos = hasBone ? bonePos : priorPos;

                    float latDiameter = 6f * GizmoBone.LateralSigma * eyeHeight;
                    float vertDiameter = 6f * GizmoBone.HeightSigma * eyeHeight;
                    float vizMul = SMModuleCalibration.GetSphereScale(GizmoBone.Control);
                    float ballDiameter = Mathf.Max(Mathf.Max(latDiameter, vertDiameter), CalibrationBallMinDiameterFrac * eyeHeight) * CalibrationBallScale * vizMul;
                    Vector3 scale = Vector3.one * ballDiameter;

                    BasisGizmoManager.UpdateSphereGizmo( GizmoBone.GizmoReference, spherePos, bodyRot, scale);

                    if (hasBone)
                    {
                        float lineWidth = Mathf.Max(0.001f, CalibrationLatchLineWidthFrac * eyeHeight);
                        if (GizmoBone.LineReference <= 0)
                        {
                            BasisGizmoManager.CreateLineGizmo($"CalibLatch_{GizmoBone.Control}", out GizmoBone.LineReference, spherePos, priorPos, lineWidth, CalibrationLatchColor);
                        }
                        else
                        {
                            BasisGizmoManager.UpdateLineGizmo(GizmoBone.LineReference, spherePos, priorPos);
                        }
                    }
                    else if (GizmoBone.LineReference > 0)
                    {
                        BasisGizmoManager.DestroyGizmo(GizmoBone.LineReference);
                        GizmoBone.LineReference = -1;
                    }

                    if (labels)
                    {
                        Quaternion rot = BasisGizmoManager.BillboardRotation(spherePos, camPos);
                        if (GizmoBone.LabelReference <= 0)
                        {
                            BasisGizmoManager.CreateTextGizmo($"CalibLabel_{GizmoBone.Control}", out GizmoBone.LabelReference, spherePos, GizmoBone.Control.ToString(), CalibrationLabelColor);
                        }
                        BasisGizmoManager.UpdateTextGizmo(GizmoBone.LabelReference, spherePos, rot, labelScale, GizmoBone.Control.ToString(), CalibrationLabelColor);
                    }
                    else if (GizmoBone.LabelReference > 0)
                    {
                        BasisGizmoManager.DestroyGizmo(GizmoBone.LabelReference);
                        GizmoBone.LabelReference = -1;
                    }
                }
            }
            else
            {
                DestroyCalibrationLabels();
            }
        }
        private void UpdateSkeletonChainLabels(Vector3 camPos, float labelScale)
        {
            for (int chainIdx = 0; chainIdx < SkeletonChainCount; chainIdx++)
            {
                Vector3[] positions = skeletonChainPositions[chainIdx];
                if (skeletonChainIds[chainIdx] < 0 || positions == null || positions.Length == 0)
                {
                    if (skeletonChainLabelIds[chainIdx] > 0)
                    {
                        BasisGizmoManager.DestroyGizmo(skeletonChainLabelIds[chainIdx]);
                        skeletonChainLabelIds[chainIdx] = -1;
                    }
                    continue;
                }

                Vector3 anchor = positions[positions.Length / 2];
                string text = SkeletonChainNames[chainIdx];
                if (skeletonChainLabelIds[chainIdx] <= 0)
                {
                    BasisGizmoManager.CreateTextGizmo($"SkeletonLabel_{text}", out skeletonChainLabelIds[chainIdx], anchor, text, SkeletonLabelColor);
                }
                Quaternion rot = BasisGizmoManager.BillboardRotation(anchor, camPos);
                BasisGizmoManager.UpdateTextGizmo(skeletonChainLabelIds[chainIdx], anchor, rot, labelScale, text, SkeletonLabelColor);
            }
        }
        private void DestroySkeletonLabels()
        {
            for (int i = 0; i < SkeletonChainCount; i++)
            {
                if (skeletonChainLabelIds[i] > 0)
                {
                    BasisGizmoManager.DestroyGizmo(skeletonChainLabelIds[i]);
                    skeletonChainLabelIds[i] = -1;
                }
            }
        }
        private void DestroyCalibrationLabels()
        {
            for (int i = 0; i < GizmoBones.Count; i++)
            {
                GizmoBone gb = GizmoBones[i];
                if (gb.LabelReference > 0)
                {
                    BasisGizmoManager.DestroyGizmo(gb.LabelReference);
                    gb.LabelReference = -1;
                }
            }
        }
        public void SimulateAndApplyWithoutLerp(BasisLocalPlayer Player)
        {
            Player.OnLateSimulateBones(Player);
            Player.OnRenderSimulateBones(Player);
            Player.ApplyVirtualData(Player);
            SimulateWithoutLerp(BasisLocalPlayer.localToWorldMatrix);
        }
        public void RemoveAllListeners()
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].OnHasRigChanged = null;
            }
            BasisLocalBoneControl.HasEvents = false;
        }
        public void AddRange(BasisLocalBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            Controls = Controls.Concat(newControls).ToArray();
            trackedRoles = trackedRoles.Concat(newRoles).ToArray();
            ControlsLength = Controls.Length;

            EnsureNativeAllocated();
            chainsBuilt = false;
        }
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

                SetupRole(22, Color.blue, out BasisLocalBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            AddRange(newControls.ToArray(), Roles.ToArray());
            HasControls = true;
            InitializeGizmos();
        }
        public void SetupRole(int Index, Color Color, out BasisLocalBoneControl BasisBoneControl, out BasisBoneTrackedRole role)
        {
            role = (BasisBoneTrackedRole)Index;
            BasisBoneControl = new BasisLocalBoneControl();
            FillOutBasicInformation(BasisBoneControl, role, Color);
        }
        private const int RenderGizmosPriority = 250;
        public void InitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;
            BasisLocalPlayer.AfterSimulateOnRender.AddAction(RenderGizmosPriority, RenderGizmos);
        }
        public void DeInitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
            BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(RenderGizmosPriority, RenderGizmos);
        }
        private void RenderGizmos()
        {
            if (!SMModuleDebugOptions.UseGizmos) return;
            DrawGizmos();
        }
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone Driver Gizmos", BasisDebug.LogTag.Gizmo);
            if (!State)
            {

                GizmoBones.Clear();
                ResetSkeletonChainGizmos();
                return;
            }

            float Size = BasisHeightDriver.ScaledToMatchValue;
            BuildSkeletonChainGizmos(Size);

            ApplySkeletonLineVisibility();
            RebuildCalibrationSpheres();
        }
        private void BuildSkeletonChainGizmos(float size)
        {

            for (int i = 0; i < SkeletonChainCount; i++)
            {
                if (skeletonChainIds[i] >= 0)
                {
                    BasisGizmoManager.DestroyGizmo(skeletonChainIds[i]);
                    skeletonChainIds[i] = -1;
                }
            }

            DestroySkeletonLabels();

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
                    skeletonChainControls[chainIdx] = null;
                    skeletonChainPositions[chainIdx] = null;
                    skeletonChainIds[chainIdx] = -1;
                    continue;
                }

                BasisLocalBoneControl[] controls = resolved.ToArray();
                Vector3[] positions = new Vector3[controls.Length];
                for (int i = 0; i < controls.Length; i++)
                {
                    positions[i] = controls[i].OutgoingWorldData.position;
                }

                if (BasisGizmoManager.CreateLineGizmo( $"Skeleton_{SkeletonChainNames[chainIdx]}", out int id, positions, lineWidth, Color.white, loop: false))
                {
                    skeletonChainIds[chainIdx] = id;
                    skeletonChainControls[chainIdx] = controls;
                    skeletonChainPositions[chainIdx] = positions;
                    ApplySkeletonChainGradient(chainIdx);
                }
                else
                {
                    skeletonChainControls[chainIdx] = null;
                    skeletonChainPositions[chainIdx] = null;
                    skeletonChainIds[chainIdx] = -1;
                }
            }
            skeletonChainsCreated = true;
        }
        private void ApplySkeletonChainGradient(int chainIdx)
        {
            BasisLocalBoneControl[] controls = skeletonChainControls[chainIdx];
            int id = skeletonChainIds[chainIdx];
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
                skeletonChainIds[i] = -1;
                skeletonChainControls[i] = null;
                skeletonChainPositions[i] = null;

                skeletonChainLabelIds[i] = -1;
            }
            skeletonChainsCreated = false;
        }
        public void RebuildCalibrationSpheres()
        {

            if (SMModuleDebugOptions.UseGizmos)
            {
                for (int i = 0; i < GizmoBones.Count; i++)
                {
                    BasisGizmoManager.DestroyGizmo(GizmoBones[i].GizmoReference);
                    if (GizmoBones[i].LabelReference > 0)
                    {
                        BasisGizmoManager.DestroyGizmo(GizmoBones[i].LabelReference);
                    }
                    if (GizmoBones[i].LineReference > 0)
                    {
                        BasisGizmoManager.DestroyGizmo(GizmoBones[i].LineReference);
                    }
                }
            }
            GizmoBones.Clear();

            if (!SMModuleDebugOptions.UseGizmos)
            {
                return;
            }

            if (!BasisAvatarIKStageCalibration.TryGetCalibrationVisualizationFrame( out _, out _, out _, out IReadOnlyList<BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior> priors))
            {
                return;
            }

            Dictionary<BasisBoneTrackedRole, BasisLocalBoneControl> controlByRole = new Dictionary<BasisBoneTrackedRole, BasisLocalBoneControl>(ControlsLength);
            for (int i = 0; i < ControlsLength; i++)
            {
                controlByRole[trackedRoles[i]] = Controls[i];
            }

            for (int i = 0; i < priors.Count; i++)
            {
                BasisAvatarIKStageCalibration.ConstellationDebug.DebugPrior prior = priors[i];

                controlByRole.TryGetValue(prior.Role, out BasisLocalBoneControl Control);
                Color regionColor;
                if (Control != null)
                {

                    regionColor = Control.Color * CalibrationSphereTint;
                    regionColor.a = Control.Color.a;
                }
                else
                {
                    regionColor = new Color(CalibrationSphereTint, CalibrationSphereTint, CalibrationSphereTint, 1f);
                }

                AddCalibrationRegion( $"{prior.Role} Calibration Region", prior.Role, Control, prior.ExpectedHeight, prior.ExpectedLateral, prior.HeightSigma, prior.LateralSigma, regionColor);
            }

            ApplyCalibrationSphereVisibility();
        }
        public void ApplyCalibrationSphereVisibility()
        {
            bool visible = SMModuleDebugOptions.UseCalibrationSpheres;
            for (int i = 0; i < GizmoBones.Count; i++)
            {
                BasisGizmoManager.SetGizmoActive(GizmoBones[i].GizmoReference, visible);
                if (GizmoBones[i].LineReference > 0)
                {
                    BasisGizmoManager.SetGizmoActive(GizmoBones[i].LineReference, visible);
                }
            }
        }
        public void ApplySkeletonLineVisibility()
        {
            bool visible = SMModuleDebugOptions.UseSkeletonLines;
            for (int i = 0; i < SkeletonChainCount; i++)
            {
                int id = skeletonChainIds[i];
                if (id >= 0)
                {
                    BasisGizmoManager.SetGizmoActive(id, visible);
                }
            }
        }
        public void FillOutBasicInformation(BasisLocalBoneControl Control, BasisBoneTrackedRole Role, Color Color)
        {
            Control.Role = Role;
            Control.Color = Color;
        }
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
        public void CreateRotationalLock(BasisLocalBoneControl addToBone, BasisLocalBoneControl target)
        {
            addToBone.TargetIndex = target.Index;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;

            chainsBuilt = false;
        }
        public static Vector3 ConvertToAvatarSpaceInitial(Transform Transform, Vector3 WorldSpace)
        {
            Transform.GetPose(out Vector3 origin, out Quaternion rotation);
            return BasisHelpers.ConvertToLocalSpace(WorldSpace, origin, rotation);
        }
        [System.Serializable]
        public class GizmoBone
        {
            public int GizmoReference, LabelReference, LineReference;
            public BasisBoneTrackedRole Control;
            public BasisLocalBoneControl BoneControl;
            public float ExpectedHeight, ExpectedLateral, HeightSigma, LateralSigma;
        }
        [SerializeField]
        public List<GizmoBone> GizmoBones = new List<GizmoBone>();
        private void UpdateSkeletonChainPositions(int chainIdx)
        {
            BasisLocalBoneControl[] controls = skeletonChainControls[chainIdx];
            Vector3[] positions = skeletonChainPositions[chainIdx];
            int id = skeletonChainIds[chainIdx];
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
        public void AddCalibrationRegion(string Name, BasisBoneTrackedRole role, BasisLocalBoneControl control, float expectedHeight, float expectedLateral, float heightSigma, float lateralSigma, Color color)
        {

            if (BasisGizmoManager.CreateSphereGizmo(Name, out int LinkedID, Vector3.zero, 1f, color))
            {
                GizmoBones.Add(new GizmoBone
                {
                    GizmoReference = LinkedID,
                    LineReference = -1,
                    Control = role,
                    BoneControl = control,
                    ExpectedHeight = expectedHeight,
                    ExpectedLateral = expectedLateral,
                    HeightSigma = heightSigma,
                    LateralSigma = lateralSigma,
                });
            }
        }
    }
}
