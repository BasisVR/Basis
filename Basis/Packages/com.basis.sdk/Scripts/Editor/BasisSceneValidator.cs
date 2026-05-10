using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static BasisAvatarValidator;

public class BasisSceneValidator
{
    private readonly BasisScene Scene;
    private VisualElement errorPanel;
    private Label errorMessageLabel;
    private VisualElement errorButtonContainer;
    private VisualElement passedPanel;
    private Label passedMessageLabel;
    private string _lastErrorSignature = "";
    public VisualElement Root;

    public BasisSceneValidator(BasisScene scene, VisualElement root)
    {
        Scene = scene;
        Root = root;
        CreateErrorPanel(root);
        CreatePassedPanel(root);
        EditorApplication.update += UpdateValidation;
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation;
    }

    private void UpdateValidation()
    {
        if (ValidateScene(out List<BasisValidationIssue> errors, out List<string> passes))
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
    }

    public bool ValidateScene(out List<BasisValidationIssue> errors, out List<string> passes)
    {
        errors = new List<BasisValidationIssue>();
        passes = new List<string>();

        if (Scene == null)
        {
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.sceneValidator.sceneMissing"), ValidationCategory.Configuration, null));
            return false;
        }
        passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.sceneAssigned"));

        // Check bundle name
        if (string.IsNullOrEmpty(Scene.BasisBundleDescription.AssetBundleName))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.bundleName.set"));
        }

        // Check bundle description
        if (string.IsNullOrEmpty(Scene.BasisBundleDescription.AssetBundleDescription))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.bundleDescription.set"));
        }

        // Check spawn point
        if (Scene.SpawnPoint == null)
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.notAssigned"), ValidationCategory.MissingReference,
                FixAssignSpawnPoint,
                BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.spawnPoint.assigned"));
        }

        // Check respawn height is reasonable
        if (Scene.RespawnHeight > 0)
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.positive", Scene.RespawnHeight),
                ValidationCategory.Configuration,
                FixResetRespawnHeight,
                BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.respawnHeight.reasonable"));
        }

        // Check scene is saved
        if (string.IsNullOrEmpty(Scene.gameObject.scene.path))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.sceneValidator.scene.unsaved"),
                ValidationCategory.Configuration, null
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.scene.saved"));
        }

        // Check for missing scripts
        Transform[] children = Scene.gameObject.GetComponentsInChildren<Transform>(true);
        bool hasMissingScripts = false;
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
            if (count > 0)
            {
                hasMissingScripts = true;
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts", child.gameObject.name),
                    ValidationCategory.MissingReference,
                    () => RemoveMissingScripts(Scene.gameObject),
                    BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts.fix")
                ));
            }
        }
        if (!hasMissingScripts)
        {
            passes.Add(BasisEditorLocalization.Get("sdk.sceneValidator.missingScripts.passed"));
        }

        // Check custom password
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        if (assetBundleObject != null)
        {
            if (assetBundleObject.UseCustomPassword && string.IsNullOrEmpty(assetBundleObject.UserSelectedPassword))
            {
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.sceneValidator.password.empty"),
                    ValidationCategory.Security, null
                ));
            }
        }

        return errors.Count == 0;
    }

    private void FixSetDefaultBundleName()
    {
        if (Scene == null) return;
        Undo.RecordObject(Scene, "Set Default Bundle Name");
        string name = Scene.gameObject.name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        Scene.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Scene);
    }

    private void FixSetDefaultDescription()
    {
        if (Scene == null) return;
        Undo.RecordObject(Scene, "Set Default Description");
        Scene.BasisBundleDescription.AssetBundleDescription =
            $"Scene \"{Scene.gameObject.name}\"";
        EditorUtility.SetDirty(Scene);
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
}
