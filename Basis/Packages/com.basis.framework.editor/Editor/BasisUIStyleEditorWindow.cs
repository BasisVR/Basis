#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Basis.BasisUI.Styling;
using Basis.Editor.Localization;
using System;

public class BasisUIStyleEditorWindow : EditorWindow
{
    private Vector2 scrollPosition;
    private UiStylePalette palette;
    private UiStyleLibrary library;

    private bool showBackgrounds = true;
    private bool showUI = true;
    private bool showFonts = true;
    private bool showStatus = true;
    private bool showOther = true;
    private bool showImageStyles = false;
    private bool showLabelStyles = false;
    private bool showSelectableStyles = false;

    [MenuItem("Basis/Settings/UI Style Editor", false, 402)]
    public static void Open()
    {
        GetWindow<BasisUIStyleEditorWindow>(BasisEditorLocalization.Get("editor.uiStyle.windowTitle"));
    }

    private void OnEnable()
    {
        LoadAssets();
    }

    private void LoadAssets()
    {
        palette = AssetDatabase.LoadAssetAtPath<UiStylePalette>(
            "Packages/com.basis.sdk/Settings/StylePalette.asset");
        library = AssetDatabase.LoadAssetAtPath<UiStyleLibrary>(
            "Packages/com.basis.sdk/Settings/StyleLibrary.asset");
    }

