// File: BasisBindingRowUI.cs
// What it does: Renders one action row with a single-selection dropdown for roles,
// a clear button, and conflict/dirty indicators. Maintains the original callback
// signature by invoking (roleIndex, true) for the newly selected role and
// (prevIndex, false) for the previously selected role. "None" means no role selected.

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisBindingRowUI : MonoBehaviour
    {
        [Header("UI Refs")]
        [SerializeField] private TextMeshProUGUI actionLabel;
        [SerializeField] private TMP_Dropdown roleDropdown;
        [SerializeField] private TextMeshProUGUI warningText;

        [Header("Dropdown Settings")]
        [SerializeField] private bool includeNoneOption = true;
        [SerializeField] private string noneOptionText = "None";

        // Callback retained for compatibility with old toggle-based API: (roleIndex, isOn)
        private Action<int, bool> _onRoleToggleChanged;

        private bool _suppressNotify;
        private int _currentSelectedIndex = -1; // -1 = None, otherwise 0..roleCount-1
        private int _roleCount = 0;

        public void SetLabel(string text)
        {
            if (actionLabel != null)
            {
                actionLabel.text = text;
            }
        }

        /// <summary>
        /// Builds the dropdown options from role names. Keeps the old method name/signature
        /// so existing callers don't need to change.
        /// </summary>
        public void BuildRoleToggles(string[] roleNames, Action<int, bool> onToggleChanged)
        {
            _onRoleToggleChanged = onToggleChanged;

            if (roleDropdown == null)
            {
                Debug.LogError("[BasisBindingRowUI] roleDropdown reference is missing.");
                return;
            }

            // Prepare dropdown
            roleDropdown.onValueChanged.RemoveAllListeners();
            roleDropdown.ClearOptions();

            var options = new List<TMP_Dropdown.OptionData>();
            if (includeNoneOption)
            {
                options.Add(new TMP_Dropdown.OptionData(string.IsNullOrEmpty(noneOptionText) ? "None" : noneOptionText));
            }

            if (roleNames != null)
            {
                foreach (var role in roleNames)
                {
                    options.Add(new TMP_Dropdown.OptionData(role));
                }
                _roleCount = roleNames.Length;
            }
            else
            {
                _roleCount = 0;
            }

            roleDropdown.AddOptions(options);

            // Default to None / first option
            _suppressNotify = true;
            try
            {
                roleDropdown.value = 0;
                _currentSelectedIndex = -1;
            }
            finally
            {
                _suppressNotify = false;
            }

            // Hook changes
            roleDropdown.onValueChanged.AddListener(OnDropdownValueChanged);
        }

        /// <summary>
        /// Sets the selection from a bool[] (first 'true' wins).
        /// If no 'true' values, selection becomes None.
        /// </summary>
        public void SetRoleSelectionWithoutNotify(bool[] selected)
        {
            if (roleDropdown == null) { return; }

            int idx = -1;
            if (selected != null)
            {
                int count = Mathf.Min(selected.Length, _roleCount);
                for (int i = 0; i < count; i++)
                {
                    if (selected[i])
                    {
                        idx = i;
                        break;
                    }
                }
            }

            _suppressNotify = true;
            try
            {
                _currentSelectedIndex = idx;
                roleDropdown.value = includeNoneOption
                    ? (idx >= 0 ? idx + 1 : 0)
                    : Mathf.Clamp(idx, 0, Mathf.Max(0, roleDropdown.options.Count - 1));
            }
            finally
            {
                _suppressNotify = false;
            }
        }

        public void SetWarning(string message)
        {
            if (warningText == null) { return; }
            warningText.text = string.IsNullOrEmpty(message) ? string.Empty : message;
        }

        private void OnDropdownValueChanged(int dropdownValue)
        {
            if (_suppressNotify) { return; }

            int newSelectedIndex = includeNoneOption ? dropdownValue - 1 : dropdownValue;

            // No change
            if (newSelectedIndex == _currentSelectedIndex) { return; }

            // Notify previous selection turned off
            if (_currentSelectedIndex >= 0)
            {
                _onRoleToggleChanged?.Invoke(_currentSelectedIndex, false);
            }

            // Notify new selection turned on
            if (newSelectedIndex >= 0)
            {
                _onRoleToggleChanged?.Invoke(newSelectedIndex, true);
            }

            _currentSelectedIndex = newSelectedIndex;
        }
    }
}
