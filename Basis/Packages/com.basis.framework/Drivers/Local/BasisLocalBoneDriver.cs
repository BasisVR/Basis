using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Avatar;
using Unity.Burst;
using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisLocalBoneDriver
    {
        public static BasisLocalBoneControl HeadControl;
        public static BasisLocalBoneControl HipsControl;
        public static BasisLocalBoneControl EyeControl;
        public static BasisLocalBoneControl MouthControl;
        public static BasisLocalBoneControl LeftFootControl;
        public static BasisLocalBoneControl RightFootControl;
        public static BasisLocalBoneControl LeftHandControl;
        public static BasisLocalBoneControl RightHandControl;
        public static BasisLocalBoneControl ChestControl;
        public static BasisLocalBoneControl LeftLowerLegControl;
        public static BasisLocalBoneControl RightLowerLegControl;
        public static BasisLocalBoneControl LeftLowerArmControl;
        public static BasisLocalBoneControl RightLowerArmControl;

        public static BasisLocalBoneControl LeftToeControl;
        public static BasisLocalBoneControl RightToeControl;
        public static bool HasEye;
        //figures out how to get the mouth bone and eye position
        public int ControlsLength;
        [SerializeField]
        public BasisLocalBoneControl[] Controls;
        [SerializeField]
        public BasisBoneTrackedRole[] trackedRoles;
        public bool HasControls = false;
        public static float DefaultGizmoSize = 0.05f;
        public static float HandGizmoSize = 0.015f;
        public void Initialize()
        {
            HasEye = FindBone(out EyeControl, BasisBoneTrackedRole.CenterEye);
            FindBone(out HeadControl, BasisBoneTrackedRole.Head);
            FindBone(out HipsControl, BasisBoneTrackedRole.Hips);
            FindBone(out MouthControl, BasisBoneTrackedRole.Mouth);
            FindBone(out LeftFootControl, BasisBoneTrackedRole.LeftFoot);
            FindBone(out RightFootControl, BasisBoneTrackedRole.RightFoot);
            FindBone(out LeftHandControl, BasisBoneTrackedRole.LeftHand);
            FindBone(out RightHandControl, BasisBoneTrackedRole.RightHand);
            FindBone(out ChestControl, BasisBoneTrackedRole.Chest);
            FindBone(out LeftLowerLegControl, BasisBoneTrackedRole.LeftLowerLeg);
            FindBone(out RightLowerLegControl, BasisBoneTrackedRole.RightLowerLeg);
            FindBone(out LeftLowerArmControl, BasisBoneTrackedRole.LeftLowerArm);
            FindBone(out RightLowerArmControl, BasisBoneTrackedRole.RightLowerArm);
            FindBone(out LeftToeControl, BasisBoneTrackedRole.LeftToes);
            FindBone(out RightToeControl, BasisBoneTrackedRole.RightToes);
        }
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
                ComputeMovementLocal(parentMatrix, Rotation, deltaTime, Controls[Index]);
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
                ComputeMovementLocal(parentMatrix, Rotation, DeltaTime, Controls[Index]);
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
            Simulate(deltaTime, Player.PlayerSelf);
        }
        public void SimulateAndApplyWithoutLerp(BasisPlayer Player)
        {
            Player.OnPreSimulateBones?.Invoke();
            SimulateWithoutLerp(Player.PlayerSelf);
        }
        public void SimulateWorldDestinations(Matrix4x4 localToWorldMatrix)
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                // Apply local transform to parent's world transform
                Controls[Index].OutgoingWorldData.position = localToWorldMatrix.MultiplyPoint3x4(Controls[Index].OutGoingData.position);
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
        public void AddRange(BasisLocalBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            Controls = Controls.Concat(newControls).ToArray();
            trackedRoles = trackedRoles.Concat(newRoles).ToArray();
            ControlsLength = Controls.Length;
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
            BasisBoneControl.OutgoingWorldData.position = Vector3.zero;
            BasisBoneControl.OutgoingWorldData.rotation = Quaternion.identity;
            BasisBoneControl.LastRunData.position = BasisBoneControl.OutGoingData.position;
            BasisBoneControl.LastRunData.rotation = BasisBoneControl.OutGoingData.rotation;
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
                BasisLocalBoneControl Control = Controls[Index];
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
        public void FillOutBasicInformation(BasisLocalBoneControl Control, string Name, Color Color)
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
        public void CreateRotationalLock(BasisLocalBoneControl addToBone, BasisLocalBoneControl target, float lerpAmount, float positional = 40)
        {
            addToBone.Target = target;
            addToBone.LerpAmountNormal = lerpAmount;
            addToBone.LerpAmountFastMovement = lerpAmount * 4;
            addToBone.AngleBeforeSpeedup = 25f;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;
            addToBone.Target = target;
            addToBone.LerpAmount = positional;
            addToBone.HasTarget = target != null;
        }
        public static Vector3 ConvertToAvatarSpaceInitial(Transform Transform, Vector3 WorldSpace)// out Vector3 FloorPosition
        {
            return BasisHelpers.ConvertToLocalSpace(WorldSpace, Transform.position);
        }
        public float trackersmooth = 25;
        [BurstCompile]
        public void ComputeMovementLocal(Matrix4x4 parentMatrix, Quaternion Rotation, float DeltaTime, BasisLocalBoneControl addToBone)
        {
            if (addToBone.HasTracked == BasisHasTracked.HasTracker)
            {
                // This needs to be refactored to understand each part of the body and a generic mode.
                // Start off with a distance limiter for the hips.
                // Could also be a step at the end for every targeted type
                if (!addToBone.HasVirtualOverride)
                {
                    // Update the position of the secondary transform to maintain the initial offset
                    addToBone.OutGoingData.position = Vector3.Lerp(addToBone.OutGoingData.position, addToBone.IncomingData.position + addToBone.IncomingData.rotation * addToBone.InverseOffsetFromBone.position, trackersmooth);

                    // Update the rotation of the secondary transform to maintain the initial offset
                    addToBone.OutGoingData.rotation = Quaternion.Slerp(addToBone.OutGoingData.rotation, addToBone.IncomingData.rotation * addToBone.InverseOffsetFromBone.rotation, trackersmooth);
                }
                else
                {
                    // This is going to the generic always accurate fake skeleton
                    addToBone.OutGoingData.rotation = addToBone.IncomingData.rotation;
                    addToBone.OutGoingData.position = addToBone.IncomingData.position;
                }
            }
            else
            {
                if (!addToBone.HasVirtualOverride)
                {
                    // This is essentially the default behaviour, most of it is normally Virtually Overriden
                    // Relying on a one size fits all shoe is wrong and as of such we barely use this anymore.
                    if (addToBone.HasTarget)
                    {
                        addToBone.OutGoingData.rotation = ApplyLerpToQuaternion(DeltaTime, addToBone.LastRunData.rotation, addToBone.Target.OutGoingData.rotation, addToBone);
                        // Apply the rotation offset using *
                        Vector3 customDirection = addToBone.Target.OutGoingData.rotation * addToBone.ScaledOffset;

                        // Calculate the target outgoing position with the rotated offset
                        Vector3 targetPosition = addToBone.Target.OutGoingData.position + customDirection;

                        float lerpFactor = ClampInterpolationFactor(addToBone.LerpAmount, DeltaTime);

                        // Interpolate between the last position and the target position
                        addToBone.OutGoingData.position = math.lerp(addToBone.LastRunData.position, targetPosition, lerpFactor);
                    }
                }
            }

            addToBone.OutgoingWorldData.position = parentMatrix.MultiplyPoint3x4(addToBone.OutGoingData.position);

            // Transform rotation via quaternion multiplication
            addToBone.OutgoingWorldData.rotation = Rotation * addToBone.OutGoingData.rotation;

            addToBone.LastRunData.position = addToBone.OutGoingData.position;
            addToBone.LastRunData.rotation = addToBone.OutGoingData.rotation;
        }
        [BurstCompile]
        public Quaternion ApplyLerpToQuaternion(float DeltaTime, Quaternion CurrentRotation, Quaternion FutureRotation, BasisLocalBoneControl addToBone)
        {
            // Calculate the dot product once to check similarity between rotations
            float dotProduct = math.dot(CurrentRotation, FutureRotation);

            // If quaternions are nearly identical, skip interpolation
            if (dotProduct > 0.999999f)
            {
                return FutureRotation;
            }

            // Calculate angle difference, avoid acos for very small differences
            float angleDifference = math.acos(math.clamp(dotProduct, -1f, 1f));

            // If the angle difference is very small, skip interpolation
            if (angleDifference < math.EPSILON)
            {
                return FutureRotation;
            }

            // Cached LerpAmount values for normal and fast movement
            float lerpAmountNormal = addToBone.LerpAmountNormal;
            float lerpAmountFastMovement = addToBone.LerpAmountFastMovement;

            // Timing factor for speed-up
            float timing = math.min(angleDifference / addToBone.AngleBeforeSpeedup, 1f);

            // Interpolate between normal and fast movement rates based on angle
            float lerpAmount = lerpAmountNormal + (lerpAmountFastMovement - lerpAmountNormal) * timing;

            // Apply frame-rate-independent lerp factor
            float lerpFactor = ClampInterpolationFactor(lerpAmount, DeltaTime);

            // Perform spherical interpolation (slerp) with the optimized factor
            return math.slerp(CurrentRotation, FutureRotation, lerpFactor);
        }
        [BurstCompile]
        private float ClampInterpolationFactor(float lerpAmount, float DeltaTime)
        {
            // Clamp the interpolation factor to ensure it stays between 0 and 1
            return math.clamp(lerpAmount * DeltaTime, 0f, 1f);
        }
        public void DrawGizmos(BasisLocalBoneControl Control)
        {
            Vector3 BonePosition = Control.OutgoingWorldData.position;
            if (Control.HasTarget)
            {
                if (Control.HasLineDraw)
                {
                    BasisGizmoManager.UpdateLineGizmo(Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position);
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
            if (BasisLocalAvatarDriver.CurrentlyTposing)
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
}
