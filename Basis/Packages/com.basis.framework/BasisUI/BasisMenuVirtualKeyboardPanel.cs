using System;
using System.Collections.Generic;
using Basis.BTween;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI;
using Basis.Scripts.Virtual_keyboard;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;

namespace Basis.BasisUI
{
    /// <summary>
    /// Virtual keyboard built on the menu system. Always lives inside the
    /// main menu's PanelRoot — opens the main menu first if it isn't already
    /// visible. The active layout is auto-detected from
    /// <see cref="BasisLocalization.CurrentLanguage"/> via the
    /// <c>LocaleToLayout</c> map and rebuilt when the user switches languages.
    /// Layout: live input mirror row, alphabetic rows from the keyboard data,
    /// and a function row (caps, space, delete, copy, paste, enter, close).
    /// </summary>
    public class BasisMenuVirtualKeyboardPanel : BasisMenuPanel
    {
        public static class KeyboardStyles
        {
            public static string Default =>
                "Packages/com.basis.sdk/Prefabs/Virtual Keyboard Panel.prefab";
        }

        public const string TitleKey = "keyboard.title";
        public const string CapsKey = "keyboard.key.caps";
        public const string SpaceKey = "keyboard.key.space";
        public const string DeleteKey = "keyboard.key.delete";
        public const string EnterKey = "keyboard.key.enter";
        public const string CloseKey = "keyboard.key.close";
        public const string CopyKey = "keyboard.key.copy";
        public const string PasteKey = "keyboard.key.paste";
        public const string DisplayPlaceholderKey = "keyboard.display.placeholder";

        public static string Title => BasisLocalization.Get(TitleKey);

        public const string DefaultLayoutAddress =
            "Packages/com.basis.sdk/Settings/KeyboardLayoutData.asset";

        // Layout data is cached for the app lifetime — small SO, loaded once,
        // shared across every keyboard open/close cycle.
        private static KeyboardLayoutData _cachedDefaultLayout;

        public static PanelData KeyboardPanelData => new PanelData
        {
            Title = Title,
            PanelSize = new Vector2(1500, 660),
            PanelPosition = new Vector3(0, -100, -5),
        };

        public const float HeaderRowHeight = 70f;
        public const float FunctionRowHeight = 90f;
        public const float KeySpacing = 6f;
        public const float RowSpacing = 6f;

        public const float DeleteRepeatDelay = 0.5f;
        public const float DeleteRepeatInterval = 0.1f;

        // Shifted variants for non-letter keys — the number row swaps to
        // symbols when Caps is on, matching a physical QWERTY shift behavior.
        // Letters fall back to ToUpper / ToLower automatically.
        private static readonly Dictionary<string, string> ShiftMap = new Dictionary<string, string>
        {
            { "1", "!" }, { "2", "@" }, { "3", "#" }, { "4", "$" }, { "5", "%" },
            { "6", "^" }, { "7", "&" }, { "8", "*" }, { "9", "(" }, { "0", ")" },
            { "-", "_" }, { "=", "+" }, { "[", "{" }, { "]", "}" }, { "\\", "|" },
            { ";", ":" }, { "'", "\"" }, { ",", "<" }, { ".", ">" }, { "/", "?" },
            { "`", "~" },
        };

        // Maps BasisLocalization locale codes to a (language, style) pair in
        // the keyboard layout asset. Locale variants that share an input
        // method (es-MX → Spanish, zh-Hant → Mandarin/Pinyin) collapse onto
        // the same entry. Anything not listed here falls back to English.
        private static readonly Dictionary<string, (string Language, string Style)> LocaleToLayout =
            new Dictionary<string, (string Language, string Style)>(StringComparer.OrdinalIgnoreCase)
            {
                { "en",      ("English",          "QWERTY") },
                { "es",      ("Spanish",          "QWERTY") },
                { "es-MX",   ("Spanish",          "QWERTY") },
                { "fr",      ("French",           "AZERTY") },
                { "de",      ("German",           "QWERTZ") },
                { "pt",      ("Portuguese",       "QWERTY") },
                { "it",      ("Italian",          "QWERTY") },
                { "nl",      ("Dutch",            "QWERTY") },
                { "ru",      ("Russian",          "ЙЦУКЕН") },
                { "hi",      ("Hindi",            "Inscript") },
                { "bn",      ("Bengali",          "Probhat") },
                { "ar",      ("Standard Arabic",  "Arabic") },
                { "ur",      ("Urdu",             "Urdu Phonetic") },
                { "ja",      ("Japanese",         "Hiragana") },
                { "zh",      ("Mandarin Chinese", "Pinyin") },
                { "zh-CN",   ("Mandarin Chinese", "Pinyin") },
                { "zh-Hans", ("Mandarin Chinese", "Pinyin") },
                { "zh-Hant", ("Mandarin Chinese", "Pinyin") },
            };

