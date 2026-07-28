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
                    () => BasisValidatorUI.RemoveMissingScripts(Prop.gameObject),
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

    public void CreateErrorPanel(VisualElement rootElement)
    {
        errorPanel = BasisValidatorUI.CreateErrorPanel(rootElement, out errorMessageLabel, out errorButtonContainer);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = BasisValidatorUI.CreatePassedPanel(rootElement, out passedMessageLabel);
    }

    public void CreateSuggestionPanel(VisualElement rootElement)
    {
        suggestionPanel = BasisValidatorUI.CreateSuggestionPanel(rootElement, out suggestionMessageLabel, out suggestionButtonContainer);
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
                BasisValidatorUI.AutoFixButton(suggestionButtonContainer, issue.Fix, actionTitle, false);
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
                BasisValidatorUI.AutoFixButton(errorButtonContainer, issue.Fix, actionTitle);
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

}
