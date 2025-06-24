using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public abstract class BasisBaseBoneDriver
    {
        //figures out how to get the mouth bone and eye position
        public int ControlsLength;
        [SerializeField]
        public BasisBoneControl[] Controls;
        [SerializeField]
        public BasisBoneTrackedRole[] trackedRoles;
        public bool HasControls = false;
        /// <summary>
        /// call this after updating the bone data
        /// </summary>
        public void Simulate(float deltaTime, Transform transform)
        {
            // sequence all other devices to run at the same time
            Matrix4x4 parentMatrix = transform.localToWorldMatrix;
            Quaternion Rotation = transform.rotation;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].ComputeMovement(parentMatrix, Rotation, deltaTime);
            }
            if (BasisGizmoManager.UseGizmos)
            {
                DrawGizmos();
            }
        }
        public void SimulateWithoutLerp(Transform transform)
        {
            // sequence all other devices to run at the same time
            float DeltaTime = Time.deltaTime;
            Matrix4x4 parentMatrix = transform.localToWorldMatrix;
            Quaternion Rotation = transform.rotation;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].LastRunData.position = Controls[Index].OutGoingData.position;
                Controls[Index].LastRunData.rotation = Controls[Index].OutGoingData.rotation;
                Controls[Index].ComputeMovement(parentMatrix, Rotation, DeltaTime);
            }
            if (BasisGizmoManager.UseGizmos)
            {
                DrawGizmos();
            }
        }
        public void DrawGizmos()
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                DrawGizmos(Controls[Index]);
            }
        }
        public void SimulateAndApply(BasisPlayer Player, float deltaTime)
        {
            Player.OnPreSimulateBones?.Invoke();
            Simulate(deltaTime, Player.transform);
        }
        public void SimulateAndApplyWithoutLerp(BasisPlayer Player)
        {
            Player.OnPreSimulateBones?.Invoke();
            SimulateWithoutLerp(Player.transform);
        }
        public void SimulateWorldDestinations(Matrix4x4 localToWorldMatrix, Quaternion Rotation)
        {
         //   Matrix4x4 parentMatrix = transform.localToWorldMatrix;
          //  Quaternion Rotation = transform.rotation;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                // Apply local transform to parent's world transform
                Controls[Index].OutgoingWorldData.position = localToWorldMatrix.MultiplyPoint3x4(Controls[Index].OutGoingData.position);

                // Transform rotation via quaternion multiplication
                Controls[Index].OutgoingWorldData.rotation = Rotation * Controls[Index].OutGoingData.rotation;
            }
        }
        public void RemoveAllListeners()
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].OnHasRigChanged = null;
                Controls[Index].WeightsChanged = null;
            }
        }
        public void AddRange(BasisBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            Controls = Controls.Concat(newControls).ToArray();
            trackedRoles = trackedRoles.Concat(newRoles).ToArray();
            ControlsLength = Controls.Length;
        }
        public bool FindBone(out BasisBoneControl control, BasisBoneTrackedRole Role)
        {
            int Index = Array.IndexOf(trackedRoles, Role);

            if (Index >= 0 && Index < ControlsLength)
            {
                control = Controls[Index];
                return true;
            }
            control = new BasisBoneControl();
            return false;
        }
        public bool FindTrackedRole(BasisBoneControl control, out BasisBoneTrackedRole Role)
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
        public void CreateInitialArrays(Transform Parent, bool IsLocal)
        {
            trackedRoles = new BasisBoneTrackedRole[] { };
            Controls = new BasisBoneControl[] { };
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
            List<BasisBoneControl> newControls = new List<BasisBoneControl>();
            List<BasisBoneTrackedRole> Roles = new List<BasisBoneTrackedRole>();
            for (int Index = 0; Index < Length; Index++)
            {
                SetupRole(Index, Parent, Colors[Index], out BasisBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            if (IsLocal == false)
            {
                SetupRole(22, Parent, Color.blue, out BasisBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            AddRange(newControls.ToArray(), Roles.ToArray());
            HasControls = true;
            InitializeGizmos();
        }
        public void SetupRole(int Index, Transform Parent, Color Color, out BasisBoneControl BasisBoneControl, out BasisBoneTrackedRole role)
        {
            role = (BasisBoneTrackedRole)Index;
            BasisBoneControl = new BasisBoneControl();
            BasisBoneControl.Initialize();
            FillOutBasicInformation(BasisBoneControl, role.ToString(), Color);
        }
        public void InitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;
        }
        public void DeInitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
        }
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone Driver Gizmos", BasisDebug.LogTag.Gizmo);
            // BasisDebug.Log("updating State!");
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                BasisBoneControl Control = Controls[Index];
                BasisBoneTrackedRole Role = trackedRoles[Index];
                if (State)
                {
                    if (Role == BasisBoneTrackedRole.CenterEye && Application.isEditor == false)
                    {
                        continue;
                    }
                    Vector3 BonePosition = Control.OutgoingWorldData.position;
                    if (Control.HasTarget)
                    {
                        if (BasisGizmoManager.CreateLineGizmo(out Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position, 0.03f, Control.Color))
                        {
                            Control.HasLineDraw = true;
                        }
                    }
                    if (BasisGizmoManager.CreateSphereGizmo(out Control.GizmoReference, BonePosition, DefaultGizmoSize * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale, Control.Color))
                    {
                        Control.HasGizmo = true;
                    }
                }
                else
                {
                    Control.HasGizmo = false;
                    Control.TposeHasGizmo = false;
                }
            }
        }
        public void FillOutBasicInformation(BasisBoneControl Control, string Name, Color Color)
        {
            Control.name = Name;
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
        public void CreateRotationalLock(BasisBoneControl addToBone, BasisBoneControl target, float lerpAmount, float positional = 40)
        {
            addToBone.Target = target;
            addToBone.LerpAmountNormal = lerpAmount;
            addToBone.LerpAmountFastMovement = lerpAmount * 4;
            addToBone.AngleBeforeSpeedup = 25f;
            addToBone.HasRotationalTarget = target != null;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;
            addToBone.Target = target;
            addToBone.LerpAmount = positional;
            addToBone.HasTarget = target != null;
        }
        public static Vector3 ConvertToAvatarSpaceInitial(Animator animator, Vector3 WorldSpace)// out Vector3 FloorPosition
        {
            return BasisHelpers.ConvertToLocalSpace(WorldSpace, animator.transform.position);
        }
        public static Vector3 ConvertToWorldSpace(Vector3 WorldSpace, Vector3 LocalSpace)
        {
            return BasisHelpers.ConvertFromLocalSpace(LocalSpace, WorldSpace);
        }
        public static float DefaultGizmoSize = 0.05f;
        public static float HandGizmoSize = 0.015f;
        public void DrawGizmos(BasisBoneControl Control)
        {
            if (Control.HasBone)
            {
                Vector3 BonePosition = Control.OutgoingWorldData.position;
                if (Control.HasTarget)
                {
                    if (Control.HasLineDraw)
                    {
                        BasisGizmoManager.UpdateLineGizmo(Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position);
                    }
                }
                if (BasisLocalPlayer.Instance.LocalBoneDriver.FindTrackedRole(Control, out BasisBoneTrackedRole Role))
                {
                    if (Role == BasisBoneTrackedRole.CenterEye)
                    {
                        //ignoring center eye to stop you having issues in vr
                        return;
                    }
                    if (Control.HasGizmo)
                    {
                        if (BasisGizmoManager.UpdateSphereGizmo(Control.GizmoReference, BonePosition) == false)
                        {
                            Control.HasGizmo = false;
                        }
                    }
                }
                if (BasisLocalPlayer.Instance.LocalAvatarDriver.CurrentlyTposing)
                {
                    if (BasisLocalPlayer.Instance.LocalBoneDriver.FindTrackedRole(Control, out BasisBoneTrackedRole role))
                    {
                        if (Role == BasisBoneTrackedRole.CenterEye)
                        {
                            //ignoring center eye to stop you having issues in vr
                            return;
                        }
                        if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                        {
                            if (Control.TposeHasGizmo)
                            {
                                if (BasisGizmoManager.UpdateSphereGizmo(Control.TposeGizmoReference, BonePosition) == false)
                                {
                                    Control.TposeHasGizmo = false;
                                }
                            }
                            else
                            {
                                if (BasisGizmoManager.CreateSphereGizmo(out Control.TposeGizmoReference, BonePosition, BasisAvatarIKStageCalibration.MaxDistanceBeforeMax(role) * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale, Control.Color))
                                {
                                    Control.TposeHasGizmo = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        public class OrderedDelegate
        {
            private struct Entry
            {
                public int Priority;
                public Action Action;

                public Entry(int priority, Action action)
                {
                    Priority = priority;
                    Action = action;
                }
            }

            private readonly List<Entry> entries = new List<Entry>();
            private List<int> executionOrder = new List<int>();
            public int Count { get; private set; }

            public void AddAction(int priority, Action action)
            {
                entries.Add(new Entry(priority, action));
                RebuildExecutionOrder();
                Count = executionOrder.Count;
            }

            public void RemoveAction(int priority, Action action)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Priority == priority && entries[i].Action == action)
                    {
                    //    BasisDebug.Log("removing Action at " + priority);
                        entries.RemoveAt(i);
                        RebuildExecutionOrder();
                        Count = executionOrder.Count;
                        return;
                    }
                }
            }

            private void RebuildExecutionOrder()
            {
                executionOrder.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    executionOrder.Add(i);
                }

                executionOrder.Sort((a, b) => entries[a].Priority.CompareTo(entries[b].Priority));
            }

            public void Invoke()
            {
                for (int Index = 0; Index < Count; Index++)
                {
                    int index = executionOrder[Index];
                    entries[index].Action?.Invoke();
                }
            }
        }
    }
}