        // Width weights drive HorizontalLayoutGroup proportions when
        // childForceExpandWidth is on. Tuning these shapes the keyboard.
        public const float LetterWeight = 1f;
        public const float ModifierWeight = 1.5f;
        public const float SpaceWeight = 5f;
        public const float CloseWeight = 0.75f;

        public static BasisMenuVirtualKeyboardPanel Instance { get; private set; }
        public static bool HasInstance => Instance != null && !Instance.IsReleased;

        public KeyboardLayoutData LayoutData;
        public string SelectedLanguage = "English";
        public string SelectedStyle = "QWERTY";
        public bool IsCapital;
        public LanguageStyle CurrentLanguageStyle;

        private static string _preferredLanguage;
        private static string _preferredStyle;

        public InputField InputField;
        public TMP_InputField TMPInputField;

        public PanelTextField DisplayField;

        public GameObject KeyboardArea;
        public readonly List<GameObject> RowObjects = new List<GameObject>();

        private readonly List<KeyEntry> _keys = new List<KeyEntry>();

        private GameObject _headerRow;
        private GameObject _functionRow;

        private bool _deleteHeld;
        private float _deleteNextRepeatTime;
        private bool _deleteRepeatFired;

        private struct KeyEntry
        {
            public PanelButton Button;
            public string BaseLabel;
            public BasisVirtualKeyboardSpecialKey Special;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            OnInstanceReleased += HandleReleased;
            BasisLocalization.OnLanguageChanged += HandleLocaleChanged;
        }

        private void HandleReleased()
        {
            StopDeleteHold();
            if (Instance == this) Instance = null;
            BasisLocalization.OnLanguageChanged -= HandleLocaleChanged;
            BasisCursorManagement.LockCursor(nameof(BasisMenuVirtualKeyboardPanel));
        }

        private void HandleLocaleChanged()
        {
            // Re-detect and rebuild only the keyboard area; the panel itself
            // and its display field can stay put.
            var (language, style) = DetectLocaleLayout();
            BuildLayoutInternal(language, style);
        }

        /// <summary>
        /// Open the keyboard with the default bundled layout. Convenience
        /// overload — equivalent to calling the explicit overload with
        /// <see cref="LoadDefaultLayout"/>.
        /// </summary>
        public static BasisMenuVirtualKeyboardPanel CreateNew(
            InputField inputField,
            TMP_InputField tmpInputField)
        {
            KeyboardLayoutData layout = LoadDefaultLayout();
            if (!layout) return null;
            return CreateNew(layout, inputField, tmpInputField);
        }

        /// <summary>
        /// Synchronously loads (and caches) the default keyboard layout from
        /// Addressables. Returns the cached instance on subsequent calls.
        /// </summary>
        public static KeyboardLayoutData LoadDefaultLayout()
        {
            if (_cachedDefaultLayout) return _cachedDefaultLayout;
            try
            {
                _cachedDefaultLayout = Addressables
                    .LoadAssetAsync<KeyboardLayoutData>(DefaultLayoutAddress)
                    .WaitForCompletion();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(
                    $"Failed to load default KeyboardLayoutData at {DefaultLayoutAddress}: {ex.Message}");
            }
            return _cachedDefaultLayout;
        }

