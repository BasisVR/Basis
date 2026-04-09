#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Basis.BasisUI.Styling;
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

    [MenuItem("Basis/Settings/UI Style Editor")]
    public static void Open()
    {
        GetWindow<BasisUIStyleEditorWindow>("UI Style Editor");
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
        if (palette == null || library == null)
        {
            EditorGUILayout.HelpBox("Could not load StylePalette or StyleLibrary asset.", MessageType.Error);
            if (GUILayout.Button("Retry"))
                LoadAssets();
            return;
        }

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        EditorGUILayout.LabelField("Style Palette", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Changes here modify the asset defaults directly. " +
            "Use the in-game UI Style tab for runtime-only overrides.",
            MessageType.Info);

        EditorGUILayout.Space();

        Undo.RecordObject(palette, "UI Style Palette Change");

        DrawPaletteSection();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Style Library", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        Undo.RecordObject(library, "UI Style Library Change");

        DrawLibrarySection();

        EditorGUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply & Refresh All"))
            {
                EditorUtility.SetDirty(palette);
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssets();

                if (Application.isPlaying)
                {
                    UiStyleSettings.UpdateAllStyleComponents();
                }
            }

            if (GUILayout.Button("Revert"))
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
        showBackgrounds = EditorGUILayout.Foldout(showBackgrounds, "Background Colors", true);
        if (showBackgrounds)
        {
            EditorGUI.indentLevel++;
            palette.BackgroundColor1 = EditorGUILayout.ColorField("Background 1", palette.BackgroundColor1);
            palette.BackgroundColor2 = EditorGUILayout.ColorField("Background 2", palette.BackgroundColor2);
            palette.BackgroundColor3 = EditorGUILayout.ColorField("Background 3", palette.BackgroundColor3);
            palette.LayerColor = EditorGUILayout.ColorField("Layer", palette.LayerColor);
            EditorGUI.indentLevel--;
        }

        showUI = EditorGUILayout.Foldout(showUI, "UI Colors", true);
        if (showUI)
        {
            EditorGUI.indentLevel++;
            palette.AccentColor = EditorGUILayout.ColorField("Accent", palette.AccentColor);
            palette.ButtonColor = EditorGUILayout.ColorField("Button", palette.ButtonColor);
            palette.InputFieldColor = EditorGUILayout.ColorField("Input Field", palette.InputFieldColor);
            palette.Scrollbar = EditorGUILayout.ColorField("Scrollbar", palette.Scrollbar);
            EditorGUI.indentLevel--;
        }

        showFonts = EditorGUILayout.Foldout(showFonts, "Font Colors", true);
        if (showFonts)
        {
            EditorGUI.indentLevel++;
            palette.FontColor1 = EditorGUILayout.ColorField("Primary", palette.FontColor1);
            palette.FontColor2 = EditorGUILayout.ColorField("Secondary", palette.FontColor2);
            palette.FontColor3 = EditorGUILayout.ColorField("Tertiary", palette.FontColor3);
            EditorGUI.indentLevel--;
        }

        showStatus = EditorGUILayout.Foldout(showStatus, "Status Colors", true);
        if (showStatus)
        {
            EditorGUI.indentLevel++;
            palette.SuccessColor = EditorGUILayout.ColorField("Success", palette.SuccessColor);
            palette.CautionColor = EditorGUILayout.ColorField("Caution", palette.CautionColor);
            palette.DangerColor = EditorGUILayout.ColorField("Danger", palette.DangerColor);
            EditorGUI.indentLevel--;
        }

        showOther = EditorGUILayout.Foldout(showOther, "Other Colors", true);
        if (showOther)
        {
            EditorGUI.indentLevel++;
            palette.WhiteColor = EditorGUILayout.ColorField("White", palette.WhiteColor);
            palette.BlackColor = EditorGUILayout.ColorField("Black", palette.BlackColor);
            palette.ClearColor = EditorGUILayout.ColorField("Clear", palette.ClearColor);
            EditorGUI.indentLevel--;
        }
    }

    // ---- Library ----

    private void DrawLibrarySection()
    {
        showImageStyles = EditorGUILayout.Foldout(showImageStyles, $"Image Styles ({library.ImageStyles.Styles?.Count ?? 0})", true);
        if (showImageStyles && library.ImageStyles.Styles != null)
        {
            EditorGUI.indentLevel++;
            foreach (ImageStyle style in library.ImageStyles.Styles)
            {
                DrawImageStyle(style);
            }
            EditorGUI.indentLevel--;
        }

        showLabelStyles = EditorGUILayout.Foldout(showLabelStyles, $"Label Styles ({library.LabelStyles.Styles?.Count ?? 0})", true);
        if (showLabelStyles && library.LabelStyles.Styles != null)
        {
            EditorGUI.indentLevel++;
            foreach (LabelStyle style in library.LabelStyles.Styles)
            {
                DrawLabelStyle(style);
            }
            EditorGUI.indentLevel--;
        }

        showSelectableStyles = EditorGUILayout.Foldout(showSelectableStyles, $"Selectable Styles ({library.SelectableStyles.Styles?.Count ?? 0})", true);
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
            DrawUiStyleColor("Color", style.Color, out UiStyleColor updated);
            style.Color = updated;
            style.Sprite = (Sprite)EditorGUILayout.ObjectField("Sprite", style.Sprite, typeof(Sprite), false);
            style.PixelsPerUnitMultiplier = EditorGUILayout.FloatField("Pixels Per Unit", style.PixelsPerUnitMultiplier);
        }
    }

    private void DrawLabelStyle(LabelStyle style)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(style.Title, EditorStyles.boldLabel);
            DrawUiStyleColor("Color", style.Color, out UiStyleColor updated);
            style.Color = updated;
            style.FontSize = EditorGUILayout.FloatField("Font Size", style.FontSize);
        }
    }

    private void DrawSelectableStyle(SelectableStyle style)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(style.Title, EditorStyles.boldLabel);
            DrawUiStyleColor("Normal", style.NormalColor, out UiStyleColor normal);
            style.NormalColor = normal;
            DrawUiStyleColor("Highlighted", style.HighlightedColor, out UiStyleColor highlighted);
            style.HighlightedColor = highlighted;
            DrawUiStyleColor("Pressed", style.PressedColor, out UiStyleColor pressed);
            style.PressedColor = pressed;
            DrawUiStyleColor("Selected", style.SelectedColor, out UiStyleColor selected);
            style.SelectedColor = selected;
            DrawUiStyleColor("Disabled", style.DisabledColor, out UiStyleColor disabled);
            style.DisabledColor = disabled;
        }
    }

    private void DrawUiStyleColor(string label, UiStyleColor styleColor, out UiStyleColor result)
    {
        result = styleColor;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(label);

            result.Type = (UiStyleColorType)EditorGUILayout.EnumPopup(result.Type, GUILayout.Width(70));

            if (result.Type == UiStyleColorType.Palette)
            {
                result.PaletteStyle = (UiPaletteStyle)EditorGUILayout.EnumPopup(result.PaletteStyle);

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
}
#endif
