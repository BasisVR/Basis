using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;

namespace Basis.Scripts.Virtual_keyboard
{
    public class BasisVirtualKeyboard : BasisUIBase
    {
        public List<BasisVirtualRow> rows = new();
        public Canvas Canvas;
        public RectTransform CanvasRectTransform;
        public Button CopyFrom;
        public KeyboardLayoutData KeyboardLayoutData;
        public BasisGraphicUIRayCaster BasisGraphicUIRayCaster;

        public float RowWidth = 44;
        public float VerticalSpacing = 1;
        public float HorizontalSpacing = 1;
        public float rowHeight = 44f;

        public GameObject keyboardParent;

        public string SelectedLanguage = "English";
        public string SelectedStyle = "QWERTY";
        public bool IsCapital;
        public LanguageStyle CurrentSelectedLanguage;

        public static string VirtualKeyboard = "Virtual Keyboard";
        public static string KeyboardParentName = "KeyboardParent";
        public static string RowName = "Row";

        public static InputField InputField;
        public static TMP_InputField TMPInputField;

        public static void CreateMenu(InputField inputField, TMP_InputField tmpInputField)
        {
            var resource = new AddressableGenericResource(VirtualKeyboard, AddressableExpectedResult.SingleItem);
            OpenMenuNow(resource);
            InputField = inputField;
            TMPInputField = tmpInputField;
        }

        void OnEnable()
        {
            var language = KeyboardLayoutData.GetLanguageStyle(SelectedLanguage, SelectedStyle);
            SetupKeyboard(language, false);
        }

        public override void InitalizeEvent() => BasisCursorManagement.UnlockCursor(nameof(BasisHamburgerMenu));

        public override void DestroyEvent() => BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));

        public void ClearOutOldData()
        {
            if (keyboardParent != null)
                Destroy(keyboardParent);
            rows.Clear();
        }

        public void Callback(BasisVirtualKeyboardButton key)
        {
            bool isEnterOrClose = key.BasisVirtualKeyboardSpecialKey is BasisVirtualKeyboardSpecialKey.IsEnterKey or BasisVirtualKeyboardSpecialKey.IsCloseKey;
            if (isEnterOrClose)
            {
                CloseThisMenu();
                return;
            }

            if (key.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey)
            {
                SetupKeyboard(CurrentSelectedLanguage, !IsCapital);
                return;
            }

            ApplyKeyToInput(InputField, key);
            ApplyKeyToInput(TMPInputField, key);
        }

        void ApplyKeyToInput(dynamic field, BasisVirtualKeyboardButton key)
        {
            if (field == null) return;

            switch (key.BasisVirtualKeyboardSpecialKey)
            {
                case BasisVirtualKeyboardSpecialKey.IsDeleteKey:
                    if (field.text.Length > 0)
                        field.text = field.text[..^1];
                    break;
                case BasisVirtualKeyboardSpecialKey.NotSpecial:
                    field.text += key.Text.text;
                    break;
                case BasisVirtualKeyboardSpecialKey.IsPasteKey:
                    var paste = GUIUtility.systemCopyBuffer;
                    if (Uri.IsWellFormedUriString(paste, UriKind.RelativeOrAbsolute))
                        field.text += paste;
                    break;
            }
        }

        public void SetupKeyboard(LanguageStyle language, bool isCapital)
        {
            IsCapital = isCapital;
            CurrentSelectedLanguage = language;
            ClearOutOldData();

            keyboardParent = new GameObject(KeyboardParentName);
            keyboardParent.transform.SetParent(transform, false);
            keyboardParent.transform.localScale = Vector3.one;
            var keyboardRect = keyboardParent.AddComponent<RectTransform>();

            var rowsData = language.rows;
            float maxRowWidth = 0f;

            foreach (var row in rowsData)
            {
                for (int i = 0; i < row.innerCollection.Count; i++)
                    row.innerCollection[i] = isCapital ? row.innerCollection[i].ToUpper() : row.innerCollection[i].ToLower();

                float rowWidth = row.innerCollection.Count * (RowWidth + VerticalSpacing);
                if (rowWidth > maxRowWidth) maxRowWidth = rowWidth;
            }

            float totalHeight = (rowHeight + VerticalSpacing) * rowsData.Count;
            keyboardRect.sizeDelta = new Vector2(maxRowWidth, totalHeight);
            CanvasRectTransform.sizeDelta = keyboardRect.sizeDelta;

            var layout = keyboardParent.AddComponent<VerticalLayoutGroup>();
            SetCommon(layout, VerticalSpacing);
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            foreach (var row in rowsData)
            {
                var rowObject = new GameObject(RowName);
                rowObject.transform.SetParent(keyboardParent.transform, false);
                rowObject.transform.localScale = Vector3.one;

                var rowRect = rowObject.AddComponent<RectTransform>();
                rowRect.sizeDelta = new Vector2(maxRowWidth, rowHeight + VerticalSpacing);

                var hLayout = rowObject.AddComponent<HorizontalLayoutGroup>();
                SetCommon(hLayout, HorizontalSpacing);
                hLayout.childControlHeight = false;
                hLayout.childControlWidth = false;

                var virtualRow = new BasisVirtualRow { RowObject = rowObject };

                foreach (var label in row.innerCollection)
                {
                    var button = CreateButton(label);
                    button.ButtonRect.SetParent(rowObject.transform, false);
                    button.ButtonRect.localScale = Vector3.one;

                    if (virtualRow.SetupButton(button, language.SpecialKeys, RowWidth, out var special))
                        button.BasisVirtualKeyboardSpecialKey = special.BasisVirtualKeyboardSpecialKey;

                    button.Button.onClick.AddListener(() => Callback(button));
                    virtualRow.RowButtons.Add(button);
                }

                rows.Add(virtualRow);
            }
        }

        public void SetCommon(HorizontalOrVerticalLayoutGroup layout, float spacing)
        {
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;
            layout.spacing = spacing;
        }

        BasisVirtualKeyboardButton CreateButton(string label)
        {
            var obj = Instantiate(CopyFrom.gameObject, transform);
            if (!obj.TryGetComponent(out RectTransform rect))
            {
                BasisDebug.LogError("Missing RectTransform");
                return new BasisVirtualKeyboardButton();
            }

            var text = obj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.autoSizeTextContainer = true;
                text.fontSizeMin = 0.1f;
                text.text = label;
            }

            obj.name = label;
            obj.SetActive(true);

            if (obj.TryGetComponent<Button>(out var btn))
            {
                return new BasisVirtualKeyboardButton
                {
                    ButtonRect = rect,
                    Text = text,
                    Button = btn
                };
            }

            BasisDebug.LogError("Missing Button");
            return new BasisVirtualKeyboardButton();
        }
    }
}
