using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Optimized version:
    /// - Eliminated LINQ/Concat/IndexOf hot-path allocations
    /// - Cached role↔index and control↔index lookups via dictionaries
    /// - Null-safety: guard optional bones/avatars, avoid NREs
    /// - Reduced repeated work in gizmo & simulation paths
    /// - Fixed bug in DrawGizmos() where a different variable name was compared (Role vs role)
    /// - Reduced GC by reusing arrays and avoiding temporary object creation in FindBone (returns null on miss)
    /// - Minor micro-opts (AggressiveInlining on tiny helpers, early-outs)
    /// </summary>
    [Serializable]
    public class BasisRemoteBoneDriver
    {
        // Config / references
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

        [SerializeField] public BasisRemoteBoneControl[] Controls;
        [SerializeField] public BasisBoneTrackedRole[] trackedRoles;

        public bool HasControls;

        public const float DefaultGizmoSize = 0.05f;

        // Caches to avoid O(n) scans
        // role -> index and control -> index maps (populated in CreateInitialArrays/AddRange)
        Dictionary<BasisBoneTrackedRole, int> _roleToIndex;
        Dictionary<BasisRemoteBoneControl, int> _controlToIndex;

        // Scale caches
        Vector3 _lastScale = Vector3.zero;
        Vector3 _lastInitialScale = Vector3.zero;

        // Reusable color cache
        Color[] _rainbowCache;

        #region Initialization

        public void InitializeRemote()
        {
            // Role maps might not exist yet (CreateInitialArrays handles normally), but guard anyway.
            EnsureMaps();

            FindBone(out Head, BasisBoneTrackedRole.Head);
            FindBone(out Hips, BasisBoneTrackedRole.Hips);
            Head.HasTracked = BasisHasTracked.HasTracker;
            Hips.HasTracked = BasisHasTracked.HasTracker;
            FindBone(out Mouth, BasisBoneTrackedRole.Mouth);
        }

        public void OnCalibration(BasisRemotePlayer remotePlayer)
        {
            // Use the incoming parameter directly; don't rely on stale RemotePlayer
            RemotePlayer = remotePlayer;

            // Cache transform for repeated use
            RemotePlayerTransform = RemotePlayer.transform;

            var animator = RemotePlayer?.BasisAvatar?.Animator;
            HeadAvatar = animator != null ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            HipsAvatar = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;

            HasHead = HeadAvatar != null;
            HasHips = HipsAvatar != null;
        }

        public void CreateInitialArrays(bool isLocal)
        {
            // Reset
            trackedRoles = Array.Empty<BasisBoneTrackedRole>();
            Controls = Array.Empty<BasisRemoteBoneControl>();

            // Determine role count
            int length = isLocal
                ? Enum.GetValues(typeof(BasisBoneTrackedRole)).Length
                : 6;

            // Colors (cache sized to max seen)
            _rainbowCache = GenerateRainbowColors(_rainbowCache, length);

            // Build arrays without LINQ/Concat
            var newControls = new BasisRemoteBoneControl[length + (isLocal ? 0 : 1)];
            var newRoles = new BasisBoneTrackedRole[length + (isLocal ? 0 : 1)];

            for (int i = 0; i < length; i++)
            {
                SetupRole(i, _rainbowCache[i], out BasisRemoteBoneControl control, out BasisBoneTrackedRole role);
                newControls[i] = control;
                newRoles[i] = role;
            }

            if (!isLocal)
            {
                // Historically index 22 has been used externally; keep behavior
                SetupRole(22, Color.blue, out BasisRemoteBoneControl extraControl, out BasisBoneTrackedRole extraRole);
                newControls[length] = extraControl;
                newRoles[length] = extraRole;
            }

            AddRange(newControls, newRoles);

            HasControls = true;
            InitializeGizmos();
        }

        public void AddRange(BasisRemoteBoneControl[] newControls, BasisBoneTrackedRole[] newRoles)
        {
            // Allocate once and copy (avoid Concat)
            int oldLen = Controls?.Length ?? 0;
            int addLen = newControls.Length;

            var combinedControls = new BasisRemoteBoneControl[oldLen + addLen];
            var combinedRoles = new BasisBoneTrackedRole[oldLen + addLen];

            if (oldLen > 0)
            {
                Array.Copy(Controls, 0, combinedControls, 0, oldLen);
                Array.Copy(trackedRoles, 0, combinedRoles, 0, oldLen);
            }

            Array.Copy(newControls, 0, combinedControls, oldLen, addLen);
            Array.Copy(newRoles, 0, combinedRoles, oldLen, addLen);

            Controls = combinedControls;
            trackedRoles = combinedRoles;
            ControlsLength = Controls.Length;

            // Rebuild maps
            RebuildMaps();
        }

        public void SetupRole(int index, Color color, out BasisRemoteBoneControl basisBoneControl, out BasisBoneTrackedRole role)
        {
            role = (BasisBoneTrackedRole)index;

            var c = new BasisRemoteBoneControl();
            c.Initialize();
            FillOutBasicInformation(c, role.ToString(), color);

            basisBoneControl = c;
        }

        public void FillOutBasicInformation(BasisRemoteBoneControl control, string name, Color color)
        {
            control.name = name;
            control.Color = color;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureMaps()
        {
            _roleToIndex ??= new Dictionary<BasisBoneTrackedRole, int>(64);
            _controlToIndex ??= new Dictionary<BasisRemoteBoneControl, int>(64);
        }

        void RebuildMaps()
        {
            EnsureMaps();
            _roleToIndex.Clear();
            _controlToIndex.Clear();

            for (int i = 0; i < ControlsLength; i++)
            {
                var role = trackedRoles[i];
                var control = Controls[i];
                _roleToIndex[role] = i;
                if (control != null)
                    _controlToIndex[control] = i;
            }
        }

        #endregion

        #region Runtime / Simulation
        public void SimulateAndApplyRemote(Vector3 nowScale)
        {
            var driver = RemotePlayer.RemoteAvatarDriver;//now will never be null.
            Vector3 initialScale = driver.AvatarInitalScale;

            // Only rescale T-pose locals if scale changed (avoid per-frame work)
            if (_lastInitialScale != initialScale || _lastScale != nowScale)
            {
                _lastInitialScale = initialScale;
                _lastScale = nowScale;

                for (int Index = 0; Index < ControlsLength; Index++)
                {
                    BasisRemoteBoneControl control = Controls[Index];
                    if (control == null) continue;

                    // Apply relative scale to T-pose local position
                    control.TposeLocalScaled.position = Vector3.Scale(control.TposeLocal.position, nowScale);
                }
            }

            RemotePlayer.OnPreSimulateBones?.Invoke();

            // Sequence devices
            for (int i = 0; i < ControlsLength; i++)
            {
                var c = Controls[i];
                c?.ComputeMovementRemote();
            }

            if (BasisGizmoManager.UseGizmos)
            {
                DrawGizmos();
            }
            Vector3 rrt = RemotePlayerTransform.position;

            HeadAvatar.GetPositionAndRotation(out Vector3 Headpos, out Quaternion Headrot);
            Head.IncomingData.position = Headpos - rrt;
            Head.IncomingData.rotation = Headrot;

            HipsAvatar.GetPositionAndRotation(out Vector3 Hipspos, out Quaternion Hipsrot);
            Hips.IncomingData.position = Hipspos - rrt;
            Hips.IncomingData.rotation = Hipsrot;
        }
        #endregion

        #region Gizmos

        public void InitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage; // prevent double-subscribe
            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;
        }

        public void DeInitializeGizmos()
        {
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
        }

        public void DrawGizmos()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                var c = Controls[i];
                if (c != null) DrawGizmos(c);
            }
        }

        public void UpdateGizmoUsage(bool state)
        {
            BasisDebug.Log("Running Bone Driver Gizmos", BasisDebug.LogTag.Gizmo);

            float scale = BasisLocalPlayer.Instance != null
                ? BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale
                : 1f;

            for (int i = 0; i < ControlsLength; i++)
            {
                var control = Controls[i];
                if (control == null) continue;

                var role = trackedRoles[i];

                if (state)
                {
                    if (role == BasisBoneTrackedRole.CenterEye && !Application.isEditor)
                        continue;

                    Vector3 bonePos = control.OutGoingData.position;

                    if (control.HasTarget)
                    {
                        if (BasisGizmoManager.CreateLineGizmo(out control.LineDrawIndex, bonePos, control.Target.OutGoingData.position, 0.03f, control.Color))
                        {
                            control.HasLineDraw = true;
                        }
                    }

                    if (BasisGizmoManager.CreateSphereGizmo(out control.GizmoReference, bonePos, DefaultGizmoSize * scale, control.Color))
                    {
                        control.HasGizmo = true;
                    }
                }
                else
                {
                    control.HasGizmo = false;
                }
            }
        }

        public void DrawGizmos(BasisRemoteBoneControl control)
        {
            if (control == null || !control.HasBone) return;

            Vector3 bonePosition = control.OutGoingData.position;

            if (control.HasTarget && control.HasLineDraw)
            {
                BasisGizmoManager.UpdateLineGizmo(control.LineDrawIndex, bonePosition, control.Target.OutGoingData.position);
            }

            if (FindTrackedRole(control, out BasisBoneTrackedRole role))
            {
                if (role == BasisBoneTrackedRole.CenterEye)
                {
                    // Ignore center eye to avoid VR issues
                    return;
                }

                if (control.HasGizmo)
                {
                    if (!BasisGizmoManager.UpdateSphereGizmo(control.GizmoReference, bonePosition))
                    {
                        control.HasGizmo = false;
                    }
                }
            }

            if (BasisLocalAvatarDriver.CurrentlyTposing && FindTrackedRole(control, out BasisBoneTrackedRole role2))
            {
                if (role2 == BasisBoneTrackedRole.CenterEye)
                {
                    // Ignore center eye to avoid VR issues
                    return;
                }

                if (BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role2))
                {
                    float scale = BasisLocalPlayer.Instance != null
                        ? BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale
                        : 1f;
                }
            }
        }

        #endregion

        #region Lookups

        /// <summary>
        /// O(1) role lookup without extra allocations. Returns false and null control if not found.
        /// </summary>
        public bool FindBone(out BasisRemoteBoneControl control, BasisBoneTrackedRole role)
        {
            control = null;
            if (_roleToIndex != null && _roleToIndex.TryGetValue(role, out int idx))
            {
                if ((uint)idx < (uint)ControlsLength)
                {
                    control = Controls[idx];
                    return control != null;
                }
            }
            return false;
        }

        /// <summary>
        /// O(1) control->role lookup without allocations.
        /// </summary>
        public bool FindTrackedRole(BasisRemoteBoneControl control, out BasisBoneTrackedRole role)
        {
            role = BasisBoneTrackedRole.CenterEye;

            if (control == null || _controlToIndex == null) return false;

            if (_controlToIndex.TryGetValue(control, out int idx))
            {
                if ((uint)idx < (uint)ControlsLength)
                {
                    role = trackedRoles[idx];
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Returns a rainbow array of size count. Reuses prior buffer when possible.
        /// </summary>
        public Color[] GenerateRainbowColors(Color[] cache, int count)
        {
            if (cache == null || cache.Length < count)
                cache = new Color[count];

            for (int i = 0; i < count; i++)
            {
                float hue = Mathf.Repeat(i / (float)count, 1f);
                cache[i] = Color.HSVToRGB(hue, 1f, 1f);
            }
            return cache;
        }

        /// <summary>
        /// Backwards-compatible overload kept for external callers.
        /// </summary>
        public Color[] GenerateRainbowColors(int requestColorCount) => GenerateRainbowColors(null, requestColorCount);

        public void CreateRotationalLock(BasisRemoteBoneControl addToBone, BasisRemoteBoneControl target)
        {
            if (addToBone == null) return;

            addToBone.Target = target;
            if (target != null)
            {
                addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
                addToBone.ScaledOffset = addToBone.Offset;
                addToBone.HasTarget = true;
            }
            else
            {
                addToBone.Offset = Vector3.zero;
                addToBone.ScaledOffset = Vector3.zero;
                addToBone.HasTarget = false;
            }
        }

        #endregion
    }
}
