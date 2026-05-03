using Basis.Editor.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static BasisAvatarValidator;

public class BasisPropValidator
{
    private readonly BasisProp Prop;
    private VisualElement errorPanel;
    private Label errorMessageLabel;
    private VisualElement errorButtonContainer;
    private VisualElement suggestionPanel;
    private Label suggestionMessageLabel;
    private VisualElement suggestionButtonContainer;
    private VisualElement passedPanel;
    private Label passedMessageLabel;
    private string _lastErrorSignature = "";
    private string _lastSuggestionSignature = "";
    public VisualElement Root;

    public BasisPropValidator(BasisProp prop, VisualElement root)
    {
        Prop = prop;
        Root = root;
        CreateErrorPanel(root);
        CreateSuggestionPanel(root);
        CreatePassedPanel(root);
        EditorApplication.update += UpdateValidation;
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation;
    }

    private void UpdateValidation()
    {
        if (ValidateProp(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> suggestions, out List<string> passes))
        {
            HideErrorPanel();
            ShowPassedPanel(passes);
        }
        else
        {
            ShowErrorPanel(errors);
            if (passes.Count > 0)
                ShowPassedPanel(passes);
            else
                HidePassedPanel();
        }

        if (suggestions.Count > 0)
            ShowSuggestionPanel(suggestions);
        else
            HideSuggestionPanel();
    }

    public bool ValidateProp(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> suggestions, out List<string> passes)
    {
        errors = new List<BasisValidationIssue>();
        suggestions = new List<BasisValidationIssue>();
        passes = new List<string>();

        if (Prop == null)
        {
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.propValidator.propMissing"), ValidationCategory.Configuration, null));
            return false;
        }
        passes.Add(BasisEditorLocalization.Get("sdk.propValidator.propAssigned"));

        // Check bundle name
        if (string.IsNullOrEmpty(Prop.BasisBundleDescription.AssetBundleName))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.propValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.propValidator.bundleName.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.propValidator.bundleName.set"));
        }

