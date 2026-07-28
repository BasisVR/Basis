using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static BasisAvatarValidator;

[CustomEditor(typeof(BasisScene))]
public class BasisSceneSDKInspector : Editor
{
    public VisualTreeAsset visualTree;
    public BasisScene BasisScene;
    public VisualElement rootElement;
    public VisualElement uiElementsRoot;
    private Label resultLabel;
    public BasisSceneValidator BasisSceneValidator;
    public bool SpawnPointGizmoState = false;

    public void OnEnable()
    {
        visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BasisSDKConstants.SceneuxmlPath);
        BasisScene = (BasisScene)target;
    }

    public void OnDisable()
    {
        if (BasisSceneValidator != null)
        {
            BasisSceneValidator.OnDestroy();
        }
    }

    public override VisualElement CreateInspectorGUI()
    {
        BasisScene = (BasisScene)target;
        rootElement = new VisualElement();

        if (visualTree != null)
        {
            uiElementsRoot = visualTree.CloneTree();
            rootElement.Add(uiElementsRoot);

            BasisSceneValidator = new BasisSceneValidator(BasisScene, rootElement);

            // Documentation button
            Button docButton = BasisSDKCommonInspector.DocumentationButton(rootElement, BasisEditorLocalization.Get("sdk.scene.documentation.button"));
            docButton.clicked += delegate
            {
                if (EditorUtility.DisplayDialog(
                        BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation.title"),
                        BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation.body"),
                        BasisEditorLocalization.Get("sdk.common.dialog.yes"),
                        BasisEditorLocalization.Get("sdk.common.dialog.no")))
                {
                    Application.OpenURL(BasisSDKConstants.SceneDocumentationURL);
                }
            };
            rootElement.Add(docButton);

            // Name and description fields
            TextField SceneNameField = uiElementsRoot.Q<TextField>(BasisSDKConstants.SceneName);
            TextField SceneDescriptionField = uiElementsRoot.Q<TextField>(BasisSDKConstants.SceneDescription);

            SceneNameField.BindProperty(serializedObject.FindProperty("BasisBundleDescription.AssetBundleName"));
            SceneDescriptionField.BindProperty(serializedObject.FindProperty("BasisBundleDescription.AssetBundleDescription"));

            // Icon field
            ObjectField SceneIconField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.SceneIcon);
            SceneIconField.objectType = typeof(Texture2D);
            SceneIconField.allowSceneObjects = true;
            SceneIconField.value = BasisScene.BasisBundleDescription.AssetBundleIcon;
            SceneIconField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(OnIconFieldChanged);

            // Spawn point field
            ObjectField SpawnPointField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.SpawnPointField);
            SpawnPointField.allowSceneObjects = true;
            SpawnPointField.BindProperty(serializedObject.FindProperty("SpawnPoint"));

            // Main camera field
            ObjectField MainCameraField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.MainCameraField);
            MainCameraField.allowSceneObjects = true;
            MainCameraField.value = BasisScene.MainCamera;
            MainCameraField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(OnMainCameraChanged);

            // Audio mixer group (hidden)
            ObjectField AudioMixerGroupField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.AudioMixerGroupField);
            AudioMixerGroupField.style.display = DisplayStyle.None;

            // Is ready (read-only display)
            Toggle IsReadyField = uiElementsRoot.Q<Toggle>(BasisSDKConstants.IsReadyField);
            IsReadyField.value = BasisScene.IsReady;

            // Respawn settings
            FloatField RespawnHeightField = uiElementsRoot.Q<FloatField>(BasisSDKConstants.RespawnHeightField);
            FloatField RespawnCheckTimerField = uiElementsRoot.Q<FloatField>(BasisSDKConstants.RespawnCheckTimerField);

            RespawnHeightField.value = BasisScene.RespawnHeight;
            RespawnCheckTimerField.value = BasisScene.RespawnCheckTimer;

            RespawnHeightField.RegisterCallback<ChangeEvent<float>>(OnRespawnHeightChanged);
            RespawnCheckTimerField.RegisterCallback<ChangeEvent<float>>(OnRespawnCheckTimerChanged);

            // Spawn point gizmo button
            Button spawnGizmoButton = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.SpawnPointGizmoButton);
            spawnGizmoButton.text = BasisEditorLocalization.Get("sdk.scene.spawnGizmo.label", BoolToText(SpawnPointGizmoState));
            spawnGizmoButton.clicked += () => ClickedSpawnPointGizmoButton(spawnGizmoButton);

            // Content tags + build options
            BasisSDKCommonInspector.CreateContentTagsFoldout(uiElementsRoot, BasisScene);
            BasisSDKCommonInspector.CreateBuildTargetOptions(uiElementsRoot);
            BasisSDKCommonInspector.CreateBuildOptionsDropdown(uiElementsRoot);

            // Build Button
            Button buildButton = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.BuildButton);

            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            buildButton.clicked += () => Build(assetBundleObject.selectedTargets, BasisScene.BasisBundleDescription.AssetBundleIcon);
        }
        else
        {
            Debug.LogError("VisualTree is null. Make sure the UXML file is assigned correctly.");
        }

            // Surface what the load-time pass will strip or rewrite, while the author can
            // still do something about it — enforcement itself is runtime-only.
            rootElement.Add(BasisContentPolicePreflight.CreateSection(
                BasisScene != null ? BasisScene.gameObject : null,
                BundledContentHolder.Selector.World));
        return rootElement;
    }

    private void OnIconFieldChanged(ChangeEvent<UnityEngine.Object> evt)
    {
        BasisScene.BasisBundleDescription.AssetBundleIcon = evt.newValue as Texture2D;
        EditorUtility.SetDirty(BasisScene);
        BasisDebug.Log($"Setting to {BasisScene.BasisBundleDescription.AssetBundleIcon}");
    }

    private void OnMainCameraChanged(ChangeEvent<UnityEngine.Object> evt)
    {
        Undo.RecordObject(BasisScene, "Change Main Camera");
        BasisScene.MainCamera = evt.newValue as Camera;
        EditorUtility.SetDirty(BasisScene);
    }

    private void OnRespawnHeightChanged(ChangeEvent<float> evt)
    {
        Undo.RecordObject(BasisScene, "Change Respawn Height");
        BasisScene.RespawnHeight = evt.newValue;
        EditorUtility.SetDirty(BasisScene);
    }

    private void OnRespawnCheckTimerChanged(ChangeEvent<float> evt)
    {
        Undo.RecordObject(BasisScene, "Change Respawn Check Timer");
        BasisScene.RespawnCheckTimer = evt.newValue;
        EditorUtility.SetDirty(BasisScene);
    }

    private void ClickedSpawnPointGizmoButton(Button button)
    {
        SpawnPointGizmoState = !SpawnPointGizmoState;
        button.text = BasisEditorLocalization.Get("sdk.scene.spawnGizmo.label", BoolToText(SpawnPointGizmoState));
    }

    private static string BoolToText(bool value)
    {
        return value
            ? BasisEditorLocalization.Get("sdk.scene.bool.on")
            : BasisEditorLocalization.Get("sdk.scene.bool.off");
    }

    private void OnSceneGUI()
    {
        if (!SpawnPointGizmoState || BasisScene == null || BasisScene.SpawnPoint == null)
            return;

        Transform spawnPoint = BasisScene.SpawnPoint;

        // Draw spawn point gizmo
        Handles.color = Color.green;
        Handles.DrawWireCube(spawnPoint.position, new Vector3(0.5f, 1.8f, 0.5f));
        Handles.ArrowHandleCap(0, spawnPoint.position, spawnPoint.rotation, 1f, EventType.Repaint);
        Handles.Label(spawnPoint.position + Vector3.up * 2f, BasisEditorLocalization.Get("sdk.scene.spawnGizmo.handlesLabel"),
            new GUIStyle(GUI.skin.label) { normal = { textColor = Color.green }, fontStyle = FontStyle.Bold });

        // Draw respawn height plane
        Handles.color = new Color(1f, 0.3f, 0.3f, 0.3f);
        Vector3 respawnCenter = new Vector3(spawnPoint.position.x, BasisScene.RespawnHeight, spawnPoint.position.z);
        Handles.DrawWireCube(respawnCenter, new Vector3(10f, 0.01f, 10f));
        Handles.Label(respawnCenter + Vector3.up * 0.5f, BasisEditorLocalization.Get("sdk.scene.respawnHeight.handlesLabel", BasisScene.RespawnHeight),
            new GUIStyle(GUI.skin.label) { normal = { textColor = Color.red }, fontStyle = FontStyle.Bold });
    }

    private async void Build(List<BuildTarget> targets, Texture2D Image)
    {
        if (targets == null || targets.Count == 0)
        {
            Debug.LogError("No build targets selected.");
            return;
        }

        if (BasisSceneValidator.ValidateScene(out List<BasisValidationIssue> errors, out List<string> passes))
        {
            Debug.Log($"Building Scene Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
            if (!BasisValidationHandler.IsSceneValid(BasisScene.gameObject.scene))
            {
                Debug.LogError("Invalid scene. AssetBundle build aborted.");
                return;
            }
            if (Image == null)
            {
                Image = AssetPreview.GetAssetPreview(BasisScene);
            }
            string ImageBytes = null;
            if (Image != null)
            {
                ImageBytes = BasisTextureCompression.ToPngBytes(Image);
            }
            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            (bool success, string message) = await BasisBundleBuild.SceneBundleBuild(ImageBytes, BasisScene, targets, assetBundleObject.UseCustomPassword, assetBundleObject.UserSelectedPassword);
            EditorUtility.ClearProgressBar();
            ClearResultLabel();

            resultLabel = new Label
            {
                style = { fontSize = 14 }
            };

            if (success)
            {
                resultLabel.text = BasisEditorLocalization.Get("sdk.common.build.success");
                resultLabel.style.backgroundColor = Color.green;
                resultLabel.style.color = Color.black;
            }
            else
            {
                resultLabel.text = BasisEditorLocalization.Get("sdk.common.build.failed", message);
                resultLabel.style.backgroundColor = Color.red;
                resultLabel.style.color = Color.black;
            }

            uiElementsRoot.Add(resultLabel);
        }
        else
        {
            if (!EditorUtility.DisplayDialog(
                    BasisEditorLocalization.Get("sdk.scene.buildError.title"),
                    BasisEditorLocalization.Get("sdk.scene.buildError.body", string.Join("\n", errors.ConvertAll(e => e.Message))),
                    BasisEditorLocalization.Get("sdk.common.dialog.ok"),
                    BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation")))
            {
                Application.OpenURL(BasisSDKConstants.SceneDocumentationURL);
            }
        }
    }

    private void ClearResultLabel()
    {
        if (resultLabel != null)
        {
            uiElementsRoot.Remove(resultLabel);
            resultLabel = null;
        }
    }

}
