using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisRemoteBoneDriver
    {
        //figures out how to get the mouth bone and eye position
        public int ControlsLength;
        public BasisRemotePlayer RemotePlayer;
        public Transform RemotePlayerTransform;
        public Transform HeadAvatar;
        public Transform HipsAvatar;
        public BasisRemoteBoneControl Head;
        public BasisRemoteBoneControl Hips;
        public BasisRemoteBoneControl Mouth;
        public bool HasHead;
        public bool HasHips;
        [SerializeField]
        public BasisRemoteBoneControl[] Controls;
        [SerializeField]
        public BasisBoneTrackedRole[] trackedRoles;
        public bool HasControls = false;
        public static float DefaultGizmoSize = 0.05f;
        public void InitializeRemote()
        {
            FindBone(out Head, BasisBoneTrackedRole.Head);
            FindBone(out Hips, BasisBoneTrackedRole.Hips);
            if (Head != null)
            {
                Head.HasTracked = BasisHasTracked.HasTracker;
            }
            if (Hips != null)
            {
                Hips.HasTracked = BasisHasTracked.HasTracker;
            }
            FindBone(out Mouth, BasisBoneTrackedRole.Mouth);
        }
        public void CalculateBoneData()
        {
            Vector3 RRT = RemotePlayerTransform.position;
            if (Head.HasBone && HasHead)
            {
                HeadAvatar.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                Head.IncomingData.position = Position - RRT;
                Head.IncomingData.rotation = Rotation;
            }
            if (Hips.HasBone && HasHips)
            {
                HipsAvatar.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                Hips.IncomingData.position = Position - RRT;
                Hips.IncomingData.rotation = Rotation;
            }
        }
        public void OnCalibration(BasisRemotePlayer remotePlayer)
        {
            HeadAvatar = RemotePlayer.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Head);
            HasHead = HeadAvatar != null;
            HipsAvatar = RemotePlayer.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
            HasHips = HipsAvatar != null;
            this.RemotePlayer = remotePlayer;
            this.RemotePlayerTransform = RemotePlayer.transform;
        }
        public bool FindBone(out BasisRemoteBoneControl control, BasisBoneTrackedRole Role)
        {
            int Index = Array.IndexOf(trackedRoles, Role);

            if (Index >= 0 && Index < ControlsLength)
            {
                control = Controls[Index];
                return true;
            }
            control = new BasisRemoteBoneControl();
            return false;
        }
        public void SimulateAndApplyRemote(BasisPlayer Player)
        {
            Player.OnPreSimulateBones?.Invoke();
            SimulateRemote();
        }
        public void SimulateRemote()
        {
            // sequence all other devices to run at the same time
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].ComputeMovementRemote();
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
        public bool FindTrackedRole(BasisRemoteBoneControl control, out BasisBoneTrackedRole Role)
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
        public void DrawGizmos(BasisRemoteBoneControl Control)
        {
            if (Control.HasBone)
            {
                Vector3 BonePosition = Control.OutGoingData.position;
                if (Control.HasTarget)
                {
                    if (Control.HasLineDraw)
                    {
                        BasisGizmoManager.UpdateLineGizmo(Control.LineDrawIndex, BonePosition, Control.Target.OutGoingData.position);
                    }
                }
                if (FindTrackedRole(Control, out BasisBoneTrackedRole Role))
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
                    if (FindTrackedRole(Control, out BasisBoneTrackedRole role))
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
        public void CreateInitialArrays(bool IsLocal)
        {
            trackedRoles = new BasisBoneTrackedRole[] { };
            Controls = new BasisRemoteBoneControl[] { };
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
            List<BasisRemoteBoneControl> newControls = new List<BasisRemoteBoneControl>();
            List<BasisBoneTrackedRole> Roles = new List<BasisBoneTrackedRole>();
            for (int Index = 0; Index < Length; Index++)
            {
                SetupRole(Index, Colors[Index], out BasisRemoteBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            if (IsLocal == false)
            {
                SetupRole(22, Color.blue, out BasisRemoteBoneControl Control, out BasisBoneTrackedRole Role);
                newControls.Add(Control);
                Roles.Add(Role);
            }
            AddRange(newControls.ToArray(), Roles.ToArray());
            HasControls = true;
            InitializeGizmos();
        }
        public void AddRange(BasisRemoteBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            Controls = Controls.Concat(newControls).ToArray();
            trackedRoles = trackedRoles.Concat(newRoles).ToArray();
            ControlsLength = Controls.Length;
        }
        public void SetupRole(int Index, Color Color, out BasisRemoteBoneControl BasisBoneControl, out BasisBoneTrackedRole role)
        {
            role = (BasisBoneTrackedRole)Index;
            BasisBoneControl = new BasisRemoteBoneControl();
            BasisBoneControl.Initialize();
            FillOutBasicInformation(BasisBoneControl, role.ToString(), Color);
        }
        public void FillOutBasicInformation(BasisRemoteBoneControl Control, string Name, Color Color)
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
                BasisRemoteBoneControl Control = Controls[Index];
                BasisBoneTrackedRole Role = trackedRoles[Index];
                if (State)
                {
                    if (Role == BasisBoneTrackedRole.CenterEye && Application.isEditor == false)
                    {
                        continue;
                    }
                    Vector3 BonePosition = Control.OutGoingData.position;
                    if (Control.HasTarget)
                    {
                        if (BasisGizmoManager.CreateLineGizmo(out Control.LineDrawIndex, BonePosition, Control.Target.OutGoingData.position, 0.03f, Control.Color))
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
        public void CreateRotationalLock(BasisRemoteBoneControl addToBone, BasisRemoteBoneControl target)
        {
            addToBone.Target = target;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;
            addToBone.Target = target;
            addToBone.HasTarget = target != null;
        }
    }
}
