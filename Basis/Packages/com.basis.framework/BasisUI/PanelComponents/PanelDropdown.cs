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

        private int _previousIndex = -1;
        private TweenScale _selectionPunchTween;
        private TweenCanvasGroupAlpha _listFadeTween;
        private Transform _dropdownList;

        public int Index
        {
            get
            {
                if (Entries == null || Entries.Count == -1)
                {
                    return -1;
                }

                return Entries.IndexOf(Value);
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

        private void AnimateSelectionChange()
        {
            if (!Application.isPlaying) return;

            int currentIndex = DropdownComponent.value;
            if (currentIndex == _previousIndex) return;
            _previousIndex = currentIndex;

            // Punch the dropdown itself to give selection feedback
            if (_selectionPunchTween != null && _selectionPunchTween.Active) _selectionPunchTween.Reset();

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

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;

            // Detect when the dropdown list spawns and animate it
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

            if (_listFadeTween != null && _listFadeTween.Active) _listFadeTween.Reset();

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
