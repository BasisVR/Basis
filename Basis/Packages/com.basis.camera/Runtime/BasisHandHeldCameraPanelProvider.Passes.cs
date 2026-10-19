using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    public partial class BasisHandHeldCameraPanelProvider
    {
        private PanelSectionToggle _passesSection;
        private PanelElementDescriptor _passesGroup;
        private PanelDropdown _passOutputDropdown;
        private PanelSlider _passDepthRangeSlider;
        private PanelToggle _separatePassesToggle;
        private readonly PanelToggle[] _separatePassToggles = new PanelToggle[BasisCameraPasses.Count];
        private readonly bool?[] _lastSeparatePass = new bool?[BasisCameraPasses.Count];
        private string _lastPassOutputKey, _lastSeparateDescription;
        private float _lastPassDepthRange = float.NaN;
        private bool? _lastSeparatePasses;

        private void BuildRenderPassesGroup(RectTransform parent)
        {
            _passesSection = PanelSectionToggle.CreateNewEntry(parent);
            _passesGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _passesSection, parent, BasisLocalization.Get("camera.passes"), false);
            RectTransform content = _passesGroup.ContentParent;
            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _passOutputDropdown = PanelDropdown.CreateNewEntry(content);
            _passOutputDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.passes.output"));
            _passOutputDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.passes.output.description"));
            _passOutputDropdown.AssignLocalizedEntries(
                new List<string>(BasisCameraPasses.OutputKeys), new List<string>(BasisCameraPasses.OutputKeys));
            _passOutputDropdown.SetResetDefault(BasisCameraPasses.OutputKeys[(int)BasisCameraPasses.SanitizeOutput(defaults.passOutput)]);
            _passOutputDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _passOutputDropdown == null) return;
                int index = _passOutputDropdown.Index;
                if (index < 0) return;
                _activeCamera.SetPassOutput(BasisCameraPasses.SanitizeOutput(index));
                _lastPassOutputKey = BasisCameraPasses.OutputKeys[(int)_activeCamera.PassOutput];
                RefreshRenderPassVisibility();
            };

            _passDepthRangeSlider = PanelSlider.CreateNew(content);
            _passDepthRangeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.passes.depthRange"), BasisCameraPasses.MinDepthRange, BasisCameraPasses.MaxDepthRange, false, 1, ValueDisplayMode.Meters));
            _passDepthRangeSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.passes.depthRange.description"));
            _passDepthRangeSlider.SetResetDefault(defaults.passDepthRange);
            _passDepthRangeSlider.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SetPassDepthRange(v);
                _lastPassDepthRange = _activeCamera.PassDepthRange;
            };

            _separatePassesToggle = PanelToggle.CreateNewEntry(content);
            _separatePassesToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.passes.separate"));
            _separatePassesToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.passes.separate.tooltip"));
            _separatePassesToggle.SetResetDefault(defaults.separatePasses);
            _separatePassesToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SetSeparatePasses(v);
                _lastSeparatePasses = v;
                _lastSeparateDescription = null;
                RefreshRenderPassVisibility();
            };

            bool[] separateDefaults = { defaults.separateDepth, defaults.separateNormals, defaults.separateMask, defaults.separateMotion };
            for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
            {
                BasisCameraPass pass = (BasisCameraPass)Index;
                int slot = Index;
                PanelToggle toggle = PanelToggle.CreateNewEntry(content);
                toggle.Descriptor.SetTitle(BasisLocalization.Get(BasisCameraPasses.PassKeys[Index]));
                toggle.Descriptor.SetTooltip(BasisLocalization.Get(BasisCameraPasses.PassKeys[Index] + ".description"));
                toggle.SetResetDefault(separateDefaults[Index]);
                toggle.OnValueChanged = v =>
                {
                    if (_activeCamera == null) return;
                    _activeCamera.SetSeparatePass(pass, v);
                    _lastSeparatePass[slot] = v;
                    _lastSeparateDescription = null;
                    RefreshRenderPassVisibility();
                };
                _separatePassToggles[Index] = toggle;
            }
        }

        private void SeedRenderPassControls()
        {
            _lastPassOutputKey = null;
            _lastPassDepthRange = float.NaN;
            _lastSeparatePasses = null;
            _lastSeparateDescription = null;
            for (int Index = 0; Index < _lastSeparatePass.Length; Index++) _lastSeparatePass[Index] = null;
            TickRenderPassSection();
        }

        private void TickRenderPassSection()
        {
            if (_activeCamera == null || _passOutputDropdown == null) return;

            string key = BasisCameraPasses.OutputKeys[(int)_activeCamera.PassOutput];
            bool expanded = _passOutputDropdown.DropdownComponent != null && _passOutputDropdown.DropdownComponent.IsExpanded;
            if (_lastPassOutputKey != key && !expanded)
            {
                _lastPassOutputKey = key;
                _passOutputDropdown.SetValueWithoutNotify(key);
            }

            SyncSlider(_passDepthRangeSlider, _activeCamera.PassDepthRange, ref _lastPassDepthRange);
            SyncToggle(_separatePassesToggle, _activeCamera.SeparatePasses, ref _lastSeparatePasses);
            for (int Index = 0; Index < _separatePassToggles.Length; Index++)
            {
                SyncToggle(_separatePassToggles[Index], _activeCamera.IsSeparatePass((BasisCameraPass)Index), ref _lastSeparatePass[Index]);
            }
            RefreshRenderPassVisibility();

            bool inUse = _activeCamera.PassOutput != BasisCameraPassOutput.Picture || _activeCamera.SeparatePasses;
            string description = inUse && !_activeCamera.RenderPassesAvailable ? BasisLocalization.Get("camera.passes.unavailable") : _activeCamera.DescribeSeparateOutputs();
            if (_lastSeparateDescription == description) return;
            _lastSeparateDescription = description;
            _separatePassesToggle?.Descriptor.SetDescription(description);
        }

        private void RefreshRenderPassVisibility()
        {
            if (_activeCamera == null) return;

            bool separate = _activeCamera.SeparatePasses;
            BasisCameraPassSet used = BasisCameraPasses.For(_activeCamera.PassOutput) | (separate ? _activeCamera.SeparatePassSet : BasisCameraPassSet.None);
            bool changed = SetRowActive(_passDepthRangeSlider, BasisCameraPasses.Contains(used, BasisCameraPass.Depth));
            for (int Index = 0; Index < _separatePassToggles.Length; Index++) changed |= SetRowActive(_separatePassToggles[Index], separate);
            if (!changed) return;

            RefreshSearch();
            ForceLayoutRebuild(_passesGroup);
        }

        private void ClearRenderPassReferences()
        {
            _passesSection = null;
            _passesGroup = null;
            _passOutputDropdown = null;
            _passDepthRangeSlider = null;
            _separatePassesToggle = null;
            for (int Index = 0; Index < _separatePassToggles.Length; Index++)
            {
                _separatePassToggles[Index] = null;
                _lastSeparatePass[Index] = null;
            }
            _lastPassOutputKey = null;
            _lastSeparateDescription = null;
            _lastPassDepthRange = float.NaN;
            _lastSeparatePasses = null;
        }
    }
}