        // Check bundle description
        if (string.IsNullOrEmpty(Prop.BasisBundleDescription.AssetBundleDescription))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.set"));
        }

        // Check for missing scripts
        Transform[] children = Prop.gameObject.GetComponentsInChildren<Transform>(true);
        bool hasMissingScripts = false;
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
            if (count > 0)
            {
                hasMissingScripts = true;
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.propValidator.missingScripts", child.gameObject.name),
                    ValidationCategory.MissingReference,
                    () => RemoveMissingScripts(Prop.gameObject),
                    BasisEditorLocalization.Get("sdk.propValidator.missingScripts.fix")
                ));
            }
        }
        if (!hasMissingScripts)
        {
            passes.Add(BasisEditorLocalization.Get("sdk.propValidator.missingScripts.passed"));
        }

        // Check colliders are on the Interactable layer
        int interactableLayer = LayerMask.NameToLayer("Interactable");
        Collider[] colliders = Prop.GetComponentsInChildren<Collider>(true);
        if (colliders.Length == 0)
        {
            passes.Add(BasisEditorLocalization.Get("sdk.propValidator.colliders.none"));
        }
        else
        {
            List<Collider> wrongLayerColliders = new List<Collider>();
            foreach (Collider col in colliders)
            {
                if (col.gameObject.layer != interactableLayer)
                {
                    wrongLayerColliders.Add(col);
                }
            }
            if (wrongLayerColliders.Count > 0)
            {
                string names = string.Join(", ", wrongLayerColliders.ConvertAll(c => c.gameObject.name));
                suggestions.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.propValidator.colliders.wrongLayer", names),
                    ValidationCategory.Configuration,
                    () => FixCollidersToInteractableLayer(Prop, interactableLayer),
                    BasisEditorLocalization.Get("sdk.propValidator.colliders.wrongLayer.fix")
                ));
            }
            else
            {
                passes.Add(BasisEditorLocalization.Get("sdk.propValidator.colliders.passed"));
            }
        }

        // Check custom password
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        if (assetBundleObject != null)
        {
            if (assetBundleObject.UseCustomPassword && string.IsNullOrEmpty(assetBundleObject.UserSelectedPassword))
            {
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.propValidator.password.empty"),
                    ValidationCategory.Security, null
                ));
            }
        }

        return errors.Count == 0;
    }

    private void FixSetDefaultBundleName()
    {
        if (Prop == null) return;
        string name = Prop.gameObject.name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        Prop.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Prop);
    }

    private void FixSetDefaultDescription()
    {
        if (Prop == null) return;
        Prop.BasisBundleDescription.AssetBundleDescription =
            $"Prop \"{Prop.gameObject.name}\"";
        EditorUtility.SetDirty(Prop);
    }

    private static void FixCollidersToInteractableLayer(BasisProp prop, int interactableLayer)
    {
        if (prop == null) return;
        Collider[] colliders = prop.GetComponentsInChildren<Collider>(true);
        foreach (Collider col in colliders)
        {
            if (col.gameObject.layer != interactableLayer)
            {
                Undo.RecordObject(col.gameObject, "Set collider to Interactable layer");
                col.gameObject.layer = interactableLayer;
                EditorUtility.SetDirty(col.gameObject);
            }
        }
    }

    private static void RemoveMissingScripts(GameObject root)
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

    public void CreateErrorPanel(VisualElement rootElement)
    {
        errorPanel = new VisualElement();
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

        errorMessageLabel = new Label(BasisEditorLocalization.Get("sdk.validator.error.empty"));
        errorMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        errorMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        errorPanel.Add(errorMessageLabel);

        errorButtonContainer = new VisualElement() { name = "ErrorButtonContainer" };
        errorPanel.Add(errorButtonContainer);

        errorPanel.style.display = DisplayStyle.None;
        rootElement.Add(errorPanel);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = new VisualElement();
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

        passedMessageLabel = new Label(BasisEditorLocalization.Get("sdk.validator.passed.empty"));
        passedMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        passedPanel.Add(passedMessageLabel);

        passedPanel.style.display = DisplayStyle.None;
        rootElement.Add(passedPanel);
    }

    public void CreateSuggestionPanel(VisualElement rootElement)
    {
        suggestionPanel = new VisualElement();
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

        suggestionMessageLabel = new Label();
        suggestionMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        suggestionMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        suggestionPanel.Add(suggestionMessageLabel);

        suggestionButtonContainer = new VisualElement() { name = "SuggestionButtonContainer" };
        suggestionPanel.Add(suggestionButtonContainer);

        suggestionPanel.style.display = DisplayStyle.None;
        rootElement.Add(suggestionPanel);
    }

    private void ShowSuggestionPanel(List<BasisValidationIssue> suggestions)
    {
        string currentSignature = string.Join("|", suggestions.ConvertAll(s => $"{s.Category}:{s.Message}"));
        if (currentSignature == _lastSuggestionSignature)
        {
            suggestionPanel.style.display = DisplayStyle.Flex;
            return;
        }
        _lastSuggestionSignature = currentSignature;

        List<string> issueList = new List<string>();
        suggestionButtonContainer.Clear();

        for (int i = 0; i < suggestions.Count; i++)
        {
            var issue = suggestions[i];
            if (issue.Fix != null)
            {
                string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? issue.Message : issue.FixLabel;
                SuggestionFixButton(suggestionButtonContainer, issue.Fix, actionTitle);
            }
            if (!issueList.Contains(issue.Message))
                issueList.Add($"- {issue.Message}");
        }

        suggestionMessageLabel.text = string.Join("\n", issueList.ToArray());
        suggestionPanel.style.display = DisplayStyle.Flex;
    }

    private void HideSuggestionPanel()
    {
        suggestionPanel.style.display = DisplayStyle.None;
        _lastSuggestionSignature = "";
    }

    private void ShowErrorPanel(List<BasisValidationIssue> errors)
    {
        string currentSignature = string.Join("|", errors.ConvertAll(e => $"{e.Category}:{e.Message}"));
        if (currentSignature == _lastErrorSignature)
        {
            errorPanel.style.display = DisplayStyle.Flex;
            return;
        }
        _lastErrorSignature = currentSignature;

        List<string> issueList = new List<string>();
        errorButtonContainer.Clear();

        for (int i = 0; i < errors.Count; i++)
        {
            var issue = errors[i];
            if (issue.Fix != null)
            {
                string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? issue.Message : issue.FixLabel;
                AutoFixButton(errorButtonContainer, issue.Fix, actionTitle);
            }
            if (!issueList.Contains(issue.Message))
                issueList.Add(issue.Message);
        }

        errorMessageLabel.text = string.Join("\n", issueList.ToArray());
        errorPanel.style.display = DisplayStyle.Flex;
    }

    private void HideErrorPanel()
    {
        errorPanel.style.display = DisplayStyle.None;
        _lastErrorSignature = "";
    }

    private void ShowPassedPanel(List<string> passes)
    {
        passedMessageLabel.text = string.Join("\n", passes);
        passedPanel.style.display = DisplayStyle.Flex;
    }

    private void HidePassedPanel()
    {
        passedPanel.style.display = DisplayStyle.None;
    }

    private void AutoFixButton(VisualElement rootElement, Action onClickAction, string fixMe)
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

        Color background = new Color(0.96f, 0.26f, 0.21f);
        Color hover = new Color(0.9f, 0.2f, 0.2f);

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

    private void SuggestionFixButton(VisualElement rootElement, Action onClickAction, string fixMe)
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

        Color background = new Color(1f, 0.63f, 0f);
        Color hover = new Color(1f, 0.7f, 0f);

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
}