        /// <summary>
        /// Open the keyboard panel and target the given input field. Returns
        /// the existing instance (with targets re-pointed) if one is already
        /// open. Opens the main menu first if it isn't already visible — the
        /// keyboard always parents under its PanelRoot.
        /// </summary>
        public static BasisMenuVirtualKeyboardPanel CreateNew(
            KeyboardLayoutData layoutData,
            InputField inputField,
            TMP_InputField tmpInputField)
        {
            if (HasInstance)
            {
                Instance.RetargetInput(inputField, tmpInputField);
                return Instance;
            }

            if (!layoutData)
            {
                BasisDebug.LogError("BasisMenuVirtualKeyboardPanel requires a KeyboardLayoutData asset.");
                return null;
            }

            if (!BasisMainMenu.Instance)
            {
                BasisMainMenu.Open();
            }

            if (!BasisMainMenu.Instance)
            {
                BasisDebug.LogError("Failed to open main menu — keyboard cannot spawn.");
                return null;
            }

            Component parent = BasisMainMenu.Instance.MenuObjectInstance.PanelRoot;

            BasisMenuVirtualKeyboardPanel panel =
                CreateNew<BasisMenuVirtualKeyboardPanel>(KeyboardStyles.Default, parent);

            if (!panel) return null;

            panel.LoadData(KeyboardPanelData);
            panel.LayoutData = layoutData;
            panel.InputField = inputField;
            panel.TMPInputField = tmpInputField;

            panel.BuildPanel();

            UIAnimations.PopIn(panel);
            BasisCursorManagement.UnlockCursor(nameof(BasisMenuVirtualKeyboardPanel));

            Instance = panel;
            return panel;
        }

        public static void Close()
        {
            if (HasInstance) Instance.ReleaseInstance();
        }

        /// <summary>
        /// Re-point the keyboard at a different input field without rebuilding
        /// the layout. Refreshes the live display.
        /// </summary>
        public void RetargetInput(InputField inputField, TMP_InputField tmpInputField)
        {
            InputField = inputField;
            TMPInputField = tmpInputField;
            UpdateDisplay();
        }