    private void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.uiStyle.header"),
            BasisEditorLocalization.Get("editor.uiStyle.subtitle"));

        if (palette == null || library == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.uiStyle.loadFailed"), MessageType.Error);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.retry")))
                LoadAssets();
            return;
        }

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.uiStyle.palette.title"));
        EditorGUILayout.HelpBox(
            BasisEditorLocalization.Get("editor.uiStyle.palette.help"),
            MessageType.Info);

        EditorGUILayout.Space();

        Undo.RecordObject(palette, "UI Style Palette Change");

        DrawPaletteSection();

        EditorGUILayout.Space(10);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.uiStyle.library.title"));
        EditorGUILayout.Space();

        Undo.RecordObject(library, "UI Style Library Change");

        DrawLibrarySection();

        EditorGUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.uiStyle.applyRefreshAll")))
            {
                EditorUtility.SetDirty(palette);
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssets();

                if (Application.isPlaying)
                {
                    UiStyleSettings.UpdateAllStyleComponents();
                }
            }

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.uiStyle.revert")))
            {
                LoadAssets();
                Repaint();
            }
        }

        EditorGUILayout.EndScrollView();

        if (GUI.changed)
        {
            EditorUtility.SetDirty(palette);
            EditorUtility.SetDirty(library);

            if (Application.isPlaying)
            {
                UiStyleSettings.UpdateAllStyleComponents();
            }
        }
    }

    // ---- Palette ----

    private void DrawPaletteSection()
    {
        showBackgrounds = EditorGUILayout.Foldout(showBackgrounds, BasisEditorLocalization.Get("editor.uiStyle.palette.backgroundColors"), true);
        if (showBackgrounds)
        {
            EditorGUI.indentLevel++;
            palette.BackgroundColor1 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.background1"), palette.BackgroundColor1);
            palette.BackgroundColor2 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.background2"), palette.BackgroundColor2);
            palette.BackgroundColor3 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.background3"), palette.BackgroundColor3);
            palette.LayerColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.layer"), palette.LayerColor);
            EditorGUI.indentLevel--;
        }

        showUI = EditorGUILayout.Foldout(showUI, BasisEditorLocalization.Get("editor.uiStyle.palette.uiColors"), true);
        if (showUI)
        {
            EditorGUI.indentLevel++;
            palette.AccentColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.accent"), palette.AccentColor);
            palette.ButtonColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.button"), palette.ButtonColor);
            palette.InputFieldColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.inputField"), palette.InputFieldColor);
            palette.Scrollbar = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.scrollbar"), palette.Scrollbar);
            EditorGUI.indentLevel--;
        }

        showFonts = EditorGUILayout.Foldout(showFonts, BasisEditorLocalization.Get("editor.uiStyle.palette.fontColors"), true);
        if (showFonts)
        {
            EditorGUI.indentLevel++;
            palette.FontColor1 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.primary"), palette.FontColor1);
            palette.FontColor2 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.secondary"), palette.FontColor2);
            palette.FontColor3 = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.tertiary"), palette.FontColor3);
            EditorGUI.indentLevel--;
        }

        showStatus = EditorGUILayout.Foldout(showStatus, BasisEditorLocalization.Get("editor.uiStyle.palette.statusColors"), true);
        if (showStatus)
        {
            EditorGUI.indentLevel++;
            palette.SuccessColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.common.success"), palette.SuccessColor);
            palette.CautionColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.caution"), palette.CautionColor);
            palette.DangerColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.danger"), palette.DangerColor);
            EditorGUI.indentLevel--;
        }

        showOther = EditorGUILayout.Foldout(showOther, BasisEditorLocalization.Get("editor.uiStyle.palette.otherColors"), true);
        if (showOther)
        {
            EditorGUI.indentLevel++;
            palette.WhiteColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.white"), palette.WhiteColor);
            palette.BlackColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.black"), palette.BlackColor);
            palette.ClearColor = EditorGUILayout.ColorField(BasisEditorLocalization.Get("editor.uiStyle.palette.clear"), palette.ClearColor);
            EditorGUI.indentLevel--;
        }
    }

    // ---- Library ----

    private void DrawLibrarySection()
    {
        showImageStyles = EditorGUILayout.Foldout(showImageStyles, BasisEditorLocalization.Get("editor.uiStyle.library.imageStyles", library.ImageStyles.Styles?.Count ?? 0), true);
        if (showImageStyles && library.ImageStyles.Styles != null)
        {
            EditorGUI.indentLevel++;
            foreach (ImageStyle style in library.ImageStyles.Styles)
            {
                DrawImageStyle(style);
            }
            EditorGUI.indentLevel--;
        }

        showLabelStyles = EditorGUILayout.Foldout(showLabelStyles, BasisEditorLocalization.Get("editor.uiStyle.library.labelStyles", library.LabelStyles.Styles?.Count ?? 0), true);
        if (showLabelStyles && library.LabelStyles.Styles != null)
        {
            EditorGUI.indentLevel++;
            foreach (LabelStyle style in library.LabelStyles.Styles)
            {
                DrawLabelStyle(style);
            }
            EditorGUI.indentLevel--;
        }

        showSelectableStyles = EditorGUILayout.Foldout(showSelectableStyles, BasisEditorLocalization.Get("editor.uiStyle.library.selectableStyles", library.SelectableStyles.Styles?.Count ?? 0), true);
        if (showSelectableStyles && library.SelectableStyles.Styles != null)
        {
            EditorGUI.indentLevel++;
            foreach (SelectableStyle style in library.SelectableStyles.Styles)
            {
                DrawSelectableStyle(style);
            }
            EditorGUI.indentLevel--;
        }
    }

    private void DrawImageStyle(ImageStyle style)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(style.Title, EditorStyles.boldLabel);
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.color"), style.Color, out UiStyleColor updated);
            style.Color = updated;
            style.Sprite = (Sprite)EditorGUILayout.ObjectField(BasisEditorLocalization.Get("editor.uiStyle.style.sprite"), style.Sprite, typeof(Sprite), false);
            style.PixelsPerUnitMultiplier = EditorGUILayout.FloatField(BasisEditorLocalization.Get("editor.uiStyle.style.pixelsPerUnit"), style.PixelsPerUnitMultiplier);
        }
    }

    private void DrawLabelStyle(LabelStyle style)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(style.Title, EditorStyles.boldLabel);
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.color"), style.Color, out UiStyleColor updated);
            style.Color = updated;
            style.FontSize = EditorGUILayout.FloatField(BasisEditorLocalization.Get("editor.uiStyle.style.fontSize"), style.FontSize);
        }
    }

    private void DrawSelectableStyle(SelectableStyle style)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(style.Title, EditorStyles.boldLabel);
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.normal"), style.NormalColor, out UiStyleColor normal);
            style.NormalColor = normal;
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.highlighted"), style.HighlightedColor, out UiStyleColor highlighted);
            style.HighlightedColor = highlighted;
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.pressed"), style.PressedColor, out UiStyleColor pressed);
            style.PressedColor = pressed;
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.uiStyle.style.selected"), style.SelectedColor, out UiStyleColor selected);
            style.SelectedColor = selected;
            DrawUiStyleColor(BasisEditorLocalization.Get("editor.common.disabled"), style.DisabledColor, out UiStyleColor disabled);
            style.DisabledColor = disabled;
        }
    }

    private void DrawUiStyleColor(string label, UiStyleColor styleColor, out UiStyleColor result)
    {
        result = styleColor;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(label);

            result.Type = LocalizedEnumPopup(result.Type, ColorTypeLabel, GUILayout.Width(70));

            if (result.Type == UiStyleColorType.Palette)
            {
                result.PaletteStyle = LocalizedEnumPopup(result.PaletteStyle, PaletteStyleLabel);

                // Show preview of the resolved palette color
                Color resolved = palette != null ? palette.GetColor(result.PaletteStyle) : Color.white;
                EditorGUI.DrawRect(GUILayoutUtility.GetRect(20, 18, GUILayout.Width(20)), resolved);
            }
            else
            {
                result.Color = EditorGUILayout.ColorField(result.Color);
            }
        }
    }

    private static T LocalizedEnumPopup<T>(T current, Func<T, string> label, params GUILayoutOption[] options) where T : Enum
    {
        T[] values = (T[])Enum.GetValues(typeof(T));
        string[] names = new string[values.Length];
        int selected = -1;
        for (int i = 0; i < values.Length; i++)
        {
            names[i] = label(values[i]);
            if (values[i].Equals(current)) selected = i;
        }
        int next = EditorGUILayout.Popup(selected, names, options);
        return next >= 0 && next < values.Length ? values[next] : current;
    }

    private static string ColorTypeLabel(UiStyleColorType type)
    {
        switch (type)
        {
            case UiStyleColorType.Palette: return BasisEditorLocalization.Get("editor.uiStyle.colorType.palette");
            case UiStyleColorType.Custom: return BasisEditorLocalization.Get("editor.common.custom");
            default: return ObjectNames.NicifyVariableName(type.ToString());
        }
    }

    private static string PaletteStyleLabel(UiPaletteStyle style)
    {
        switch (style)
        {
            case UiPaletteStyle.BackgroundColor1: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.backgroundColor1");
            case UiPaletteStyle.BackgroundColor2: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.backgroundColor2");
            case UiPaletteStyle.BackgroundColor3: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.backgroundColor3");
            case UiPaletteStyle.LayerColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.layerColor");
            case UiPaletteStyle.AccentColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.accentColor");
            case UiPaletteStyle.FontColor1: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.fontColor1");
            case UiPaletteStyle.FontColor2: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.fontColor2");
            case UiPaletteStyle.FontColor3: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.fontColor3");
            case UiPaletteStyle.InputFieldColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.inputFieldColor");
            case UiPaletteStyle.ButtonColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.buttonColor");
            case UiPaletteStyle.WhiteColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.whiteColor");
            case UiPaletteStyle.ClearColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.clearColor");
            case UiPaletteStyle.BlackColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.blackColor");
            case UiPaletteStyle.SuccessColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.successColor");
            case UiPaletteStyle.CautionColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.cautionColor");
            case UiPaletteStyle.DangerColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.dangerColor");
            case UiPaletteStyle.Scrollbar: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.scrollbar");
            case UiPaletteStyle.EdgeColor: return BasisEditorLocalization.Get("editor.uiStyle.paletteStyle.edgeColor");
            default: return ObjectNames.NicifyVariableName(style.ToString());
        }
    }
}
#endif
