using Basis.Scripts.Drivers; // BasisLocalBoneDriver
using Basis.Scripts.TransformBinders.BoneControl; // BasisLocalBoneControl
using UnityEngine;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;

namespace Basis.Scripts.Debugging
{
    /// <summary>
    /// Runtime visualisation of hint trackers and their calibrated "push up/out" offsets:
    /// a sphere at the raw hint pose, an offset line to the biased pose, a sphere at the
    /// biased pose, and an orientation triad. Built from BasisGizmoManager so it renders
    /// in-game; driven from SMModuleDebugOptions under the GizmoHintOffsets toggle.
    /// </summary>
    public static class BasisHintOffsetGizmos
    {
        public static bool Show;

        private const int RoleCount = 5;
        private const float RawSphereSize = 0.03f;
        private const float BiasedSphereSize = 0.03f;
        private const float AxisLength = 0.06f;
        private const float LineWidth = 0.003f;
        private const float LabelScale = 0.02f;

        private static readonly Color RawColor = new Color(1f, 1f, 0f, 0.9f);
        private static readonly Color BiasedColor = new Color(0f, 1f, 1f, 0.9f);
        private static readonly Color OffsetLineColor = new Color(1f, 0.25f, 0.25f, 0.95f);
        private static readonly Color XAxisColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        private static readonly Color YAxisColor = new Color(0.2f, 1f, 0.2f, 0.9f);
        private static readonly Color ZAxisColor = new Color(0.2f, 0.4f, 1f, 0.9f);

        private static readonly BasisBoneTrackedRole[] Roles =
        {
            BasisBoneTrackedRole.Chest,
            BasisBoneTrackedRole.LeftLowerArm,
            BasisBoneTrackedRole.RightLowerArm,
            BasisBoneTrackedRole.LeftLowerLeg,
            BasisBoneTrackedRole.RightLowerLeg,
        };

        private static readonly int[] _rawSphere = NewIds();
        private static readonly int[] _biasedSphere = NewIds();
        private static readonly int[] _offsetLine = NewIds();
        private static readonly int[] _axisX = NewIds();
        private static readonly int[] _axisY = NewIds();
        private static readonly int[] _axisZ = NewIds();
        private static readonly int[] _label = NewIds();
        private static readonly bool[] _slotVisible = new bool[RoleCount];

        private static bool _created;
        private static bool _registered;

        public static void Tick(bool shouldShow, bool showLabels, Vector3 cameraPos)
        {
            EnsureMasterToggleHook();

            if (!shouldShow)
            {
                HideAll();
                return;
            }
            EnsureCreated();

            float labelScale = LabelScale * Mathf.Max(0.01f, BasisHeightDriver.ScaledToMatchValue);

            for (int i = 0; i < RoleCount; i++)
            {
                BasisLocalBoneControl control = ControlForSlot(i);
                if (control == null || !BasisHintBiasStore.TryGet(Roles[i], out Vector3 localOffset))
                {
                    SetSlotVisible(i, false);
                    DestroyLabel(i);
                    continue;
                }

                Vector3 rawPos = control.OutgoingWorldData.position;
                Quaternion rawRot = control.OutgoingWorldData.rotation;
                Vector3 biasedPos = rawPos + rawRot * localOffset;

                BasisGizmoManager.UpdateSphereGizmo(_rawSphere[i], rawPos, Vector3.one * RawSphereSize);
                BasisGizmoManager.UpdateSphereGizmo(_biasedSphere[i], biasedPos, Vector3.one * BiasedSphereSize);
                BasisGizmoManager.UpdateLineGizmo(_offsetLine[i], rawPos, biasedPos);
                BasisGizmoManager.UpdateLineGizmo(_axisX[i], rawPos, rawPos + rawRot * Vector3.right * AxisLength);
                BasisGizmoManager.UpdateLineGizmo(_axisY[i], rawPos, rawPos + rawRot * Vector3.up * AxisLength);
                BasisGizmoManager.UpdateLineGizmo(_axisZ[i], rawPos, rawPos + rawRot * Vector3.forward * AxisLength);
                SetSlotVisible(i, true);

                if (showLabels)
                {
                    string text = $"{Roles[i]}\n|offset|={localOffset.magnitude:F3}m";
                    Vector3 labelPos = rawPos + Vector3.up * (RawSphereSize * 2.5f);
                    if (_label[i] <= 0)
                    {
                        BasisGizmoManager.CreateTextGizmo($"HintLabel_{Roles[i]}", out _label[i], labelPos, text, Color.white);
                    }
                    Quaternion rot = BasisGizmoManager.BillboardRotation(labelPos, cameraPos);
                    BasisGizmoManager.UpdateTextGizmo(_label[i], labelPos, rot, labelScale, text, Color.white);
                    BasisGizmoManager.SetGizmoActive(_label[i], true);
                }
                else
                {
                    DestroyLabel(i);
                }
            }
        }

