using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Everything the SDK checks about a world before it can be uploaded. Scheduling — when a pass runs
/// and why it no longer runs every editor frame — lives in <see cref="BasisValidationRunner"/>.
/// </summary>
public class BasisSceneValidator : BasisValidationRunner
{
    private readonly BasisScene Scene;
    private VisualElement errorPanel;
    private Label errorMessageLabel;
    private VisualElement errorButtonContainer;
    private VisualElement passedPanel;
    private Label passedMessageLabel;

    private readonly BasisValidationHierarchyScan _scan = new BasisValidationHierarchyScan();

    public BasisSceneValidator(BasisScene scene, VisualElement root)
    {
        Scene = scene;
        CreateErrorPanel(root);
        CreatePassedPanel(root);

        BeginValidation(root,
            ValidateConfiguration,
            ValidateSceneSetup,
            ValidateHierarchy);
    }

    protected override void RefreshScan()
    {
        _scan.Rebuild(Scene != null ? Scene.transform : null);
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
    }

    /// <summary>
    /// Runs the complete suite — every group — and returns fresh lists. This is the upload path: a
    /// build gets a full pass taken on the spot, never whatever the panels happen to be showing.
    /// </summary>
    public bool ValidateScene(out List<BasisValidationIssue> errors, out List<string> passes)
    {
        BasisValidationBucket results = RunAllGroups();
        errors = new List<BasisValidationIssue>(results.Errors);
        passes = new List<string>(results.Passes);
        return errors.Count == 0;
    }

    private void ValidateConfiguration(BasisValidationBucket bucket)
    {
        if (Scene == null)
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.sceneValidator.sceneMissing"), ValidationCategory.Configuration);
            return;
        }
        bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.sceneAssigned"));

        if (string.IsNullOrEmpty(Scene.BasisBundleDescription.AssetBundleName))
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.set"));
        }

        if (string.IsNullOrEmpty(Scene.BasisBundleDescription.AssetBundleDescription))
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.set"));
        }

        if (Scene.UseCustomPassword && string.IsNullOrEmpty(Scene.UserSelectedPassword))
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.sceneValidator.password.empty"), ValidationCategory.Security);
        }
    }

    private void ValidateSceneSetup(BasisValidationBucket bucket)
    {
        if (Scene == null) return;

        if (Scene.SpawnPoint == null)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.notAssigned"), ValidationCategory.MissingReference,
                FixAssignSpawnPoint,
                BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.assigned"));
        }

        if (Scene.RespawnHeight > 0)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.positive", Scene.RespawnHeight),
                ValidationCategory.Configuration,
                FixResetRespawnHeight,
                BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.reasonable"));
        }

        if (string.IsNullOrEmpty(Scene.gameObject.scene.path))
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.sceneValidator.scene.unsaved"), ValidationCategory.Configuration);
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.scene.saved"));
        }
    }

    private void ValidateHierarchy(BasisValidationBucket bucket)
    {
        if (Scene == null) return;

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
                BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts", child.gameObject.name),
                ValidationCategory.MissingReference,
                () => BasisValidatorUI.RemoveMissingScripts(Scene.gameObject),
                BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts.fix"));
        }

        if (!hasMissingScripts)
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts.passed"));
        }
    }

    private void FixSetDefaultBundleName()
    {
        if (Scene == null) return;
        Undo.RecordObject(Scene, "Set Default Bundle Name");
        string name = BasisContentDefaults.ResolveName(Scene.gameObject, BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.default"));
        Scene.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Scene);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.SceneName, name);
    }

    private void FixSetDefaultDescription()
    {
        if (Scene == null) return;
        Undo.RecordObject(Scene, "Set Default Description");
        string name = string.IsNullOrEmpty(Scene.BasisBundleDescription.AssetBundleName)
            ? BasisContentDefaults.ResolveName(Scene.gameObject, BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.default"))
            : Scene.BasisBundleDescription.AssetBundleName;
        string description = BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.default", name);
        Scene.BasisBundleDescription.AssetBundleDescription = description;
        EditorUtility.SetDirty(Scene);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.SceneDescription, description);
    }

    private void FixAssignSpawnPoint()
    {
        if (Scene == null) return;
        Undo.RecordObject(Scene, "Assign Spawn Point");
        Scene.SpawnPoint = Scene.transform;
        EditorUtility.SetDirty(Scene);
    }

    private void FixResetRespawnHeight()
    {
        if (Scene == null) return;
        Scene.RespawnHeight = -100;
        EditorUtility.SetDirty(Scene);
    }

    public void CreateErrorPanel(VisualElement rootElement)
    {
        errorPanel = BasisValidatorUI.CreateErrorPanel(rootElement, out errorMessageLabel, out errorButtonContainer);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = BasisValidatorUI.CreatePassedPanel(rootElement, out passedMessageLabel);
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
