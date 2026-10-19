using System.Collections.Generic;
using Basis.BTween;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelDropdown : PanelDataComponent<string>
    {

        public static class DropdownStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown - Entry Variant.prefab";
            public static string OverlayEntry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown - Entry Variant - Overlay.prefab";
            public static string EntryNoLabel => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown - Entry No Title Variant.prefab";
        }

        public TMP_Dropdown DropdownComponent;

        protected override Selectable InteractableTarget => DropdownComponent;

        protected override bool SupportsResetGesture => true;

        private int _previousIndex = -1;
        private TweenScale _selectionPunchTween;
        private TweenCanvasGroupAlpha _listFadeTween;
        private Transform _dropdownList;
        private List<string> _optionTooltips;
        private int _hoveredOption = -1;
        private bool _optionHoverReady;

        public int Index
        {
            get
            {
                if (Entries == null || Entries.Count == 0)
                {
                    return -1;
                }

                for (int i = 0; i < Entries.Count; i++)
                {
                    if (string.Equals(Entries[i], Value, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        private PanelDropdown() { }

        public static PanelDropdown CreateNew(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Default, parent);
        public static PanelDropdown CreateNewEntry(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Entry, parent);

        public static PanelDropdown CreateNew(string style, Component parent)
            => CreateNew<PanelDropdown>(style, parent);

        public List<string> Entries { get; protected set; }

        public void AssignEntries(List<string> entries)
        {
            Entries = entries;
            DropdownComponent.ClearOptions();
            DropdownComponent.AddOptions(Entries);
            SetOptionTooltips(null);
            SetValueWithoutNotify(Value);
        }

        public void AssignEntries(List<string> entries, List<string> displayLabels)
        {
            Entries = entries;
            DropdownComponent.ClearOptions();
            DropdownComponent.AddOptions(displayLabels != null && displayLabels.Count == entries.Count ? displayLabels : entries);
            SetOptionTooltips(null);
            SetValueWithoutNotify(Value);
        }

        public void AssignEntries(List<string> entries, List<string> displayLabels, List<string> optionTooltips)
        {
            AssignEntries(entries, displayLabels);
            SetOptionTooltips(optionTooltips);
        }

        /// <summary>
        /// Localized entries pick up a per-option tooltip for free: each option key is also looked
        /// up with a <c>.tooltip</c> suffix, matching how every other control in the panels names
        /// its tooltip string. Options with no such key simply show the dropdown's own tooltip.
        /// </summary>
        public void AssignLocalizedEntries(List<string> entries, List<string> localizationKeys)
            => AssignLocalizedEntries(entries, localizationKeys, null);

        /// <summary>
        /// Variant for options whose label key is shared with other dropdowns — the generic
        /// <c>ui.option.on</c> / <c>ui.option.off</c> pair, mostly — where the derived tooltip key
        /// would collide. Naming the tooltip keys outright keeps each list's text its own. Null or
        /// missing entries fall back to the derived key.
        /// </summary>
        public void AssignLocalizedEntries(List<string> entries, List<string> localizationKeys, List<string> tooltipKeys)
        {
            List<string> displayLabels = new List<string>(entries.Count);
            List<string> tooltips = new List<string>(entries.Count);
            bool anyTooltip = false;

            for (int i = 0; i < entries.Count; i++)
            {
                string key = (localizationKeys != null && i < localizationKeys.Count) ? localizationKeys[i] : entries[i];
                displayLabels.Add(BasisLocalization.Get(key));

                string tooltipKey = (tooltipKeys != null && i < tooltipKeys.Count && !string.IsNullOrEmpty(tooltipKeys[i]))
                    ? tooltipKeys[i]
                    : key + ".tooltip";

                bool found = BasisLocalization.TryGet(tooltipKey, out string tooltip);
                tooltips.Add(found ? tooltip : null);
                anyTooltip |= found;
            }

            AssignEntries(entries, displayLabels);
            SetOptionTooltips(anyTooltip ? tooltips : null);
        }

        /// <summary>
        /// Text shown in the tooltip bar while each option is hovered in the open list, indexed the
        /// same as <see cref="Entries"/>. Null entries fall back to the dropdown's own tooltip.
        /// </summary>
        public void SetOptionTooltips(List<string> optionTooltips)
        {
            _optionTooltips = optionTooltips;
            if (optionTooltips != null) EnsureOptionHover();
            RefreshHoverTooltip();
        }

        /// <summary>Sets one option's hover text, for lists whose entries are built one at a time.</summary>
        public void SetOptionTooltip(int index, string tooltip)
        {
            if (index < 0) return;

            _optionTooltips ??= new List<string>();
            while (_optionTooltips.Count <= index) _optionTooltips.Add(null);
            _optionTooltips[index] = tooltip;

            EnsureOptionHover();
            if (_hoveredOption == index) RefreshHoverTooltip();
        }

        public string GetOptionTooltip(int index)
        {
            if (_optionTooltips == null || index < 0 || index >= _optionTooltips.Count) return null;
            return _optionTooltips[index];
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();

            // A dropdown whose entries were never assigned still shows whatever options its prefab
            // shipped with, and clicking one of those rows used to dereference a null list. Report
            // no selection instead: the rows do not stand for anything this control can name.
            int selected = DropdownComponent.value;
            if (Entries == null || selected < 0 || selected >= Entries.Count) SetValue(string.Empty);
            else SetValue(Entries[selected]);

            AnimateSelectionChange();
        }

        public override void SetValueWithoutNotify(string value)
        {
            base.SetValueWithoutNotify(value);
            DropdownComponent.SetValueWithoutNotify(Index);
        }

        /// <summary>
        /// While an option in the open list is hovered its own text wins, so the bar describes the
        /// choice under the pointer instead of the dropdown as a whole. Options without text fall
        /// through to the normal control tooltip.
        /// </summary>
        protected override string HoverTooltipText
        {
            get
            {
                string option = GetOptionTooltip(_hoveredOption);
                return string.IsNullOrEmpty(option) ? base.HoverTooltipText : option;
            }
        }

        internal void SetHoveredOption(int index)
        {
            if (_hoveredOption == index) return;
            _hoveredOption = index;
            RefreshHoverTooltip();
        }

        internal void ClearHoveredOption(int index)
        {
            if (_hoveredOption != index) return;
            _hoveredOption = -1;
            RefreshHoverTooltip();
        }

        /// <summary>
        /// TMP_Dropdown builds its option list by cloning the item template when the list opens, so
        /// the hover relay goes onto that template once and rides along into every option. Adding it
        /// to the spawned items instead would mean waiting a frame for the list to be populated.
        /// The template's only Toggle is that item; TMP's own DropdownItem type is not public.
        /// </summary>
        private void EnsureOptionHover()
        {
            if (_optionHoverReady) return;
            if (DropdownComponent == null || DropdownComponent.template == null) return;

            Toggle item = DropdownComponent.template.GetComponentInChildren<Toggle>(true);
            if (item == null) return;

            if (!item.TryGetComponent(out PanelDropdownOptionHover _))
            {
                item.gameObject.AddComponent<PanelDropdownOptionHover>();
            }

            _optionHoverReady = true;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _hoveredOption = -1;
        }

        protected override void ApplyReset(string target)
        {
            base.ApplyReset(target);
            _previousIndex = DropdownComponent.value;
        }

        protected override bool TryConformShareValue(ref string value)
        {
            if (Entries == null) return false;
            for (int Index = 0; Index < Entries.Count; Index++)
            {
                if (string.Equals(Entries[Index], value, System.StringComparison.OrdinalIgnoreCase))
                {
                    value = Entries[Index];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Shows the option's display label rather than the raw stored value, since entries and
        /// their labels can differ (localized dropdowns store the entry, show the translation).
        /// </summary>
        protected override string FormatSettingValue(string value)
        {
            if (Entries != null && DropdownComponent != null && DropdownComponent.options.Count == Entries.Count)
            {
                for (int Index = 0; Index < Entries.Count; Index++)
                {
                    if (string.Equals(Entries[Index], value, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return DropdownComponent.options[Index].text;
                    }
                }
            }

            return base.FormatSettingValue(value);
        }

        private void AnimateSelectionChange()
        {
            if (!Application.isPlaying) return;

            int currentIndex = DropdownComponent.value;
            if (currentIndex == _previousIndex) return;
            _previousIndex = currentIndex;

            // Punch the dropdown itself to give selection feedback
            if (_selectionPunchTween != null && _selectionPunchTween.Active && _selectionPunchTween.Target == transform) _selectionPunchTween.Reset();

            _selectionPunchTween = transform.TweenScale(0.06f, transform.localScale, Vector3.one * 0.96f)
                .SetEase(Easing.OutCubic)
                .AddCallback(() =>
                {
                    if (this != null)
                    {
                        transform.TweenScale(0.14f, Vector3.one * 0.96f, Vector3.one)
                            .SetEase(Easing.OutBack);
                    }
                });
        }

        private void OnTransformChildrenChanged()
        {
            if (!Application.isPlaying) return;

            // Unity's TMP_Dropdown doesn't expose an "opened" event, so we have to
            // watch for the "Dropdown List" child GameObject it adds on open. This
            // fires only when the child list actually changes — previously a
            // LateUpdate polled childCount on every dropdown every frame.
            Transform list = transform.Find("Dropdown List");
            if (list != null && list != _dropdownList)
            {
                _dropdownList = list;
                AnimateDropdownListOpen();
            }
            else if (list == null)
            {
                _dropdownList = null;
                _hoveredOption = -1;
            }
        }

        private void AnimateDropdownListOpen()
        {
            if (_dropdownList == null) return;

            // Fade in the dropdown list
            if (!_dropdownList.TryGetComponent<CanvasGroup>(out CanvasGroup cg))
            {
                cg = _dropdownList.gameObject.AddComponent<CanvasGroup>();
            }

            if (_listFadeTween != null && _listFadeTween.Active && _listFadeTween.Target == cg) _listFadeTween.Reset();

            cg.alpha = 0f;
            _listFadeTween = cg.TweenAlpha(0.15f, 0f, 1f).SetEase(Easing.OutCubic);

            // Scale pop from slightly smaller
            _dropdownList.localScale = new Vector3(1f, 0.9f, 1f);
            _dropdownList.TweenScale(0.2f, new Vector3(1f, 0.9f, 1f), Vector3.one)
                .SetEase(Easing.OutCubic);
        }

        public int StringValueToIndex(string Active)
        {
            int Count = DropdownComponent.options.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                TMP_Dropdown.OptionData optionData = DropdownComponent.options[Index];
                if (Active == optionData.text)
                {
                    return Index;
                }
            }
            return 0;
        }
        public string SelectedString
        {
            get
            {
                if (DropdownComponent == null) return string.Empty;
                int index = DropdownComponent.value;
                if (index < 0 || index >= DropdownComponent.options.Count) return string.Empty;
                return DropdownComponent.options[index].text;
            }
        }

    }
}