        public static void Shutdown()
        {
            if (!_created)
            {
                return;
            }
            for (int i = 0; i < RoleCount; i++)
            {
                BasisGizmoManager.DestroyGizmo(_rawSphere[i]);
                BasisGizmoManager.DestroyGizmo(_biasedSphere[i]);
                BasisGizmoManager.DestroyGizmo(_offsetLine[i]);
                BasisGizmoManager.DestroyGizmo(_axisX[i]);
                BasisGizmoManager.DestroyGizmo(_axisY[i]);
                BasisGizmoManager.DestroyGizmo(_axisZ[i]);
                DestroyLabel(i);
            }
            ResetState();
        }

        private static BasisLocalBoneControl ControlForSlot(int i)
        {
            switch (i)
            {
                case 0: return BasisLocalBoneDriver.ChestControl;
                case 1: return BasisLocalBoneDriver.LeftLowerArmControl;
                case 2: return BasisLocalBoneDriver.RightLowerArmControl;
                case 3: return BasisLocalBoneDriver.LeftLowerLegControl;
                case 4: return BasisLocalBoneDriver.RightLowerLegControl;
                default: return null;
            }
        }

        private static void EnsureCreated()
        {
            if (_created)
            {
                return;
            }
            for (int i = 0; i < RoleCount; i++)
            {
                string role = Roles[i].ToString();
                BasisGizmoManager.CreateSphereGizmo($"HintRaw_{role}", out _rawSphere[i], Vector3.zero, RawSphereSize, RawColor);
                BasisGizmoManager.CreateSphereGizmo($"HintBiased_{role}", out _biasedSphere[i], Vector3.zero, BiasedSphereSize, BiasedColor);
                BasisGizmoManager.CreateLineGizmo($"HintOffset_{role}", out _offsetLine[i], Vector3.zero, Vector3.zero, LineWidth, OffsetLineColor);
                BasisGizmoManager.CreateLineGizmo($"HintAxisX_{role}", out _axisX[i], Vector3.zero, Vector3.zero, LineWidth, XAxisColor);
                BasisGizmoManager.CreateLineGizmo($"HintAxisY_{role}", out _axisY[i], Vector3.zero, Vector3.zero, LineWidth, YAxisColor);
                BasisGizmoManager.CreateLineGizmo($"HintAxisZ_{role}", out _axisZ[i], Vector3.zero, Vector3.zero, LineWidth, ZAxisColor);
                BasisGizmoManager.SetGizmoActive(_rawSphere[i], false);
                BasisGizmoManager.SetGizmoActive(_biasedSphere[i], false);
                BasisGizmoManager.SetGizmoActive(_offsetLine[i], false);
                BasisGizmoManager.SetGizmoActive(_axisX[i], false);
                BasisGizmoManager.SetGizmoActive(_axisY[i], false);
                BasisGizmoManager.SetGizmoActive(_axisZ[i], false);
                _slotVisible[i] = false;
            }
            _created = true;
        }

        private static void SetSlotVisible(int i, bool active)
        {
            if (!_created || _slotVisible[i] == active)
            {
                return;
            }
            BasisGizmoManager.SetGizmoActive(_rawSphere[i], active);
            BasisGizmoManager.SetGizmoActive(_biasedSphere[i], active);
            BasisGizmoManager.SetGizmoActive(_offsetLine[i], active);
            BasisGizmoManager.SetGizmoActive(_axisX[i], active);
            BasisGizmoManager.SetGizmoActive(_axisY[i], active);
            BasisGizmoManager.SetGizmoActive(_axisZ[i], active);
            _slotVisible[i] = active;
        }

        private static void HideAll()
        {
            if (!_created)
            {
                return;
            }
            for (int i = 0; i < RoleCount; i++)
            {
                SetSlotVisible(i, false);
                DestroyLabel(i);
            }
        }

        private static void DestroyLabel(int i)
        {
            if (_label[i] > 0)
            {
                BasisGizmoManager.DestroyGizmo(_label[i]);
                _label[i] = -1;
            }
        }

        private static void EnsureMasterToggleHook()
        {
            if (_registered)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            _registered = true;
        }

        private static void OnMasterToggleChanged(bool state)
        {
            if (!state)
            {
                ResetState();
            }
        }

        private static void ResetState()
        {
            for (int i = 0; i < RoleCount; i++)
            {
                _rawSphere[i] = -1;
                _biasedSphere[i] = -1;
                _offsetLine[i] = -1;
                _axisX[i] = -1;
                _axisY[i] = -1;
                _axisZ[i] = -1;
                _label[i] = -1;
                _slotVisible[i] = false;
            }
            _created = false;
        }

        private static int[] NewIds()
        {
            int[] ids = new int[RoleCount];
            for (int i = 0; i < RoleCount; i++)
            {
                ids[i] = -1;
            }
            return ids;
        }
    }
}
