using System;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers.Editor;
using Basis.Scripts.Editor;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static BasisAvatarValidator;
using Basis.Scripts.BasisSdk.Players;

[CustomEditor(typeof(BasisAvatar))]
public partial class BasisAvatarSDKInspector : Editor
{
    private const string PendingTestInEditorAvatarIdSessionKey = "BasisAvatarSDKInspector.PendingTestInEditorAvatarId";

    public delegate void BeforeTestInEditorHandler(GameObject clone);
    public delegate void BeforeTestInEditorOriginalDeactivationHandler(
        GameObject original,
        TestInEditorOriginalDeactivationContext context);

    public sealed class TestInEditorOriginalDeactivationContext
    {
        public int SettleFrames { get; private set; }

        public void RequestSettleFrames(int frames)
        {
            SettleFrames = Math.Max(SettleFrames, Math.Max(0, frames));
        }
    }
    /// <summary>
    /// Legacy Test in Editor preparation stage. The clone is active when this runs, matching the
    /// historical contract for consumers that expect initialized Animator/renderer state.
    /// </summary>
    public static BeforeTestInEditorHandler OnBeforeTestInEditor;
    /// <summary>
    /// Structural Test in Editor preparation stage. The clone is intentionally inactive here so
    /// authoring-only behaviours cannot execute before final build-time conversion has completed.
    /// Processors registered here must support inactive hierarchies.
    /// </summary>
    public static BeforeTestInEditorHandler OnBeforeTestInEditorPrepareInactive;
    /// <summary>
    /// Final inactive Test in Editor preparation stage. Runs after structural clone processors and
    /// immediately before the clone is activated. Build-time component replacement belongs here.
    /// </summary>
    public static BeforeTestInEditorHandler OnBeforeTestInEditorFinalize;
    /// <summary>
    /// Runs while the authored Test in Editor object is still active, immediately before Basis
    /// deactivates it. Handlers may inspect feature-specific runtime components and return the number
    /// of inactive frames they require before Basis clones the object. The largest request wins.
    /// </summary>
    public static event BeforeTestInEditorOriginalDeactivationHandler OnBeforeTestInEditorOriginalDeactivation;
    private static BasisAvatar ScheduledTestInEditorAvatar;

    public static event Action<BasisAvatarSDKInspector> InspectorGuiCreated;
    public static event Action ButtonClicked;
    public static event Action ValueChanged;
    public VisualTreeAsset visualTree;
    public BasisAvatar Avatar;
    public VisualElement uiElementsRoot;
    public bool AvatarEyePositionState = false;
    public bool AvatarMouthPositionState = false;
    public VisualElement rootElement;
    public AvatarSDKVisemes AvatarSDKVisemes = new AvatarSDKVisemes();
    public Button EventCallbackAvatarBundleButton { get; private set; }
    private Label resultLabel; // Store the result label for later clearing
    public string Error;
    public BasisAvatarValidator BasisAvatarValidator;

