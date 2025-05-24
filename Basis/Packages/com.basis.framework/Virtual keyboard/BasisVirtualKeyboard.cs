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
        public List<BasisVirtualRow> rows = new List<BasisVirtualRow>();
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
        public static string KeyboardParent = "KeyboardParent";
        public static string RowName = "Row";
        public string SelectedLanguage = "English";
        public string SelectedStyle = "QWERTY";
        public bool IsCapital;
        public static InputField InputField;
        public static TMP_InputField TMPInputField;
        public LanguageStyle CurrentSelectedLanguage;
        public static string VirtualKeyboard = "Virtual Keyboard";
        public static bool HasInstance;
        public static void CreateMenu(InputField inputField, TMP_InputField tMP_InputField)
        {
            HasInstance = true;
            AddressableGenericResource resource = new AddressableGenericResource(VirtualKeyboard, AddressableExpectedResult.SingleItem);
            OpenMenuNow(resource);
            InputField = inputField;
            TMPInputField = tMP_InputField;
        }
        // Start is called before the first frame update
        void OnEnable()
        {
            LanguageStyle Language = KeyboardLayoutData.GetLanguageStyle(SelectedLanguage, SelectedStyle);
            SetupKeyboard(Language, false);
        }
        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(BasisHamburgerMenu));
        }
        public void ClearOutOldData()
        {
            // Clear existing rows and destroy previous keyboard parent
            if (keyboardParent != null)
            {
                Destroy(keyboardParent);
            }
            rows.Clear();
        }
        public void Callback(BasisVirtualKeyboardButton KeyInformation)
        {
            if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsEnterKey || KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsCloseKey)
            {
                HasInstance = false;
                CloseThisMenu();
            }
            else
            {
                if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey)
                {
                    SetupKeyboard(CurrentSelectedLanguage, !IsCapital);
                }
                else
                {
                    if (InputField != null)
                    {
                        if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsDeleteKey)
                        {
                            if (InputField.text.Length > 0)
                            {
                                InputField.text = InputField.text.Substring(0, InputField.text.Length - 1);
                            }
                        }
                        else
                        {
                            if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.NotSpecial)
                            {
                                InputField.text += KeyInformation.Text.text;
                            }
                            else
                            {
                                if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsPasteKey)
                                {
                                    string copiedText = GUIUtility.systemCopyBuffer; // Get the clipboard text

                                    if (Uri.IsWellFormedUriString(copiedText, UriKind.RelativeOrAbsolute))
                                    {
                                        InputField.text += copiedText;
                                    }
                                }
                            }
                        }
                    }
                    if (TMPInputField != null)
                    {
                        if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsDeleteKey)
                        {
                            if (TMPInputField.text.Length > 0)
                            {
                                TMPInputField.text = TMPInputField.text.Substring(0, TMPInputField.text.Length - 1);
                            }
                        }
                        else
                        {
                            if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.NotSpecial)
                            {
                                TMPInputField.text += KeyInformation.Text.text;
                            }
                            else
                            {
                                if (KeyInformation.BasisVirtualKeyboardSpecialKey == BasisVirtualKeyboardSpecialKey.IsPasteKey)
                                {
                                    string copiedText = GUIUtility.systemCopyBuffer; // Get the clipboard text

                                    if (Uri.IsWellFormedUriString(copiedText, UriKind.RelativeOrAbsolute))
                                    {
                                        TMPInputField.text += copiedText;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        // Setup the QWERTY keyboard layout
        public void SetupKeyboard(LanguageStyle Language, bool IsCapitalization)
        {
            IsCapital = IsCapitalization;
            CurrentSelectedLanguage = Language;
            ClearOutOldData();
            // Create a parent object to hold all the rows
            keyboardParent = new GameObject(KeyboardParent);
            keyboardParent.transform.SetParent(this.transform);
            keyboardParent.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            keyboardParent.transform.localScale = Vector3.one;

            // Ensure the parent object has a RectTransform component
            RectTransform keyboardParentRectTransform = keyboardParent.AddComponent<RectTransform>();

            // Calculate the required width based on the widest row and fixed height of 50 units
            float totalWidth = 0f;
            List<RowCollection> Rows = Language.rows;
            for (int Index = 0; Index < Rows.Count; Index++)
            {
                List<string> ItemsInRow = Rows[Index].innerCollection;
                for (int RowIndex = 0; RowIndex < ItemsInRow.Count; RowIndex++)
                {
                    if (IsCapitalization)
                    {
                        ItemsInRow[RowIndex] = ItemsInRow[RowIndex].ToUpper();
                    }
                    else
                    {
                        ItemsInRow[RowIndex] = ItemsInRow[RowIndex].ToLower();
                    }

                }
            }
            foreach (var row in Rows)
            {
                float rowWidth = row.innerCollection.Count * (RowWidth + VerticalSpacing); // Assuming each button is 50 units wide
                if (rowWidth > totalWidth)
                {
                    totalWidth = rowWidth;
                }
            }

            // Set the size of the RectTransform to match the calculated width and fixed height
            keyboardParentRectTransform.sizeDelta = new Vector2(totalWidth, (rowHeight + VerticalSpacing) * Rows.Count);

            // Set the size of the Canvas to match the RectTransform
            CanvasRectTransform.sizeDelta = keyboardParentRectTransform.sizeDelta;

            // Add VerticalLayoutGroup to the parent object
            VerticalLayoutGroup verticalLayoutGroup = keyboardParent.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childControlWidth = true;
            SetCommon(verticalLayoutGroup, VerticalSpacing);

            // Create the rows and buttons based on the layout
            foreach (var row in Rows)
            {
                // Create a new GameObject for the row
                GameObject rowObject = new GameObject(RowName);
                rowObject.transform.SetParent(keyboardParent.transform);
                rowObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                rowObject.transform.localScale = Vector3.one;

                RectTransform rowRectTransform = rowObject.AddComponent<RectTransform>();
                rowRectTransform.sizeDelta = new Vector2(totalWidth, rowHeight + VerticalSpacing);

                // Add HorizontalLayoutGroup to the row object
                HorizontalLayoutGroup horizontalLayoutGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
                horizontalLayoutGroup.childControlHeight = false;
                horizontalLayoutGroup.childControlWidth = false;
                SetCommon(horizontalLayoutGroup, HorizontalSpacing);

                BasisVirtualRow basisRow = new BasisVirtualRow();
                basisRow.RowObject = rowObject;

                List<string> ItemsInRow = row.innerCollection;
                for (int RowIndex = 0; RowIndex < ItemsInRow.Count; RowIndex++)
                {
                    BasisVirtualKeyboardButton button = CreateButton(ItemsInRow[RowIndex]);
                    button.ButtonRect.SetParent(rowObject.transform);
                    button.ButtonRect.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    button.ButtonRect.localScale = Vector3.one;
                    if (basisRow.SetupButton(button, Language.SpecialKeys, RowWidth, out SpecialKeySizes FoundSpecial))
                    {
                        button.BasisVirtualKeyboardSpecialKey = FoundSpecial.BasisVirtualKeyboardSpecialKey;
                    }
                    button.Button.onClick.AddListener(() => Callback(button));
                    basisRow.RowButtons.Add(button);
                    rows.Add(basisRow);
                }
            }
        }
        public void SetCommon(HorizontalOrVerticalLayoutGroup HorizontalOrVerticalLayoutGroup, float Spacing)
        {
            HorizontalOrVerticalLayoutGroup.childForceExpandHeight = false;
            HorizontalOrVerticalLayoutGroup.childForceExpandWidth = false;
            HorizontalOrVerticalLayoutGroup.spacing = Spacing;
        }

        // Helper method to create a button
        BasisVirtualKeyboardButton CreateButton(string label)
        {
            GameObject buttonObject = GameObject.Instantiate(CopyFrom.gameObject, this.transform);
            if (buttonObject.TryGetComponent<RectTransform>(out RectTransform buttonRectTransform))
            {
                TextMeshProUGUI textMeshProUGUI = buttonObject.GetComponentInChildren<TextMeshProUGUI>();

                textMeshProUGUI.autoSizeTextContainer = true;
                textMeshProUGUI.fontSizeMin = 0.1f;
                textMeshProUGUI.text = label;

                buttonObject.name = label;
                buttonObject.SetActive(true);
                if (buttonObject.TryGetComponent<Button>(out Button Button))
                {
                    BasisVirtualKeyboardButton BVKB = new BasisVirtualKeyboardButton
                    {
                        ButtonRect = buttonRectTransform,
                        Text = textMeshProUGUI,
                        Button = Button,
                    };
                    return BVKB;
                }
                else
                {
                    BasisDebug.LogError("Missing Button");
                    return new BasisVirtualKeyboardButton();
                }
            }
            else
            {
                BasisDebug.LogError("Missing RectTransform");
                return new BasisVirtualKeyboardButton();
            }
        }

        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
        }
    }
}
