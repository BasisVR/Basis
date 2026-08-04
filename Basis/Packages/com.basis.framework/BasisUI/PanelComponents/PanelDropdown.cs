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
            SetValueWithoutNotify(Value);
        }

        public void AssignEntries(List<string> entries, List<string> displayLabels)
        {
            Entries = entries;
            DropdownComponent.ClearOptions();
            DropdownComponent.AddOptions(displayLabels != null && displayLabels.Count == entries.Count ? displayLabels : entries);
            SetValueWithoutNotify(Value);
        }

        public void AssignLocalizedEntries(List<string> entries, List<string> localizationKeys)
        {
            List<string> displayLabels = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                string key = (localizationKeys != null && i < localizationKeys.Count) ? localizationKeys[i] : entries[i];
                displayLabels.Add(BasisLocalization.Get(key));
            }
            AssignEntries(entries, displayLabels);
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            if (DropdownComponent.value == -1) SetValue(string.Empty);
            else SetValue(Entries[DropdownComponent.value]);

            AnimateSelectionChange();
        }

        public override void SetValueWithoutNotify(string value)
        {
            base.SetValueWithoutNotify(value);
            DropdownComponent.SetValueWithoutNotify(Index);
        }

        protected override void ApplyReset(string target)
        {
            base.ApplyReset(target);
            _previousIndex = DropdownComponent.value;
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