    [InitializeOnLoadMethod]
    private static void InitializeTestInEditorHooks()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !HasPendingTestInEditorAvatarId())
        {
            return;
        }

        EditorApplication.delayCall -= TryExecutePendingTestInEditor;
        EditorApplication.delayCall += TryExecutePendingTestInEditor;
    }

    private static void TryExecutePendingTestInEditor()
    {
        string pendingAvatarId = GetPendingTestInEditorAvatarId();
        if (string.IsNullOrEmpty(pendingAvatarId))
        {
            return;
        }

        if (!GlobalObjectId.TryParse(pendingAvatarId, out GlobalObjectId avatarId))
        {
            ClearPendingTestInEditorAvatarId();
            return;
        }

        BasisAvatar avatar = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(avatarId) as BasisAvatar;
        ClearPendingTestInEditorAvatarId();
        if (avatar == null)
        {
            BasisDebug.LogError("Unable to resolve the pending avatar for Test In Editor.", BasisDebug.LogTag.Editor);
            return;
        }

        RequestAvatarLoad(avatar);
    }

    private static bool HasPendingTestInEditorAvatarId()
    {
        return SessionState.GetBool(PendingTestInEditorAvatarIdSessionKey + ".Exists", false);
    }

    private static string GetPendingTestInEditorAvatarId()
    {
        return SessionState.GetString(PendingTestInEditorAvatarIdSessionKey, string.Empty);
    }

    private static void SetPendingTestInEditorAvatarId(string avatarId)
    {
        SessionState.SetString(PendingTestInEditorAvatarIdSessionKey, avatarId ?? string.Empty);
        SessionState.SetBool(PendingTestInEditorAvatarIdSessionKey + ".Exists", !string.IsNullOrEmpty(avatarId));
    }

    private static void ClearPendingTestInEditorAvatarId()
    {
        SessionState.EraseString(PendingTestInEditorAvatarIdSessionKey);
        SessionState.SetBool(PendingTestInEditorAvatarIdSessionKey + ".Exists", false);
    }
    private void OnEnable()
    {
        visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BasisSDKConstants.AvataruxmlPath);
        Avatar = target as BasisAvatar;
    }
    public void OnDisable()
    {
        if (BasisAvatarValidator != null)
        {
            BasisAvatarValidator.OnDestroy();
        }
    }

    public override VisualElement CreateInspectorGUI()
    {
        Avatar = target as BasisAvatar;
        rootElement = new VisualElement();
        if (Avatar == null)
        {
            return rootElement;
        }
        if (visualTree != null)
        {
            uiElementsRoot = visualTree.CloneTree();
            rootElement.Add(uiElementsRoot);
            BasisAvatarValidator = new BasisAvatarValidator(Avatar, rootElement);
            Button button = BasisSDKCommonInspector.DocumentationButton(rootElement, BasisEditorLocalization.Get("sdk.avatar.documentation.button"));
            button.clicked += delegate
            {
                if (EditorUtility.DisplayDialog(
                        BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation.title"),
                        BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation.body"),
                        BasisEditorLocalization.Get("sdk.common.dialog.yes"),
                        BasisEditorLocalization.Get("sdk.common.dialog.no")))
                {
                    Application.OpenURL(BasisSDKConstants.AvatarDocumentationURL);
                }
            };
            rootElement.Add(button);
            BasisAutomaticSetupAvatarEditor.TryToAutomatic(this);
            SetupItems();
            AvatarSDKVisemes.Initialize(this);
            SetupNetworkBehaviours();
#if !BASIS_FRAMEWORK_EXISTS
            rootElement.Add(Basis.Scripts.BasisSdk.Players.Editor.BasisEditorPreviewParametersFoldout.Create());
#endif
            // Surface what the load-time pass will strip or rewrite, while the author can
            // still do something about it — enforcement itself is runtime-only.
            rootElement.Add(BasisContentPolicePreflight.CreateSection(
                Avatar != null ? Avatar.gameObject : null,
                BundledContentHolder.Selector.Avatar));
            InspectorGuiCreated?.Invoke(this);
        }
        else
        {
            Debug.LogError("VisualTree is null. Make sure the UXML file is assigned correctly.");
        }
        return rootElement;
    }
    public void AutomaticallyFindVisemes()
    {
        SkinnedMeshRenderer Renderer = Avatar.FaceVisemeMesh;
        Undo.RecordObject(Avatar, "Automatically Find Visemes");
        Avatar.FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        List<string> Names = AvatarHelper.FindAllNames(Renderer);
        foreach (KeyValuePair<string, int> Value in AvatarHelper.SearchForVisemeIndex)
        {
            if (AvatarHelper.GetBlendShapes(Names, Value.Key, out int OnMeshIndex))
            {
                Avatar.FaceVisemeMovement[Value.Value] = OnMeshIndex;
            }
        }
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
        AvatarSDKVisemes.Initialize(this);
    }
    public void AutomaticallyFindBlinking()
    {
        SkinnedMeshRenderer Renderer = Avatar.FaceBlinkMesh;
        Undo.RecordObject(Avatar, "Automatically Find Blinking");
        Avatar.BlinkViseme = new int[] { };
        List<string> Names = AvatarHelper.FindAllNames(Renderer);
        int[] Ints = new int[] { -1 };
        foreach (string Name in AvatarHelper.SearchForBlinkIndex)
        {
            if (AvatarHelper.GetBlendShapes(Names, Name, out int BlendShapeIndex))
            {
                Ints[0] = BlendShapeIndex;
                break;
            }
        }
        Avatar.BlinkViseme = Ints;
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
        AvatarSDKVisemes.Initialize(this);
    }
    public void ClickedAvatarEyePositionButton(Button Button)
    {
        Undo.RecordObject(Avatar, "Toggle Eye Position Gizmo");
        AvatarEyePositionState = !AvatarEyePositionState;
        Button.text = BasisEditorLocalization.Get("sdk.avatar.eyeGizmo.label", AvatarHelper.BoolToText(AvatarEyePositionState));
        EditorUtility.SetDirty(Avatar);
        ButtonClicked?.Invoke();
    }
    public void ClickedAvatarMouthPositionButton(Button Button)
    {
        Undo.RecordObject(Avatar, "Toggle Mouth Position Gizmo");
        AvatarMouthPositionState = !AvatarMouthPositionState;
        Button.text = BasisEditorLocalization.Get("sdk.avatar.mouthGizmo.label", AvatarHelper.BoolToText(AvatarMouthPositionState));
        EditorUtility.SetDirty(Avatar);
        ButtonClicked?.Invoke();
    }
    private void OnMouthHeightValueChanged(ChangeEvent<Vector2> evt)
    {
        Undo.RecordObject(Avatar, "Change Mouth Position");
        Avatar.AvatarMouthPosition = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        ValueChanged?.Invoke();
    }
    private void OnEyeHeightValueChanged(ChangeEvent<Vector2> evt)
    {
        Undo.RecordObject(Avatar, "Change Eye Position");
        Avatar.AvatarEyePosition = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        ValueChanged?.Invoke();
    }
    private void OnEyeLivelinessChanged(ChangeEvent<float> evt)
    {
        Undo.RecordObject(Avatar, "Change Eye Liveliness");
        Avatar.EyeLiveliness = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        ValueChanged?.Invoke();
        if (Application.isPlaying)
        {
            BasisLocalEyeDriverData.Liveliness = evt.newValue;
            BasisLocalEyeDriverData.PersonalityDirty = true;
        }
    }
    private void OnEyeAttentivenessChanged(ChangeEvent<float> evt)
    {
        Undo.RecordObject(Avatar, "Change Eye Attentiveness");
        Avatar.EyeAttentiveness = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        ValueChanged?.Invoke();
        if (Application.isPlaying)
        {
            BasisLocalEyeDriverData.Attentiveness = evt.newValue;
            BasisLocalEyeDriverData.PersonalityDirty = true;
        }
    }
    public void EventCallbackAnimator(ChangeEvent<UnityEngine.Object> evt, ref Animator Renderer)
    {
        //  Debug.Log(nameof(EventCallbackAnimator));
        Undo.RecordObject(Avatar, "Change Animator");
        Renderer = (Animator)evt.newValue;
        // Check if the Avatar is part of a prefab
        if (PrefabUtility.IsPartOfPrefabInstance(Avatar))
        {
            // Record the prefab modification
            PrefabUtility.RecordPrefabInstancePropertyModifications(Avatar);
        }
        EditorUtility.SetDirty(Avatar);
    }
    public void EventCallbackFaceVisemeMesh(ChangeEvent<UnityEngine.Object> evt, ref SkinnedMeshRenderer Renderer)
    {
        // Debug.Log(nameof(EventCallbackFaceVisemeMesh));
        Undo.RecordObject(Avatar, "Change Face Viseme Mesh");
        Renderer = (SkinnedMeshRenderer)evt.newValue;

        // Check if the Avatar is part of a prefab
        if (PrefabUtility.IsPartOfPrefabInstance(Avatar))
        {
            // Record the prefab modification
            PrefabUtility.RecordPrefabInstancePropertyModifications(Avatar);
        }
        EditorUtility.SetDirty(Avatar);
    }
    private void OnSceneGUI()
    {
        Avatar = target as BasisAvatar;
        if (Avatar != null)
        {
            BasisAvatarGizmoEditor.UpdateGizmos(this, Avatar);
        }
    }
    public void SetupItems()
    {
        // Initialize Buttons
        Button avatarEyePositionClick = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.avatarEyePositionButton);
        Button avatarMouthPositionClick = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.avatarMouthPositionButton);
        Button avatarBundleButton = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.AvatarBundleButton);
        Button avatarAutomaticVisemeDetectionClick = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.AvatarAutomaticVisemeDetection);
        Button avatarAutomaticBlinkDetectionClick = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.AvatarAutomaticBlinkDetection);
        Button AvatarTestInEditorClick = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.AvatarTestInEditor);

        // Initialize Event Callbacks for Vector2 fields (for Avatar Eye and Mouth Position)
        BasisHelpersGizmo.CallBackVector2Field(uiElementsRoot, BasisSDKConstants.avatarEyePositionField, Avatar.AvatarEyePosition, OnEyeHeightValueChanged);
        BasisHelpersGizmo.CallBackVector2Field(uiElementsRoot, BasisSDKConstants.avatarMouthPositionField, Avatar.AvatarMouthPosition, OnMouthHeightValueChanged);

        string heightLabel = BasisEditorLocalization.Get("sdk.avatar.position.height");
        string forwardLabel = BasisEditorLocalization.Get("sdk.avatar.position.forward");
        BasisHelpersGizmo.LabelVector2Field(uiElementsRoot, BasisSDKConstants.avatarEyePositionField, BasisEditorLocalization.Get("sdk.avatar.eyePosition.label"), heightLabel, forwardLabel);
        BasisHelpersGizmo.LabelVector2Field(uiElementsRoot, BasisSDKConstants.avatarMouthPositionField, BasisEditorLocalization.Get("sdk.avatar.mouthPosition.label"), heightLabel, forwardLabel);

        // Eye Personality sliders
        Slider livelinessSlider = uiElementsRoot.Q<Slider>(BasisSDKConstants.EyeLivelinessField);
        if (livelinessSlider != null)
        {
            livelinessSlider.value = Avatar.EyeLiveliness;
            livelinessSlider.RegisterCallback<ChangeEvent<float>>(OnEyeLivelinessChanged);
        }
        Slider attentivenessSlider = uiElementsRoot.Q<Slider>(BasisSDKConstants.EyeAttentivenessField);
        if (attentivenessSlider != null)
        {
            attentivenessSlider.value = Avatar.EyeAttentiveness;
            attentivenessSlider.RegisterCallback<ChangeEvent<float>>(OnEyeAttentivenessChanged);
        }

        // Initialize ObjectFields and assign references
        ObjectField animatorField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.animatorField);
        ObjectField faceBlinkMeshField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.FaceBlinkMeshField);
        ObjectField faceVisemeMeshField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.FaceVisemeMeshField);

        TextField AvatarNameField = uiElementsRoot.Q<TextField>(BasisSDKConstants.AvatarName);
        TextField AvatarDescriptionField = uiElementsRoot.Q<TextField>(BasisSDKConstants.AvatarDescription);

        TextField AvatarPasswordField = uiElementsRoot.Q<TextField>(BasisSDKConstants.Avatarpassword);

        ObjectField AvatarIconField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.AvatarIcon);

        Toggle AvatarDoNotAutoRenameBonesField = uiElementsRoot.Q<Toggle>(BasisSDKConstants.AvatarDoNotAutoRenameBonesField);
        Toggle AvatarAutomaticallyRemoveBlendshapesField = uiElementsRoot.Q<Toggle>(BasisSDKConstants.AvatarAutomaticallyRemoveBlendshapesField);

        AvatarIconField.objectType = typeof(Texture2D);

        animatorField.allowSceneObjects = true;
        faceBlinkMeshField.allowSceneObjects = true;
        faceVisemeMeshField.allowSceneObjects = true;
        AvatarIconField.allowSceneObjects = true;

        //  AvatarIconField.value = null;
        animatorField.value = Avatar.Animator;
        faceBlinkMeshField.value = Avatar.FaceBlinkMesh;
        faceVisemeMeshField.value = Avatar.FaceVisemeMesh;

        AvatarNameField.value = Avatar.BasisBundleDescription.AssetBundleName;
        AvatarDescriptionField.value = Avatar.BasisBundleDescription.AssetBundleDescription;

        AvatarNameField.RegisterCallback<ChangeEvent<string>>(AvatarName);
        AvatarDescriptionField.RegisterCallback<ChangeEvent<string>>(AvatarDescription);

        AvatarDoNotAutoRenameBonesField.value = Avatar.ProcessingAvatarOptions != null ? Avatar.ProcessingAvatarOptions.doNotAutoRenameBones : false;
        AvatarDoNotAutoRenameBonesField.RegisterCallback<ChangeEvent<bool>>(OnAvatarDoNotAutoRenameBonesField);

      //  AvatarAutomaticallyRemoveBlendshapesField.value = Avatar.ProcessingAvatarOptions != null ? Avatar.ProcessingAvatarOptions.RemoveUnusedBlendshapes : false;
       // AvatarAutomaticallyRemoveBlendshapesField.RegisterCallback<ChangeEvent<bool>>(OnAvatarRemoveUnusedBlendshapesField);
        // Button click events
        avatarEyePositionClick.clicked += () => ClickedAvatarEyePositionButton(avatarEyePositionClick);
        avatarMouthPositionClick.clicked += () => ClickedAvatarMouthPositionButton(avatarMouthPositionClick);
        avatarAutomaticVisemeDetectionClick.clicked += AutomaticallyFindVisemes;
        avatarAutomaticBlinkDetectionClick.clicked += AutomaticallyFindBlinking;
        AvatarTestInEditorClick.clicked += AvatarTestInEditorClickFunction;// unity editor window button

        // Parented into the UXML blocks so they pick up that styling rather than trailing off the
        // end of the inspector: what the avatar is goes under Settings, how it is published goes in
        // the build blocks above the build button, each its own collapsible section.
        BasisSDKCommonInspector.CreateBuildTargetOptions(BasisSDKCommonInspector.ResolveBuildTargetsContainer(uiElementsRoot));
        BasisSDKCommonInspector.CreateBuildOptionsDropdown(BasisSDKCommonInspector.ResolveBuildContainer(uiElementsRoot));

        BasisSDKCommonInspector.CreateContentTagsFoldout(BasisSDKCommonInspector.ResolveContentTagsContainer(uiElementsRoot), Avatar);
        BasisSDKCommonInspector.CreateContentGroupIdFoldout(BasisSDKCommonInspector.ResolveContentTagsContainer(uiElementsRoot), Avatar);
        BasisSDKCommonInspector.StyleBuildButton(avatarBundleButton);
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        // Icon field, plus the capture buttons that fill it in. Bound to the serialized property
        // rather than written by hand so both routes land on the undo stack.
        BasisSDKCommonInspector.CreateIconTools(
            AvatarIconField,
            serializedObject,
            Avatar,
            BasisEditorLocalization.Get("sdk.avatar.icon.generate"),
            BasisEditorLocalization.Get("sdk.avatar.icon.generate.tooltip"),
            () => BasisIconCapture.CaptureGameObject(Avatar.gameObject, BasisIconCapture.AvatarYaw, BasisIconCapture.AvatarPitch));
        avatarBundleButton.clicked += () => EventCallbackAvatarBundle(assetBundleObject.selectedTargets, Avatar.BasisBundleDescription.AssetBundleIcon);


        // Register Animator field change event
        animatorField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackAnimator(evt, ref Avatar.Animator));

        // Register Blink and Viseme Mesh field change events
        faceBlinkMeshField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackFaceVisemeMesh(evt, ref Avatar.FaceBlinkMesh));
        faceVisemeMeshField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackFaceVisemeMesh(evt, ref Avatar.FaceVisemeMesh));

        // Update Button Text
        avatarEyePositionClick.text = BasisEditorLocalization.Get("sdk.avatar.eyeGizmo.label", AvatarHelper.BoolToText(AvatarEyePositionState));
        avatarMouthPositionClick.text = BasisEditorLocalization.Get("sdk.avatar.mouthGizmo.label", AvatarHelper.BoolToText(AvatarMouthPositionState));
    }

    private async void EventCallbackAvatarBundle(List<BuildTarget> targets, Texture2D Image)
    {
        if (targets == null || targets.Count == 0)
        {
            BasisDebug.LogError("No build targets selected.");
            return;
        }
        if (BasisAvatarValidator.ValidateAvatar(out List<BasisValidationIssue> Errors, out List<BasisValidationIssue> Warnings, out List<string> Passes))
        {
            if (Avatar.Animator.runtimeAnimatorController != null)
            {
                string path = AssetDatabase.GetAssetPath(Avatar.Animator.runtimeAnimatorController);
                if (path == BasisSDKConstants.AvatarAnimatorControllerPath)
                {
                    BasisDebug.Log("Animator Controller Used was the default! Unassigning");
                    Avatar.Animator.runtimeAnimatorController = null;
                    EditorUtility.SetDirty(Avatar.Animator);
                    AssetDatabase.SaveAssetIfDirty(Avatar);
                }
            }
            //here
            if (Image == null)
            {
                Image = AssetPreview.GetAssetPreview(Avatar.gameObject);
            }
            string ImageBytes = null;
            if (Image != null)
            {
                ImageBytes = BasisTextureCompression.ToPngBytes(Image);
            }
            (bool success, string message) BundleCreatedState = new(false, "");
            GameObject buildRoot = null;
            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            try
            {
                BasisDebug.Log($"Building Avatar Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
                // Build from a stripped clone so the authored avatar stays untouched. The group id
                // must be ensured on the ORIGINAL first — generating it on the clone would discard
                // it when the clone is destroyed, breaking version stacking across uploads.
                BasisContentGroupId.EnsurePersistent(Avatar);
                buildRoot = GameObject.Instantiate(Avatar.gameObject);
                buildRoot.TryGetComponent<BasisAvatar>(out Avatar);
                //  if (Avatar.ProcessingAvatarOptions != null && Avatar.ProcessingAvatarOptions.RemoveUnusedBlendshapes)
                //  {
                // If your pipeline needs editor-only stripping, do it here.
                //  BasisBuildBlendshapeStripper.StripForBuild(settings: assetBundleObject, buildRoot, Avatar);

                //}
#if UNITY_6000_2_OR_NEWER
                GenerateMeshLODs(3);
#endif

                BasisDebug.Log($"Building Avatar Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
                BundleCreatedState = await BasisBundleBuild.GameObjectBundleBuild(ImageBytes, Avatar, targets, assetBundleObject.UseCustomPassword, assetBundleObject.UserSelectedPassword);

                EditorUtility.ClearProgressBar();
                // Clear any previous result label
                ClearResultLabel();
            }
            finally
            {
                if (buildRoot != null)
                {
                    BasisDebug.Log("Cleaning Up Duplicated Avatar", BasisDebug.LogTag.Core);
                    GameObject.DestroyImmediate(buildRoot);
                }
            }

            // Display new result in the UI
            resultLabel = new Label
            {
                style = { fontSize = 14 }
            };
            resultLabel.style.color = Color.black; // Error message color
            if (BundleCreatedState.success)
            {
                resultLabel.text = BasisEditorLocalization.Get("sdk.common.build.success");
                resultLabel.style.backgroundColor = Color.green;
            }
            else
            {
                resultLabel.text = BasisEditorLocalization.Get("sdk.common.build.failed", BundleCreatedState.message);
                resultLabel.style.backgroundColor = Color.red;
            }

            // Add the result label to the UI
            uiElementsRoot.Add(resultLabel);
            //  BuildReportViewerWindow.ShowWindow();
        }
        else
        {
            if (!EditorUtility.DisplayDialog(
                    BasisEditorLocalization.Get("sdk.avatar.buildError.title"),
                    BasisEditorLocalization.Get("sdk.avatar.buildError.body", string.Join("\n", Errors)),
                    BasisEditorLocalization.Get("sdk.common.dialog.ok"),
                    BasisEditorLocalization.Get("sdk.common.dialog.openDocumentation")))
            {
                Application.OpenURL(BasisSDKConstants.AvatarDocumentationURL);
            }
        }
    }
#if UNITY_6000_2_OR_NEWER && UNITY_EDITOR
    public void GenerateMeshLODs(int lodLimit = -1)
    {
        var smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (smrs == null || smrs.Length == 0)
        {
            BasisDebug.LogWarning("GenerateMeshLODs: No SkinnedMeshRenderer found under root.");
            return;
        }

        var modelPathsNeedingReimport = new HashSet<string>();
        var meshAssetsNeedingSave = new HashSet<Mesh>();

        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;

            var mesh = smr.sharedMesh;
            string meshPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(meshPath)) continue;

            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;

            if (importer != null)
            {
                bool changed = false;

                if (!importer.generateMeshLods)
                {
                    importer.generateMeshLods = true;
                    changed = true;
                }

                if (lodLimit >= 0 && importer.maximumMeshLod != lodLimit)
                {
                    importer.maximumMeshLod = lodLimit;
                    changed = true;
                }

                if (changed)
                {
                    modelPathsNeedingReimport.Add(meshPath);
                }
            }
            else
            {
                MeshLodUtility.GenerateMeshLods(mesh, lodLimit);

                EditorUtility.SetDirty(mesh);
                meshAssetsNeedingSave.Add(mesh);
            }
            smr.meshLodSelectionBias = 0f;
            smr.forceMeshLod = -1;
        }

        // Reimport model files (FBX/OBJ)
        if (modelPathsNeedingReimport.Count > 0)
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                int i = 0, total = modelPathsNeedingReimport.Count;

                foreach (var path in modelPathsNeedingReimport)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            BasisEditorLocalization.Get("sdk.avatar.lod.reimport.title"),
                            BasisEditorLocalization.Get("sdk.avatar.lod.reimport.progress", i + 1, total, path),
                            (float)i / total))
                    {
                        Debug.LogWarning("GenerateMeshLODs: Canceled by user.");
                        break;
                    }

                    var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi != null) mi.SaveAndReimport();
                    i++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
            }
        }

        // Save modified .asset meshes
        if (meshAssetsNeedingSave.Count > 0)
            AssetDatabase.SaveAssets();

        Debug.Log($"GenerateMeshLODs: Reimported {modelPathsNeedingReimport.Count} model asset(s); updated {meshAssetsNeedingSave.Count} mesh asset(s).");
    }
