using Basis.Editor.Localization;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Everything the SDK checks about a prop before it can be uploaded. Scheduling — when a pass runs
/// and why it no longer runs every editor frame — lives in <see cref="BasisValidationRunner"/>.
///
/// <para>Props report their layer problems as suggestions rather than errors, so the bucket's
/// warnings feed the suggestion panel.</para>
/// </summary>
public class BasisPropValidator : BasisValidationRunner
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

    private readonly BasisValidationHierarchyScan _scan = new BasisValidationHierarchyScan();
    private readonly List<Collider> _colliderScratch = new List<Collider>(4);
    private readonly List<string> _wrongLayerNames = new List<string>();

    public BasisPropValidator(BasisProp prop, VisualElement root)
    {
        Prop = prop;
        CreateErrorPanel(root);
        CreateSuggestionPanel(root);
        CreatePassedPanel(root);

        BeginValidation(root,
            ValidateConfiguration,
            ValidateHierarchy,
            ValidateColliders);
    }

    protected override void RefreshScan()
    {
        _scan.Rebuild(Prop != null ? Prop.transform : null);
    }

    protected override void Refresh(BasisValidationBucket results)
    {
        if (results.Errors.Count == 0)
        {
            HideErrorPanel();
            ShowPassedPanel(results.Passes);
        }
        else
        {
            ShowErrorPanel(results.Errors);
            if (results.Passes.Count > 0)
                ShowPassedPanel(results.Passes);
            else
                HidePassedPanel();
        }

        if (results.Warnings.Count > 0)
            ShowSuggestionPanel(results.Warnings);
        else
            HideSuggestionPanel();
    }

    /// <summary>
    /// Runs the complete suite — every group — and returns fresh lists. This is the upload path: a
    /// build gets a full pass taken on the spot, never whatever the panels happen to be showing.
    /// </summary>
    public bool ValidateProp(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> suggestions, out List<string> passes)
    {
        BasisValidationBucket results = RunAllGroups();
        errors = new List<BasisValidationIssue>(results.Errors);
        suggestions = new List<BasisValidationIssue>(results.Warnings);
        passes = new List<string>(results.Passes);
        return errors.Count == 0;
    }

    private void ValidateConfiguration(BasisValidationBucket bucket)
    {
        if (Prop == null)
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.propValidator.propMissing"), ValidationCategory.Configuration);
            return;
        }
        bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.propAssigned"));

        if (string.IsNullOrEmpty(Prop.BasisBundleDescription.AssetBundleName))
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.propValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.propValidator.bundleName.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.bundleName.set"));
        }

        if (string.IsNullOrEmpty(Prop.BasisBundleDescription.AssetBundleDescription))
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.set"));
        }

        if (Prop.UseCustomPassword && string.IsNullOrEmpty(Prop.UserSelectedPassword))
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.propValidator.password.empty"), ValidationCategory.Security);
        }
    }

    private void ValidateHierarchy(BasisValidationBucket bucket)
    {
        if (Prop == null) return;

        bool hasMissingScripts = false;
        List<Transform> all = _scan.All;
        int transformCount = all.Count;
        for (int Index = 0; Index < transformCount; Index++)
        {
            Transform child = all[Index];
            if (child == null) continue;
            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) <= 0) continue;

            hasMissingScripts = true;
            bucket.Error(
                BasisEditorLocalization.Get("sdk.propValidator.missingScripts", child.gameObject.name),
                ValidationCategory.MissingReference,
                () => BasisValidatorUI.RemoveMissingScripts(Prop.gameObject),
                BasisEditorLocalization.Get("sdk.propValidator.missingScripts.fix"));
        }

        if (!hasMissingScripts)
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.missingScripts.passed"));
        }
    }

    private void ValidateColliders(BasisValidationBucket bucket)
    {
        if (Prop == null) return;

        int interactableLayer = LayerMask.NameToLayer("Interactable");
        _wrongLayerNames.Clear();
        bool anyColliders = false;

        List<Transform> all = _scan.All;
        int transformCount = all.Count;
        for (int Index = 0; Index < transformCount; Index++)
        {
            Transform transform = all[Index];
            if (transform == null) continue;

            transform.GetComponents(_colliderScratch);
            int colliderCount = _colliderScratch.Count;
            for (int ColliderIndex = 0; ColliderIndex < colliderCount; ColliderIndex++)
            {
                anyColliders = true;
                GameObject owner = _colliderScratch[ColliderIndex].gameObject;
                if (owner.layer != interactableLayer)
                {
                    _wrongLayerNames.Add(owner.name);
                }
            }
        }

        if (!anyColliders)
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.colliders.none"));
            return;
        }

        if (_wrongLayerNames.Count > 0)
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.propValidator.colliders.wrongLayer", string.Join(", ", _wrongLayerNames)),
                ValidationCategory.Configuration,
                () => FixCollidersToInteractableLayer(Prop, interactableLayer),
                BasisEditorLocalization.Get("sdk.propValidator.colliders.wrongLayer.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.propValidator.colliders.passed"));
        }
    }

    private void FixSetDefaultBundleName()
    {
        if (Prop == null) return;
        Undo.RecordObject(Prop, "Set Default Bundle Name");
        string name = BasisContentDefaults.ResolveName(Prop.gameObject, BasisEditorLocalization.Get("sdk.propValidator.bundleName.default"));
        Prop.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Prop);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.PropName, name);
    }

    private void FixSetDefaultDescription()
    {
        if (Prop == null) return;
        Undo.RecordObject(Prop, "Set Default Description");
        string name = string.IsNullOrEmpty(Prop.BasisBundleDescription.AssetBundleName)
            ? BasisContentDefaults.ResolveName(Prop.gameObject, BasisEditorLocalization.Get("sdk.propValidator.bundleName.default"))
            : Prop.BasisBundleDescription.AssetBundleName;
        string description = BasisEditorLocalization.Get("sdk.propValidator.bundleDescription.default", name);
        Prop.BasisBundleDescription.AssetBundleDescription = description;
        EditorUtility.SetDirty(Prop);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.PropDescription, description);
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
            string line = $"- {issue.Message}";
            if (!issueList.Contains(line))
                issueList.Add(line);
        }

        suggestionMessageLabel.text = string.Join("\n", issueList.ToArray());
        suggestionPanel.style.display = DisplayStyle.Flex;
    }

    private void HideSuggestionPanel()
    {
        suggestionPanel.style.display = DisplayStyle.None;
    }

    private void ShowErrorPanel(List<BasisValidationIssue> errors)
    {
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
