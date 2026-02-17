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
        /// Simulates all bone controls for a frame using the given parent <paramref name="transform"/> and delta time.
        /// Also draws gizmos when debug rendering is enabled.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update (seconds).</param>
        /// <param name="transform">Parent transform whose <see cref="Transform.localToWorldMatrix"/> seeds world computation.</param>
        public void Simulate(float deltaTime, Matrix4x4 parentMatrix)
        {
            // sequence all other devices to run at the same time
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].ComputeMovementLocal(parentMatrix, deltaTime);
            }
            if (SMModuleDebugOptions.UseGizmos)
            {
                DrawGizmos();
            }
        }

        /// <summary>
        /// Simulates all bone controls but seeds their "last run" data from current outgoing data,
        /// effectively skipping interpolation/lerp for this frame.
        /// </summary>
        /// <param name="transform">Parent transform for world calculations.</param>
        public void SimulateWithoutLerp(Matrix4x4 parentMatrix)
        {
            // sequence all other devices to run at the same time
            float DeltaTime = Time.deltaTime;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                Controls[Index].LastRunData.position = Controls[Index].OutGoingData.position;
                Controls[Index].LastRunData.rotation = Controls[Index].OutGoingData.rotation;
                Controls[Index].ComputeMovementLocal(parentMatrix, DeltaTime);
            }
            if (SMModuleDebugOptions.UseGizmos)
            {
                DrawGizmos();
            }
        }

        /// <summary>
        /// Draws gizmos for all controls using the current avatar scale.
        /// </summary>
        public void DrawGizmos()
        {
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                DrawGizmos(Controls[Index]);
            }
            for (int i = 0; i < GizmoBones.Count; i++)
            {
                GizmoBone GizmoBone = GizmoBones[i];
                if (GizmoBone.GizmoTransform != null)
                {
                    float ScaledDistance = BasisAvatarIKStageCalibration.MaxDistanceBeforeTrackerIsIrrelivant(GizmoBone.Control) * BasisHeightDriver.ScaledToMatchValue;
                    BasisGizmoManager.UpdateSphereGizmo(GizmoBone.GizmoReference, GizmoBone.GizmoTransform.position, Vector3.one * ScaledDistance);
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

        /// <summary>
        /// Subscribes to gizmo usage changes so this driver can create/update gizmos.
        /// </summary>
        public void InitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;
        }

        /// <summary>
        /// Unsubscribes from gizmo usage changes.
        /// </summary>
        public void DeInitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
        }

        /// <summary>
        /// Creates or updates gizmos/lines for each control when <paramref name="State"/> is true;
        /// clears flags when false. Actual updates per-frame occur in <see cref="DrawGizmos(BasisLocalBoneControl, float)"/>.
        /// </summary>
        /// <param name="State">Whether gizmo rendering should be active.</param>
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone Driver Gizmos", BasisDebug.LogTag.Gizmo);
            float Size = BasisHeightDriver.ScaledToMatchValue;
            for (int Index = 0; Index < ControlsLength; Index++)
            {
                BasisLocalBoneControl Control = Controls[Index];
                if (State)
                {
                    Vector3 BonePosition = Control.OutgoingWorldData.position;
                    if (Control.HasTarget)
                    {
                        if (BasisGizmoManager.CreateLineGizmo(trackedRoles[Index].ToString(), out Control.LineDrawIndex, BonePosition, Control.Target.OutgoingWorldData.position, 0.05f * Size, Control.Color))
                        {
                            Control.HasLineDraw = true;
                        }
                    }
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
        [System.Serializable]
        public class GizmoBone
        {
            public int GizmoReference;
            public Transform GizmoTransform;
            public BasisBoneTrackedRole Control;

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
        public void AddGizmo(string Name, Transform Transform, float Scale, Color Color, BasisBoneTrackedRole Bone)
        {
            GizmoBone GizmoBone = new GizmoBone();
            if (BasisGizmoManager.CreateSphereGizmo(Name, out int LinkedID, Transform.position, Scale, Color))
            {
                GizmoBone.GizmoReference = LinkedID;
                GizmoBone.GizmoTransform = Transform;
                GizmoBone.Control = Bone;
                GizmoBones.Add(GizmoBone);
            }
        }
    }
}
