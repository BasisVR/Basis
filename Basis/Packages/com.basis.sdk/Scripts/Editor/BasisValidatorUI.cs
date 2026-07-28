using Basis.Editor.Localization;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class BasisValidatorUI
{
    public static VisualElement CreateErrorPanel(VisualElement rootElement, out Label messageLabel, out VisualElement buttonContainer)
    {
        VisualElement errorPanel = new VisualElement();
        errorPanel.style.backgroundColor = new StyleColor(new Color(1, 0.5f, 0.5f, 0.5f));
        errorPanel.style.paddingTop = 5;
        errorPanel.style.flexGrow = 1;
        errorPanel.style.paddingBottom = 5;
        errorPanel.style.marginBottom = 10;
        errorPanel.style.borderTopLeftRadius = 5;
        errorPanel.style.borderTopRightRadius = 5;
        errorPanel.style.borderBottomLeftRadius = 5;
        errorPanel.style.borderBottomRightRadius = 5;
        errorPanel.style.borderLeftWidth = 2;
        errorPanel.style.borderRightWidth = 2;
        errorPanel.style.borderTopWidth = 2;
        errorPanel.style.borderBottomWidth = 2;
        errorPanel.style.borderBottomColor = new StyleColor(Color.red);

        messageLabel = new Label(BasisEditorLocalization.Get("sdk.validator.error.empty"));
        messageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        messageLabel.style.whiteSpace = WhiteSpace.Normal;
        errorPanel.Add(messageLabel);

        buttonContainer = new VisualElement() { name = "ErrorButtonContainer" };
        errorPanel.Add(buttonContainer);

        errorPanel.style.display = DisplayStyle.None;
        rootElement.Add(errorPanel);
        return errorPanel;
    }

    public static VisualElement CreatePassedPanel(VisualElement rootElement, out Label messageLabel)
    {
        VisualElement passedPanel = new VisualElement();
        passedPanel.style.backgroundColor = new StyleColor(new Color(0.5f, 1f, 0.5f, 0.5f));
        passedPanel.style.paddingTop = 5;
        passedPanel.style.flexGrow = 1;
        passedPanel.style.paddingBottom = 5;
        passedPanel.style.marginBottom = 10;
        passedPanel.style.borderTopLeftRadius = 5;
        passedPanel.style.borderTopRightRadius = 5;
        passedPanel.style.borderBottomLeftRadius = 5;
        passedPanel.style.borderBottomRightRadius = 5;
        passedPanel.style.borderLeftWidth = 2;
        passedPanel.style.borderRightWidth = 2;
        passedPanel.style.borderTopWidth = 2;
        passedPanel.style.borderBottomWidth = 2;
        passedPanel.style.borderBottomColor = new StyleColor(Color.green);

        messageLabel = new Label(BasisEditorLocalization.Get("sdk.validator.passed.empty"));
        messageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        passedPanel.Add(messageLabel);

        passedPanel.style.display = DisplayStyle.None;
        rootElement.Add(passedPanel);
        return passedPanel;
    }

    public static VisualElement CreateSuggestionPanel(VisualElement rootElement, out Label messageLabel, out VisualElement buttonContainer)
    {
        VisualElement suggestionPanel = new VisualElement();
        suggestionPanel.style.backgroundColor = new StyleColor(new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
        suggestionPanel.style.paddingTop = 5;
        suggestionPanel.style.flexGrow = 1;
        suggestionPanel.style.paddingBottom = 5;
        suggestionPanel.style.marginBottom = 10;
        suggestionPanel.style.borderTopLeftRadius = 5;
        suggestionPanel.style.borderTopRightRadius = 5;
        suggestionPanel.style.borderBottomLeftRadius = 5;
        suggestionPanel.style.borderBottomRightRadius = 5;
        suggestionPanel.style.borderLeftWidth = 2;
        suggestionPanel.style.borderRightWidth = 2;
        suggestionPanel.style.borderTopWidth = 2;
        suggestionPanel.style.borderBottomWidth = 2;
        suggestionPanel.style.borderBottomColor = new StyleColor(Color.yellow);

        Label header = new Label(BasisEditorLocalization.Get("sdk.validator.suggestions.header"));
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.color = new StyleColor(Color.white);
        suggestionPanel.Add(header);

        messageLabel = new Label();
        messageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        messageLabel.style.whiteSpace = WhiteSpace.Normal;
        suggestionPanel.Add(messageLabel);

        buttonContainer = new VisualElement() { name = "SuggestionButtonContainer" };
        suggestionPanel.Add(buttonContainer);

        suggestionPanel.style.display = DisplayStyle.None;
        rootElement.Add(suggestionPanel);
        return suggestionPanel;
    }

    public static void AutoFixButton(VisualElement rootElement, Action onClickAction, string fixMe, bool isError = true)
    {
        foreach (var child in rootElement.Children())
        {
            if (child is Button existing && existing.text == fixMe)
                return;
        }

        Button fixMeButton = new Button();
        fixMeButton.clicked += delegate
        {
            onClickAction?.Invoke();
            fixMeButton.RemoveFromHierarchy();
        };
        fixMeButton.text = fixMe;

        Color errBackground = new Color(0.96f, 0.26f, 0.21f);
        Color errHover = new Color(0.9f, 0.2f, 0.2f);
        Color warnBackground = new Color(1f, 0.63f, 0f);
        Color warnHover = new Color(1f, 0.7f, 0f);

        Color background = isError ? errBackground : warnBackground;
        Color hover = isError ? errHover : warnHover;

        fixMeButton.style.backgroundColor = new StyleColor(background);
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;
        fixMeButton.style.whiteSpace = WhiteSpace.Normal;
        fixMeButton.style.flexShrink = 0;
        fixMeButton.style.paddingTop = 6;
        fixMeButton.style.paddingBottom = 6;
        fixMeButton.style.paddingLeft = 12;
        fixMeButton.style.paddingRight = 12;
        fixMeButton.style.marginBottom = 10;
        fixMeButton.style.borderTopLeftRadius = 8;
        fixMeButton.style.borderTopRightRadius = 8;
        fixMeButton.style.borderBottomLeftRadius = 8;
        fixMeButton.style.borderBottomRightRadius = 8;
        fixMeButton.style.borderLeftWidth = 0;
        fixMeButton.style.borderRightWidth = 0;
        fixMeButton.style.borderTopWidth = 0;
        fixMeButton.style.borderBottomWidth = 3;
        fixMeButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        fixMeButton.style.alignSelf = Align.Auto;

        fixMeButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(hover);
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(background);
        });

        rootElement.Add(fixMeButton);
    }

    public static void RemoveMissingScripts(GameObject root)
    {
        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            if (count > 0)
            {
                EditorUtility.SetDirty(child.gameObject);
            }
        }
    }
}
