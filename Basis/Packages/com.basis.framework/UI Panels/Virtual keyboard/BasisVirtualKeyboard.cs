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
        public bool IsSymbolMode;
        public static InputField InputField;
        public static TMP_InputField TMPInputField;
        public LanguageStyle CurrentSelectedLanguage;
        public static string VirtualKeyboard = "Virtual Keyboard";
        public static bool HasInstance;

        // Symbol layers for when the user toggles to symbols mode
        private static readonly List<List<string>> SymbolRows = new List<List<string>>
        {
            new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new List<string> { "@", "#", "$", "%", "&", "*", "-", "+", "(", ")" },
            new List<string> { "=", "\"", "'", ":", ";", "!", "?", "/", "\\" },
            new List<string> { "~", "`", "^", "|", "{", "}", "[", "]", "<", ">" },
        };

        public static void CreateMenu(InputField inputField, TMP_InputField tMP_InputField)
        {
            HasInstance = true;
            OpenMenuNow(VirtualKeyboard);
            InputField = inputField;
            TMPInputField = tMP_InputField;
        }
        public void OnDestroy()
        {
            HasInstance = false;
        }
        void OnEnable()
        {
            LanguageStyle Language = KeyboardLayoutData.GetLanguageStyle(SelectedLanguage, SelectedStyle);
            IsSymbolMode = false;
            SetupKeyboard(Language, false);
        }
        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(BasisVirtualKeyboard));
        }
        public void ClearOutOldData()
        {
            if (keyboardParent != null)
            {
                Destroy(keyboardParent);
            }
            rows.Clear();
        }

        /// <summary>
        /// Applies a text change to whichever input field is active and fires onValueChanged.
        /// </summary>
        private void ApplyTextChange(Action<InputField> legacyAction, Action<TMP_InputField> tmpAction)
        {
            if (InputField != null)
            {
                legacyAction(InputField);
                InputField.onValueChanged.Invoke(InputField.text);
            }
            if (TMPInputField != null)
            {
                tmpAction(TMPInputField);
                TMPInputField.onValueChanged.Invoke(TMPInputField.text);
            }
        }

        public void Callback(BasisVirtualKeyboardButton KeyInformation)
        {
            switch (KeyInformation.BasisVirtualKeyboardSpecialKey)
            {
                case BasisVirtualKeyboardSpecialKey.IsEnterKey:
                case BasisVirtualKeyboardSpecialKey.IsCloseKey:
                    HasInstance = false;
                    CloseThisMenu();
                    break;
                case BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey:
                    SetupKeyboard(CurrentSelectedLanguage, !IsCapital);
                    break;
                case BasisVirtualKeyboardSpecialKey.IsSymbolKey:
                    IsSymbolMode = !IsSymbolMode;
                    SetupKeyboard(CurrentSelectedLanguage, IsCapital);
                    break;
                case BasisVirtualKeyboardSpecialKey.IsDeleteKey:
                    ApplyTextChange(
                        f => { if (f.text.Length > 0) f.text = f.text.Substring(0, f.text.Length - 1); },
                        f => { if (f.text.Length > 0) f.text = f.text.Substring(0, f.text.Length - 1); }
                    );
                    break;
                case BasisVirtualKeyboardSpecialKey.IsSpaceKey:
                    ApplyTextChange(
                        f => f.text += " ",
                        f => f.text += " "
                    );
                    break;
                case BasisVirtualKeyboardSpecialKey.IsPasteKey:
                    {
                        string copiedText = GUIUtility.systemCopyBuffer;
                        if (!string.IsNullOrEmpty(copiedText))
                        {
                            ApplyTextChange(
                                f => f.text += copiedText,
                                f => f.text += copiedText
                            );
                        }
                        break;
                    }
                case BasisVirtualKeyboardSpecialKey.IsCopyKey:
                    if (TMPInputField != null)
                        GUIUtility.systemCopyBuffer = TMPInputField.text;
                    else if (InputField != null)
                        GUIUtility.systemCopyBuffer = InputField.text;
                    break;
                case BasisVirtualKeyboardSpecialKey.NotSpecial:
                default:
                    {
                        string character = KeyInformation.Text.text;
                        ApplyTextChange(
                            f => f.text += character,
                            f => f.text += character
                        );
                        break;
                    }
            }
        }

        /// <summary>
        /// Sets up the keyboard layout. Uses symbol rows when in symbol mode,
        /// otherwise uses the language layout. Capitalization is applied to copies
        /// of the row data so the ScriptableObject is never mutated.
        /// </summary>
        public void SetupKeyboard(LanguageStyle Language, bool IsCapitalization)
        {
            IsCapital = IsCapitalization;
            CurrentSelectedLanguage = Language;
            ClearOutOldData();

            keyboardParent = new GameObject(KeyboardParent);
            keyboardParent.transform.SetParent(this.transform);
            keyboardParent.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            keyboardParent.transform.localScale = Vector3.one;

            RectTransform keyboardParentRectTransform = keyboardParent.AddComponent<RectTransform>();

            // Build the display rows: either symbol layer or language layout + utility row
            List<List<string>> displayRows = new List<List<string>>();

            if (IsSymbolMode)
            {
                foreach (var symbolRow in SymbolRows)
                {
                    displayRows.Add(new List<string>(symbolRow));
                }
            }
            else
            {
                // Copy row data and apply capitalization without mutating the ScriptableObject
                List<RowCollection> sourceRows = Language.rows;
                for (int i = 0; i < sourceRows.Count; i++)
                {
                    List<string> items = sourceRows[i].innerCollection;
                    List<string> rowCopy = new List<string>(items.Count);
                    for (int j = 0; j < items.Count; j++)
                    {
                        rowCopy.Add(IsCapitalization ? items[j].ToUpper() : items[j].ToLower());
                    }
                    displayRows.Add(rowCopy);
                }
            }

            // Add the punctuation row (before utility keys)
            if (!IsSymbolMode)
            {
                displayRows.Add(new List<string> { ".", ",", "?", "!", "'", "-", "@", ":" });
            }

            // Add the bottom utility row with special keys
            string capsLabel = IsCapital ? "abc" : "ABC";
            string symbolLabel = IsSymbolMode ? "ABC" : "#+=";
            displayRows.Add(new List<string> { capsLabel, symbolLabel, "Space", "Del", "Paste", "Copy", "Enter", "Close" });

            // Calculate total width from the widest row
            float totalWidth = 0f;
            foreach (var row in displayRows)
            {
                float rowWidth = 0f;
                foreach (var key in row)
                {
                    float keyWidth = GetKeyWidth(key, Language.SpecialKeys);
                    rowWidth += keyWidth + HorizontalSpacing;
                }
                if (rowWidth > totalWidth)
                {
                    totalWidth = rowWidth;
                }
            }

            keyboardParentRectTransform.sizeDelta = new Vector2(totalWidth, (rowHeight + VerticalSpacing) * displayRows.Count);
            CanvasRectTransform.sizeDelta = keyboardParentRectTransform.sizeDelta;

            VerticalLayoutGroup verticalLayoutGroup = keyboardParent.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childControlWidth = true;
            SetCommon(verticalLayoutGroup, VerticalSpacing);

            // Build each row
            foreach (var row in displayRows)
            {
                GameObject rowObject = new GameObject(RowName);
                rowObject.transform.SetParent(keyboardParent.transform);
                rowObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                rowObject.transform.localScale = Vector3.one;

                RectTransform rowRectTransform = rowObject.AddComponent<RectTransform>();
                rowRectTransform.sizeDelta = new Vector2(totalWidth, rowHeight + VerticalSpacing);

                HorizontalLayoutGroup horizontalLayoutGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
                horizontalLayoutGroup.childControlHeight = false;
                horizontalLayoutGroup.childControlWidth = false;
                SetCommon(horizontalLayoutGroup, HorizontalSpacing);

                BasisVirtualRow basisRow = new BasisVirtualRow();
                basisRow.RowObject = rowObject;

                for (int keyIndex = 0; keyIndex < row.Count; keyIndex++)
                {
                    string keyLabel = row[keyIndex];
                    BasisVirtualKeyboardButton button = CreateButton(keyLabel);
                    button.ButtonRect.SetParent(rowObject.transform);
                    button.ButtonRect.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    button.ButtonRect.localScale = Vector3.one;

                    // Check for built-in utility keys first
                    BasisVirtualKeyboardSpecialKey builtInKey = GetBuiltInSpecialKey(keyLabel);
                    if (builtInKey != BasisVirtualKeyboardSpecialKey.NotSpecial)
                    {
                        button.BasisVirtualKeyboardSpecialKey = builtInKey;
                        basisRow.SetScale(button, GetUtilityKeyWidth(builtInKey));
                        // Apply display labels for utility keys
                        button.Text.text = GetUtilityKeyDisplayLabel(builtInKey, keyLabel);
                    }
                    else if (basisRow.SetupButton(button, Language.SpecialKeys, RowWidth, out SpecialKeySizes FoundSpecial))
                    {
                        button.BasisVirtualKeyboardSpecialKey = FoundSpecial.BasisVirtualKeyboardSpecialKey;
                    }

                    button.Button.onClick.AddListener(() => Callback(button));
                    basisRow.RowButtons.Add(button);
                }
                rows.Add(basisRow);
            }
        }

        /// <summary>
        /// Returns the special key type for built-in utility key labels.
        /// </summary>
        private BasisVirtualKeyboardSpecialKey GetBuiltInSpecialKey(string label)
        {
            switch (label)
            {
                case "Del": return BasisVirtualKeyboardSpecialKey.IsDeleteKey;
                case "ABC":
                case "abc": return BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey;
                case "Enter": return BasisVirtualKeyboardSpecialKey.IsEnterKey;
                case "Close": return BasisVirtualKeyboardSpecialKey.IsCloseKey;
                case "Paste": return BasisVirtualKeyboardSpecialKey.IsPasteKey;
                case "Copy": return BasisVirtualKeyboardSpecialKey.IsCopyKey;
                case "#+=": return BasisVirtualKeyboardSpecialKey.IsSymbolKey;
                case "Space": return BasisVirtualKeyboardSpecialKey.IsSpaceKey;
                default: return BasisVirtualKeyboardSpecialKey.NotSpecial;
            }
        }

        /// <summary>
        /// Returns the display width for a built-in utility key.
        /// </summary>
        private float GetUtilityKeyWidth(BasisVirtualKeyboardSpecialKey key)
        {
            switch (key)
            {
                case BasisVirtualKeyboardSpecialKey.IsSpaceKey: return RowWidth * 2.5f;
                case BasisVirtualKeyboardSpecialKey.IsDeleteKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsSymbolKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsEnterKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsCloseKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsPasteKey: return RowWidth * 1.5f;
                case BasisVirtualKeyboardSpecialKey.IsCopyKey: return RowWidth * 1.5f;
                default: return RowWidth;
            }
        }

        /// <summary>
        /// Returns a user-friendly display label for utility keys.
        /// </summary>
        private string GetUtilityKeyDisplayLabel(BasisVirtualKeyboardSpecialKey key, string originalLabel)
        {
            switch (key)
            {
                case BasisVirtualKeyboardSpecialKey.IsSpaceKey: return " ";
                case BasisVirtualKeyboardSpecialKey.IsDeleteKey: return "\u232B";
                case BasisVirtualKeyboardSpecialKey.IsEnterKey: return "\u23CE";
                case BasisVirtualKeyboardSpecialKey.IsCloseKey: return "\u2715";
                default: return originalLabel;
            }
        }

        /// <summary>
        /// Gets the width for a specific key, checking special keys configuration first.
        /// </summary>
        private float GetKeyWidth(string keyLabel, List<SpecialKeySizes> specialKeys)
        {
            BasisVirtualKeyboardSpecialKey builtIn = GetBuiltInSpecialKey(keyLabel);
            if (builtIn != BasisVirtualKeyboardSpecialKey.NotSpecial)
            {
                return GetUtilityKeyWidth(builtIn);
            }

            if (specialKeys != null)
            {
                foreach (var sk in specialKeys)
                {
                    if (keyLabel.Equals(sk.Match, StringComparison.OrdinalIgnoreCase))
                    {
                        return sk.WidthSize;
                    }
                }
            }

            return RowWidth;
        }

        public void SetCommon(HorizontalOrVerticalLayoutGroup HorizontalOrVerticalLayoutGroup, float Spacing)
        {
            HorizontalOrVerticalLayoutGroup.childForceExpandHeight = false;
            HorizontalOrVerticalLayoutGroup.childForceExpandWidth = false;
            HorizontalOrVerticalLayoutGroup.spacing = Spacing;
        }

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
            BasisCursorManagement.LockCursor(nameof(BasisVirtualKeyboard));
        }
    }
}