#endif
    public void AvatarTestInEditorClickFunction()
    {
#if BASIS_FRAMEWORK_EXISTS
        if (!Application.isPlaying)
        {
            bool result = EditorUtility.DisplayDialog(
                BasisEditorLocalization.Get("sdk.common.dialog.confirm"),
                BasisEditorLocalization.Get("sdk.avatar.testInEditor.confirm.body"),
                BasisEditorLocalization.Get("sdk.common.dialog.yes"),
                BasisEditorLocalization.Get("sdk.common.dialog.no"));
            if (result)
            {
                SetPendingTestInEditorAvatarId(GlobalObjectId.GetGlobalObjectIdSlow(Avatar).ToString());
                EditorApplication.EnterPlaymode();
            }
        }
        else
        {
            RequestAvatarLoad();
        }
#else
        // SDK preview runs in either edit or play mode — no play-mode prompt needed.
        RequestAvatarLoad();
#endif
    }
    public void RequestAvatarLoad()
    {
        RequestAvatarLoad(Avatar);
    }

    private static void RequestAvatarLoad(BasisAvatar avatar)
    {
        if (avatar == null)
        {
            BasisDebug.LogError("Unable to Test In Editor because the avatar reference is no longer valid.", BasisDebug.LogTag.Editor);
            return;
        }

        if (BasisLocalPlayerData.PlayerReady)
        {
            BasisDebug.Log("Player Ready Loading", BasisDebug.LogTag.Editor);
            LoadAvatar(avatar);
        }
        else
        {
            ScheduledTestInEditorAvatar = avatar;
            BasisDebug.Log("Scheduling Load Avatar", BasisDebug.LogTag.Editor);
            BasisLocalPlayerData.OnLocalPlayerInitialized -= LoadScheduledAvatar;
            BasisLocalPlayerData.OnLocalPlayerInitialized += LoadScheduledAvatar;
        }
    }

    private static void LoadScheduledAvatar()
    {
        BasisLocalPlayerData.OnLocalPlayerInitialized -= LoadScheduledAvatar;
        if (ScheduledTestInEditorAvatar == null)
        {
            return;
        }

        BasisAvatar avatar = ScheduledTestInEditorAvatar;
        ScheduledTestInEditorAvatar = null;
        LoadAvatar(avatar);
    }

    private static GameObject InstantiateInactiveClone(GameObject originalObject)
    {
        GameObject stagingRoot = new GameObject("Basis Test In Editor Clone Staging")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        stagingRoot.SetActive(false);

        if (!EditorUtility.IsPersistent(originalObject) && originalObject.scene.IsValid() && originalObject.scene.isLoaded)
        {
            SceneManager.MoveGameObjectToScene(stagingRoot, originalObject.scene);
        }

        try
        {
            GameObject clone = GameObject.Instantiate(originalObject, stagingRoot.transform, true);
            // The inactive parent prevents Awake/OnEnable from running even when the source is an
            // active persistent prefab asset. Make activeSelf false before removing that parent.
            clone.SetActive(false);
            clone.transform.SetParent(null, true);
            return clone;
        }
        finally
        {
            if (Application.isPlaying)
            {
                GameObject.Destroy(stagingRoot);
            }
            else
            {
                GameObject.DestroyImmediate(stagingRoot);
            }
        }
    }

    private static void ProcessTestInEditorClone(GameObject inSceneItem)
    {
        BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(inSceneItem);
        OnBeforeTestInEditorPrepareInactive?.Invoke(inSceneItem);
        OnBeforeTestInEditorFinalize?.Invoke(inSceneItem);

        // Finalization has removed/replaced authoring-only runtime scripts. Activate before the
        // legacy hook and PostProcessAvatar so consumers see the same initialized hierarchy they
        // historically received and active-only bone traversal remains valid.
        inSceneItem.SetActive(true);
        OnBeforeTestInEditor?.Invoke(inSceneItem);
        BasisAssetBundlePipeline.PostProcessAvatar(inSceneItem);
    }

    private static int GetTestInEditorOriginalSettleFrames(GameObject originalObject)
    {
        var context = new TestInEditorOriginalDeactivationContext();
        OnBeforeTestInEditorOriginalDeactivation?.Invoke(originalObject, context);
        return context.SettleFrames;
    }

    private static async void LoadAvatar(BasisAvatar avatar)
    {
        if (avatar == null || avatar.gameObject == null)
        {
            BasisDebug.LogError("Unable to Test In Editor because the avatar reference is no longer valid.", BasisDebug.LogTag.Editor);
            return;
        }

        BasisDebug.Log("LoadAvatar Called", BasisDebug.LogTag.Editor);

        GameObject originalObject = avatar.gameObject;
        bool originalWasActive = originalObject.activeSelf;
        bool disabledOriginal = false;
        bool cloneHandedToPlayer = false;
        GameObject inSceneItem = null;

        try
        {
            // In play mode the authored scene instance and the Test in Editor clone must never run
            // together. Persistent prefab assets are not scene instances and must not be modified.
            bool canDisableOriginal = Application.isPlaying
                && !EditorUtility.IsPersistent(originalObject)
                && originalObject.scene.IsValid()
                && originalObject.scene.isLoaded;

            int settleFrames = 0;
            if (canDisableOriginal && originalWasActive)
            {
                settleFrames = GetTestInEditorOriginalSettleFrames(originalObject);
                originalObject.SetActive(false);
                disabledOriginal = true;
            }

            for (int frame = 0; disabledOriginal && frame < settleFrames; frame++)
            {
                await Awaitable.NextFrameAsync();
            }

            inSceneItem = InstantiateInactiveClone(originalObject);

            ProcessTestInEditorClone(inSceneItem);

            BasisLoadableBundle LoadableBundle = new BasisLoadableBundle
            {
                LoadableGameobject = new BasisLoadableGameobject() { InSceneItem = inSceneItem }
            };
            LoadableBundle.LoadableGameobject.InSceneItem.transform.parent = null;
            LoadableBundle.BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = BasisGenerateUniqueID.GenerateUniqueID()
            };
            BasisDebug.Log("Requesting Avatar Load", BasisDebug.LogTag.Editor);
            IBasisLocalPlayer localPlayer = BasisLocalPlayerData.Instance;
            if (localPlayer == null)
            {
                throw new InvalidOperationException("The local player disappeared before Test In Editor could load the prepared avatar.");
            }

            // From this point the local-player avatar path owns the clone. Do not destroy it from the
            // editor catch path if loading fails after ownership has been accepted.
            cloneHandedToPlayer = true;
            await localPlayer.CreateAvatarFromMode(BasisLoadMode.ByGameobjectReference, LoadableBundle);
            BasisDebug.Log("Avatar Load Complete", BasisDebug.LogTag.Editor);

            // The in-scene object is now the local player's avatar. Keep the authored scene instance
            // disabled for the rest of play mode so there is never a second copy executing beside it.
            disabledOriginal = false;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Test In Editor failed while preparing the avatar clone: {ex}", BasisDebug.LogTag.Editor);
            if (inSceneItem != null && !cloneHandedToPlayer)
            {
                if (Application.isPlaying)
                {
                    GameObject.Destroy(inSceneItem);
                }
                else
                {
                    GameObject.DestroyImmediate(inSceneItem);
                }
            }
        }
        finally
        {
            // Only restore if preparation failed before ownership of the clone transferred to the
            // local-player avatar path. Successful play-mode tests intentionally leave it disabled.
            if (disabledOriginal && originalObject != null)
            {
                originalObject.SetActive(originalWasActive);
            }
        }
    }
    private void ClearResultLabel()
    {
        if (resultLabel != null)
        {
            uiElementsRoot.Remove(resultLabel);  // Remove the label from the UI
            resultLabel = null; // Optionally reset the reference to null
        }
    }
    public void AvatarDescription(ChangeEvent<string> evt)
    {
        Avatar.BasisBundleDescription.AssetBundleDescription = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
    }
    public void AvatarName(ChangeEvent<string> evt)
    {
        Avatar.BasisBundleDescription.AssetBundleName = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
    }
    public void OnAvatarDoNotAutoRenameBonesField(ChangeEvent<bool> evt)
    {
        if (Avatar.ProcessingAvatarOptions == null) Avatar.ProcessingAvatarOptions = new BasisProcessingAvatarOptions();

        Avatar.ProcessingAvatarOptions.doNotAutoRenameBones = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
    }
    /*
    public void OnAvatarRemoveUnusedBlendshapesField(ChangeEvent<bool> evt)
    {
        if (Avatar.ProcessingAvatarOptions == null) Avatar.ProcessingAvatarOptions = new BasisProcessingAvatarOptions();

        Avatar.ProcessingAvatarOptions.RemoveUnusedBlendshapes = evt.newValue;
        EditorUtility.SetDirty(Avatar);
        AssetDatabase.Refresh();
    }
    */
}
