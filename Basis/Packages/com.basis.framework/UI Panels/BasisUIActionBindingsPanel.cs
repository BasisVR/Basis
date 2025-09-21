// File: BasisUIActionBindingsPanel.cs
// What it does: Manages the bindings UI with multi-role selection, clear-per-row, conflict warnings, and unsaved-change highlighting.

using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
// Short alias so we can refer to the Action enum cleanly.
using ActionId = BasisActionDriver.ActionId;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisUIActionBindingsPanel : BasisUIBase
    {
        public static string ActionBindings = "ActionBindings";
        public BasisUIMovementDriver BasisUIMovementDriver;

        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisUISettings));
            BasisUIMovementDriver.DeInitalize();
        }

        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(BasisUISettings));
        }

        [Header("UI References")]
        [Tooltip("Where rows will be instantiated (should have a VerticalLayoutGroup).")]
        [SerializeField] private Transform rowsParent;

        [Tooltip("Row prefab containing label, role toggles, clear button, and warning/dirty indicators.")]
        [SerializeField] private BasisBindingRowUI rowPrefab;
        [SerializeField] private Button resetButton;
        [SerializeField] private TextMeshProUGUI saveButtonLabel;

        private readonly List<BasisBindingRowUI> _rows = new List<BasisBindingRowUI>(16);
        private readonly Dictionary<ActionId, int> _actionIndexLookup = new Dictionary<ActionId, int>(16);

        private BasisBoneTrackedRole[] _roles;
        private string[] _roleNames;

        private bool _dirty;

        private void Awake()
        {
            _roles = (BasisBoneTrackedRole[])Enum.GetValues(typeof(BasisBoneTrackedRole));

            _roleNames = new string[_roles.Length];
            for (int Index = 0; Index < _roles.Length; Index++)
            {
                _roleNames[Index] = PrettyEnumName(_roles[Index].ToString());
            }

            WireButtons(true);
            SetDirty(false);
        }

        private void OnEnable()
        {
            Initalize();
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        }
        public async void Initalize()
        {
            RebuildRows();
            if (BasisActionDriver.HasSavedBindings)
            {
                await BasisActionDriver.LoadApplyToDriverAsync();
            }

            RefreshSelectionsFromDriver();
            RecomputeAllConflicts();
            SetDirty(false);
        }

        private void OnBootModeChanged(string Mode)
        {
            Initalize();
        }

        private void OnDisable()
        {
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            SaveToDisk();
        }

        private void WireButtons(bool on)
        {
            if (on)
            {
                resetButton.onClick.AddListener(ResetToDefault);
            }
            else
            {
                resetButton.onClick.RemoveListener(ResetToDefault);
            }
        }

        private void RebuildRows()
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (_rows[i]) { Destroy(_rows[i].gameObject); }
            }
            _rows.Clear();
            _actionIndexLookup.Clear();

            int rowIdx = 0;
            foreach (ActionId action in (ActionId[])Enum.GetValues(typeof(ActionId)))
            {
                if (action == ActionId.Count) { continue; }

                var row = Instantiate(rowPrefab, rowsParent);
                row.gameObject.name = $"Row_{action}";
                row.SetLabel(PrettyEnumName(action.ToString()));
                row.BuildRoleToggles(_roleNames, (roleIndex, isOn) => OnRoleToggleChanged(action, roleIndex, isOn));
                row.SetWarning(string.Empty);
                // row.SetDirty(false);
                row.gameObject.SetActive(true);

                _rows.Add(row);
                _actionIndexLookup[action] = rowIdx;
                rowIdx++;
            }
        }

        private void RefreshSelectionsFromDriver()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var action = (ActionId)i;

                var boundRoles = BasisActionDriver.GetBindings(action); // multi-bind aware
                var selected = new bool[_roles.Length];

                for (int r = 0; r < _roles.Length; r++)
                {
                    selected[r] = false;
                }

                for (int k = 0; k < boundRoles.Count; k++)
                {
                    int idx = Array.IndexOf(_roles, boundRoles[k]);
                    if (idx >= 0) { selected[idx] = true; }
                }

                _rows[i].SetRoleSelectionWithoutNotify(selected);
                //   _rows[i].SetDirty(false);
                _rows[i].SetWarning(string.Empty);
            }
        }

        private void OnRoleToggleChanged(ActionId action, int roleIndex, bool isOn)
        {
            if (roleIndex < 0 || roleIndex >= _roles.Length) { return; }
            var role = _roles[roleIndex];

            if (isOn)
            {
                BasisActionDriver.Bind(action, role);
            }
            else
            {
                BasisActionDriver.Unbind(action, role);
            }
            SaveToDisk();

            RecomputeAllConflicts();
        }
        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            if (saveButtonLabel != null)
            {
                saveButtonLabel.text = dirty ? "Save (Unsaved Changes)" : "Save";
            }
        }

        private async void ResetToDefault()
        {
            await BasisActionDriver.LoadBindings();
            BasisActionDriver.DeleteSaveFile();
            RefreshSelectionsFromDriver();
            RecomputeAllConflicts();
            SetDirty(false);
        }

        private async void SaveToDisk()
        {
            await BasisActionDriver.SaveFromDriver();
            ClearDirtyAll();
        }

        private void ClearDirtyAll()
        {
            // for (int i = 0; i < _rows.Count; i++)
            // {
            //  _rows[i].SetDirty(false);
            //  }
            SetDirty(false);
        }

        private void RecomputeAllConflicts()
        {
            var perRoleChannels = new Dictionary<BasisBoneTrackedRole, Dictionary<string, List<ActionId>>>(8);

            foreach (ActionId action in (ActionId[])Enum.GetValues(typeof(ActionId)))
            {
                if (action == ActionId.Count) { continue; }

                var channels = GetActionChannels(action);
                var roles = BasisActionDriver.GetBindings(action);

                for (int i = 0; i < roles.Count; i++)
                {
                    var role = roles[i];
                    if (!perRoleChannels.TryGetValue(role, out var map))
                    {
                        map = new Dictionary<string, List<ActionId>>(8);
                        perRoleChannels[role] = map;
                    }

                    for (int c = 0; c < channels.Length; c++)
                    {
                        var channel = channels[c];
                        if (!map.TryGetValue(channel, out var list))
                        {
                            list = new List<ActionId>(4);
                            map[channel] = list;
                        }
                        list.Add(action);
                    }
                }
            }

            foreach (var pair in _actionIndexLookup)
            {
                var action = pair.Key;
                string warning = BuildWarningForAction(action, perRoleChannels);
                _rows[pair.Value].SetWarning(warning);
            }
        }

        private string BuildWarningForAction(ActionId action, Dictionary<BasisBoneTrackedRole, Dictionary<string, List<ActionId>>> perRoleChannels)
        {
            var roles = BasisActionDriver.GetBindings(action);
            if (roles.Count == 0) { return string.Empty; }

            var channels = GetActionChannels(action);
            List<string> conflicts = new List<string>(4);

            for (int i = 0; i < roles.Count; i++)
            {
                var role = roles[i];
                if (!perRoleChannels.TryGetValue(role, out var map)) { continue; }

                for (int c = 0; c < channels.Length; c++)
                {
                    var channel = channels[c];
                    if (map.TryGetValue(channel, out var list) && list.Count > 1)
                    {
                        // Exclude self then collect
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (list[k] != action)
                            {
                                conflicts.Add($"{PrettyEnumName(list[k].ToString())} on {PrettyEnumName(role.ToString())}");
                            }
                        }
                    }
                }
            }

            if (conflicts.Count == 0) { return string.Empty; }

            // Deduplicate simple strings
            var set = new HashSet<string>(conflicts);
            conflicts.Clear();
            conflicts.AddRange(set);

            return $"Potential conflict with: {string.Join(", ", conflicts)}";
        }

        private static string[] GetActionChannels(ActionId action)
        {
            // What it does: Provides a coarse input-channel classification for conflict warnings.
            switch (action)
            {
                case ActionId.SetMovementSpeedMultiplierFromPrimary2DAxis: return _Axis2D;
                case ActionId.SetMovementVectorFromPrimary2DAxis: return _Axis2D;
                case ActionId.RotateFromPrimary2DAxis: return _Axis2D;

                case ActionId.ToggleHamburgerOnSecondaryRelease: return _SecondaryButtonRelease;
                case ActionId.ToggleMicOnPrimaryReleaseIfNoHover: return _PrimaryButtonRelease;
                case ActionId.JumpOnPrimaryButton: return _PrimaryButtonHold;

                case ActionId.TickMovementSpeed: return _Tick;
                default: return _Misc;
            }
        }

        private static readonly string[] _Axis2D = new[] { "Axis2D" };
        private static readonly string[] _PrimaryButtonRelease = new[] { "PrimaryButtonRelease" };
        private static readonly string[] _PrimaryButtonHold = new[] { "PrimaryButtonHold" };
        private static readonly string[] _SecondaryButtonRelease = new[] { "SecondaryButtonRelease" };
        private static readonly string[] _Tick = new[] { "Tick" };
        private static readonly string[] _Misc = new[] { "Misc" };

        private static string PrettyEnumName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) { return raw; }
            var chars = new List<char>(raw.Length + 8);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(raw[i - 1]) || (i + 1 < raw.Length && char.IsLower(raw[i + 1]))))
                {
                    chars.Add(' ');
                }
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }
    }
}