        private void BuildPanel()
        {
            ClearLayout();
            CreateRoot();
            CreateHeaderRow();
            CreateKeyboardArea();

            var (language, style) = ResolveInitialLayout();
            BuildLayoutInternal(language, style);

            UpdateDisplay();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        /// <summary>
        /// Tear down the keyboard area and rebuild for the given language/style.
        /// Falls back to the first language in the asset if the requested
        /// combination is missing.
        /// </summary>
        public void BuildLayout(string language, string style)
        {
            BuildLayoutInternal(language, style);
        }

        private void BuildLayoutInternal(string language, string style)
        {
            if (!LayoutData || KeyboardArea == null) return;
            CurrentLanguageStyle = LayoutData.GetLanguageStyle(language, style);
            if (CurrentLanguageStyle == null) return;

            SelectedLanguage = CurrentLanguageStyle.language;
            SelectedStyle = CurrentLanguageStyle.style;

            ClearKeyboardArea();
            CreateRows(CurrentLanguageStyle);
            CreateFunctionRow();
            ApplyCase();
        }

        private void ClearLayout()
        {
            ClearKeyboardArea();

            if (DisplayField && !DisplayField.IsReleased) DisplayField.ReleaseInstance();
            DisplayField = null;

            if (_headerRow) Destroy(_headerRow);
            _headerRow = null;

            if (KeyboardArea) Destroy(KeyboardArea);
            KeyboardArea = null;
        }

        private void ClearKeyboardArea()
        {
            StopDeleteHold();

            for (int i = 0; i < _keys.Count; i++)
            {
                PanelButton button = _keys[i].Button;
                if (button && !button.IsReleased) button.ReleaseInstance();
            }
            _keys.Clear();

            for (int i = 0; i < RowObjects.Count; i++)
            {
                if (RowObjects[i]) Destroy(RowObjects[i]);
            }
            RowObjects.Clear();

            if (_functionRow) Destroy(_functionRow);
            _functionRow = null;
        }

        private void CreateRoot()
        {
            VerticalLayoutGroup root = Descriptor.ContentParent.gameObject
                .GetComponent<VerticalLayoutGroup>();
            if (!root)
            {
                root = Descriptor.ContentParent.gameObject.AddComponent<VerticalLayoutGroup>();
            }
            root.childForceExpandHeight = false;
            root.childForceExpandWidth = true;
            root.childControlHeight = true;
            root.childControlWidth = true;
            root.spacing = RowSpacing;
            root.padding = new RectOffset(8, 8, 8, 8);
        }

        private void CreateHeaderRow()
        {
            _headerRow = new GameObject("Header Row");
            RectTransform rowTransform = _headerRow.AddComponent<RectTransform>();
            rowTransform.SetParent(Descriptor.ContentParent, false);
            rowTransform.localScale = Vector3.one;

            HorizontalLayoutGroup horizontal = _headerRow.AddComponent<HorizontalLayoutGroup>();
            horizontal.childForceExpandHeight = true;
            horizontal.childForceExpandWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childControlWidth = true;
            horizontal.spacing = KeySpacing;

            LayoutElement layout = _headerRow.AddComponent<LayoutElement>();
            layout.minHeight = HeaderRowHeight;
            layout.preferredHeight = HeaderRowHeight;
            layout.flexibleHeight = 0f;
            layout.flexibleWidth = 1f;

            DisplayField = PanelTextField.CreateNew(
                PanelTextField.TextFieldStyles.EntryWithNoTitle,
                rowTransform);

            DisplayField._inputField.interactable = false;
            DisplayField._inputField.readOnly = true;
            if (DisplayField._placeholderLabel)
            {
                DisplayField._placeholderLabel.text = BasisLocalization.Get(DisplayPlaceholderKey);
            }
        }

        /// <summary>
        /// Resolve the keyboard layout for the active
        /// <see cref="BasisLocalization.CurrentLanguage"/>. Falls back to
        /// English/QWERTY if the locale isn't mapped. Region tags ("es-MX",
        /// "zh-Hant") fall back to their base locale before defaulting.
        /// </summary>
        private static (string Language, string Style) DetectLocaleLayout()
        {
            string locale = BasisLocalization.CurrentLanguage;
            if (!string.IsNullOrEmpty(locale))
            {
                if (LocaleToLayout.TryGetValue(locale, out var exact)) return exact;

                int dash = locale.IndexOf('-');
                if (dash > 0)
                {
                    string baseLocale = locale.Substring(0, dash);
                    if (LocaleToLayout.TryGetValue(baseLocale, out var baseMatch)) return baseMatch;
                }
            }
            return ("English", "QWERTY");
        }

        private (string Language, string Style) ResolveInitialLayout()
        {
            if (!string.IsNullOrEmpty(_preferredLanguage) && LayoutExists(_preferredLanguage, _preferredStyle))
            {
                return (_preferredLanguage, _preferredStyle);
            }
            return DetectLocaleLayout();
        }

        private bool LayoutExists(string language, string style)
        {
            if (!LayoutData || LayoutData.languagesAndStyles == null) return false;
            List<LanguageStyle> list = LayoutData.languagesAndStyles;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].language == language && list[i].style == style)
                {
                    return true;
                }
            }
            return false;
        }

        private void CycleLanguage()
        {
            if (!LayoutData || LayoutData.languagesAndStyles == null) return;
            List<LanguageStyle> list = LayoutData.languagesAndStyles;
            if (list.Count == 0) return;

            int current = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].language == SelectedLanguage && list[i].style == SelectedStyle)
                {
                    current = i;
                    break;
                }
            }

            int next = (current + 1) % list.Count;
            LanguageStyle pick = list[next];
            if (pick == null) return;

            _preferredLanguage = pick.language;
            _preferredStyle = pick.style;

            BuildLayoutInternal(pick.language, pick.style);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        private void CreateKeyboardArea()
        {
            KeyboardArea = new GameObject("Keyboard Area");
            RectTransform areaTransform = KeyboardArea.AddComponent<RectTransform>();
            areaTransform.SetParent(Descriptor.ContentParent, false);
            areaTransform.localScale = Vector3.one;

            VerticalLayoutGroup vertical = KeyboardArea.AddComponent<VerticalLayoutGroup>();
            vertical.childForceExpandHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childControlHeight = true;
            vertical.childControlWidth = true;
            vertical.spacing = RowSpacing;

            LayoutElement areaLayout = KeyboardArea.AddComponent<LayoutElement>();
            areaLayout.flexibleHeight = 1f;
            areaLayout.flexibleWidth = 1f;
        }

        private void CreateRows(LanguageStyle language)
        {
            List<RowCollection> rows = language.rows;
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                List<string> rowKeys = rows[rowIndex].innerCollection;
                if (rowKeys == null || rowKeys.Count == 0) continue;

                // Skip rows whose entries are all whitespace or control-key
                // matches (e.g. English's bundled `[" ", "enter", "delete",
                // "caps", "paste", "close"]` row) — the function row at the
                // bottom already provides those keys.
                if (IsRowOnlyControlKeys(rowKeys, language.SpecialKeys)) continue;

                GameObject rowObject = CreateRow($"Row {rowIndex}", KeyboardArea.transform);
                RowObjects.Add(rowObject);

                for (int keyIndex = 0; keyIndex < rowKeys.Count; keyIndex++)
                {
                    string baseLabel = rowKeys[keyIndex];
                    BasisVirtualKeyboardSpecialKey special =
                        ResolveSpecialKey(baseLabel, language.SpecialKeys);

                    // Defensive: even if a row mixes characters and control
                    // keys, drop the control-key entries so they aren't
                    // duplicated with the function row.
                    if (special != BasisVirtualKeyboardSpecialKey.NotSpecial) continue;
                    if (string.IsNullOrWhiteSpace(baseLabel)) continue;

                    string style = StyleForSpecialKey(special);
                    float weight = WeightForSpecialKey(special);
                    AddKey(rowObject.transform, baseLabel, special, style, weight);
                }
            }
        }

        private void CreateFunctionRow()
        {
            // Sibling of the keyboard area inside ContentParent so its
            // explicit height isn't overridden by the keyboard area's
            // childForceExpandHeight.
            _functionRow = CreateRow("Function Row", Descriptor.ContentParent);
            LayoutElement layout = _functionRow.AddComponent<LayoutElement>();
            layout.minHeight = FunctionRowHeight;
            layout.preferredHeight = FunctionRowHeight;
            layout.flexibleHeight = 0f;

            string capsLabel = BasisLocalization.Get(CapsKey);
            string spaceLabel = BasisLocalization.Get(SpaceKey);
            string deleteLabel = BasisLocalization.Get(DeleteKey);
            string copyLabel = BasisLocalization.Get(CopyKey);
            string pasteLabel = BasisLocalization.Get(PasteKey);
            string enterLabel = BasisLocalization.Get(EnterKey);
            string closeLabel = BasisLocalization.Get(CloseKey);

            // Order: Language, Caps (modifiers, leftmost) | Space (dominant) |
            // Delete, Copy, Paste, Enter, Close (action keys, right-aligned)
            AddKey(_functionRow.transform, SelectedLanguage,
                BasisVirtualKeyboardSpecialKey.IsLanguageKey,
                PanelButton.ButtonStyles.StandardButton, ModifierWeight);

            AddKey(_functionRow.transform, capsLabel,
                BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey,
                PanelButton.ButtonStyles.StandardButton, ModifierWeight);

            AddKey(_functionRow.transform, spaceLabel,
                BasisVirtualKeyboardSpecialKey.NotSpecial,
                PanelButton.ButtonStyles.StandardButton, SpaceWeight,
                appendValue: " ");

            AddKey(_functionRow.transform, deleteLabel,
                BasisVirtualKeyboardSpecialKey.IsDeleteKey,
                PanelButton.ButtonStyles.StandardButton, ModifierWeight);

            AddKey(_functionRow.transform, copyLabel,
                BasisVirtualKeyboardSpecialKey.IsCopyKey,
                PanelButton.ButtonStyles.StandardButton, ModifierWeight);

            AddKey(_functionRow.transform, pasteLabel,
                BasisVirtualKeyboardSpecialKey.IsPasteKey,
                PanelButton.ButtonStyles.StandardButton, ModifierWeight);

            AddKey(_functionRow.transform, enterLabel,
                BasisVirtualKeyboardSpecialKey.IsEnterKey,
                PanelButton.ButtonStyles.AcceptButton, ModifierWeight);

            AddKey(_functionRow.transform, closeLabel,
                BasisVirtualKeyboardSpecialKey.IsCloseKey,
                PanelButton.ButtonStyles.CancelButton, CloseWeight);
        }

        private GameObject CreateRow(string name, Transform parent)
        {
            GameObject rowObject = new GameObject(name);
            RectTransform rowTransform = rowObject.AddComponent<RectTransform>();
            rowTransform.SetParent(parent, false);
            rowTransform.localScale = Vector3.one;

            HorizontalLayoutGroup horizontal = rowObject.AddComponent<HorizontalLayoutGroup>();
            horizontal.childForceExpandHeight = true;
            horizontal.childForceExpandWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childControlWidth = true;
            horizontal.spacing = KeySpacing;

            return rowObject;
        }

        private void AddKey(
            Transform parent,
            string baseLabel,
            BasisVirtualKeyboardSpecialKey special,
            string buttonStyle,
            float weight,
            string appendValue = null)
        {
            PanelButton button = PanelButton.CreateNew(buttonStyle, parent);
            button.Descriptor.SetTitle(baseLabel);

            if (button.Layout)
            {
                button.Layout.flexibleWidth = weight;
                button.Layout.minWidth = 0f;
                button.Layout.preferredWidth = 0f;
            }

            int captured = _keys.Count;
            button.OnClicked += () => OnKeyPressed(captured);

            if (special == BasisVirtualKeyboardSpecialKey.IsDeleteKey)
            {
                button.OnPressed += OnDeletePressed;
                button.OnReleased += OnDeleteReleased;
            }

            _keys.Add(new KeyEntry
            {
                Button = button,
                BaseLabel = appendValue ?? baseLabel,
                Special = special,
            });
        }

        private static bool IsRowOnlyControlKeys(
            List<string> rowKeys,
            List<SpecialKeySizes> specials)
        {
            for (int i = 0; i < rowKeys.Count; i++)
            {
                string key = rowKeys[i];
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (ResolveSpecialKey(key, specials) != BasisVirtualKeyboardSpecialKey.NotSpecial) continue;
                return false;
            }
            return true;
        }

        private static BasisVirtualKeyboardSpecialKey ResolveSpecialKey(
            string label,
            List<SpecialKeySizes> specials)
        {
            if (specials == null) return BasisVirtualKeyboardSpecialKey.NotSpecial;
            for (int i = 0; i < specials.Count; i++)
            {
                if (string.Equals(specials[i].Match, label, StringComparison.OrdinalIgnoreCase))
                {
                    return specials[i].BasisVirtualKeyboardSpecialKey;
                }
            }
            return BasisVirtualKeyboardSpecialKey.NotSpecial;
        }

        private static string StyleForSpecialKey(BasisVirtualKeyboardSpecialKey special)
        {
            switch (special)
            {
                case BasisVirtualKeyboardSpecialKey.IsEnterKey:
                    return PanelButton.ButtonStyles.AcceptButton;
                case BasisVirtualKeyboardSpecialKey.IsCloseKey:
                    return PanelButton.ButtonStyles.CancelButton;
                default:
                    return PanelButton.ButtonStyles.StandardButton;
            }
        }

        private static float WeightForSpecialKey(BasisVirtualKeyboardSpecialKey special)
        {
            return special == BasisVirtualKeyboardSpecialKey.NotSpecial
                ? LetterWeight
                : ModifierWeight;
        }

        private void ApplyCase()
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                KeyEntry entry = _keys[i];
                if (entry.Special != BasisVirtualKeyboardSpecialKey.NotSpecial) continue;
                // The space key has BaseLabel = " " — leave its visual label alone.
                if (string.IsNullOrWhiteSpace(entry.BaseLabel)) continue;
                entry.Button.Descriptor.SetTitle(GetDisplayChar(entry.BaseLabel, IsCapital));
            }
        }

        private static string GetDisplayChar(string baseLabel, bool capital)
        {
            if (string.IsNullOrEmpty(baseLabel)) return baseLabel;
            if (!capital) return baseLabel.ToLower();
            if (ShiftMap.TryGetValue(baseLabel, out string shifted)) return shifted;
            return baseLabel.ToUpper();
        }

        private void OnKeyPressed(int index)
        {
            if (index < 0 || index >= _keys.Count) return;
            KeyEntry entry = _keys[index];

            switch (entry.Special)
            {
                case BasisVirtualKeyboardSpecialKey.IsCloseKey:
                    ReleaseInstance();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsEnterKey:
                    SubmitTarget();
                    ReleaseInstance();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsCaseSwitchKey:
                    IsCapital = !IsCapital;
                    ApplyCase();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsLanguageKey:
                    CycleLanguage();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsDeleteKey:
                    if (_deleteRepeatFired)
                    {
                        _deleteRepeatFired = false;
                        return;
                    }
                    DeleteCharacter();
                    UpdateDisplay();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsCopyKey:
                    GUIUtility.systemCopyBuffer = ReadCurrentText();
                    return;

                case BasisVirtualKeyboardSpecialKey.IsPasteKey:
                    string buffer = GUIUtility.systemCopyBuffer;
                    if (!string.IsNullOrEmpty(buffer))
                    {
                        InsertText(buffer);
                        UpdateDisplay();
                    }
                    return;

                case BasisVirtualKeyboardSpecialKey.NotSpecial:
                default:
                    string toAppend = string.IsNullOrWhiteSpace(entry.BaseLabel)
                        ? entry.BaseLabel
                        : GetDisplayChar(entry.BaseLabel, IsCapital);
                    InsertText(toAppend);
                    UpdateDisplay();
                    return;
            }
        }

        private void UpdateDisplay()
        {
            if (DisplayField == null || DisplayField._inputField == null) return;
            DisplayField._inputField.SetTextWithoutNotify(ReadCurrentText());
        }

        private string ReadCurrentText()
        {
            if (TMPInputField) return TMPInputField.text;
            if (InputField) return InputField.text;
            return string.Empty;
        }

        private void InsertText(string value)
        {
            BasisTextFieldCaret.InsertAtCaret(TMPInputField, InputField, value);
        }

        private void SubmitTarget()
        {
            if (TMPInputField)
            {
                string text = TMPInputField.text;
                TMPInputField.onSubmit?.Invoke(text);
                TMPInputField.onEndEdit?.Invoke(text);
                return;
            }
            if (InputField)
            {
                InputField.onEndEdit?.Invoke(InputField.text);
            }
        }

        private void DeleteCharacter()
        {
            BasisTextFieldCaret.DeleteBeforeCaret(TMPInputField, InputField);
        }

        private void OnDeletePressed()
        {
            _deleteRepeatFired = false;
            if (_deleteHeld) return;
            _deleteHeld = true;
            _deleteNextRepeatTime = Time.unscaledTime + DeleteRepeatDelay;
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += TickDeleteRepeat;
        }

        private void OnDeleteReleased()
        {
            StopDeleteHold();
        }

        private void StopDeleteHold()
        {
            if (!_deleteHeld) return;
            _deleteHeld = false;
            BasisFrameClock.OnTick -= TickDeleteRepeat;
            BasisFrameClock.RemoveRequest();
        }

        private void TickDeleteRepeat()
        {
            if (!this)
            {
                StopDeleteHold();
                return;
            }
            if (Time.unscaledTime < _deleteNextRepeatTime) return;
            _deleteNextRepeatTime = Time.unscaledTime + DeleteRepeatInterval;
            _deleteRepeatFired = true;
            DeleteCharacter();
            UpdateDisplay();
        }
    }
}
