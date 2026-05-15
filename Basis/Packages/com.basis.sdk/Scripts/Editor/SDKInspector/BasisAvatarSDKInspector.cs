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
    public static BeforeTestInEditorHandler OnBeforeTestInEditor;
    private static BasisAvatar ScheduledTestInEditorAvatar;

    public static event Action<BasisAvatarSDKInspector> InspectorGuiCreated;
    public static event Action ButtonClicked;
    public static event Action ValueChanged;
    public static Func<VisualElement> PreviewFoldoutFactory;
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

        // Re-attach the sceneLoaded hook here so we're 
        // listening before the play-mode scene finishes loading.
        if (HasPendingTestInEditorAvatarId())
        {
            HookSceneLoadedForPendingAvatar();
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (!HasPendingTestInEditorAvatarId())
        {
            return;
        }

        if (state == PlayModeStateChange.ExitingEditMode)
        {
            HookSceneLoadedForPendingAvatar();
            return;
        }

        // If sceneLoaded fired earlier the pending key is already cleared and this is a no-op.
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            SceneManager.sceneLoaded -= OnSceneLoadedForPendingAvatar;
            TryExecutePendingTestInEditor();
        }
    }

    private static void HookSceneLoadedForPendingAvatar()
    {
        SceneManager.sceneLoaded -= OnSceneLoadedForPendingAvatar;
        SceneManager.sceneLoaded += OnSceneLoadedForPendingAvatar;
    }

    private static void OnSceneLoadedForPendingAvatar(Scene scene, LoadSceneMode mode)
    {
        SceneManager.sceneLoaded -= OnSceneLoadedForPendingAvatar;
        TryExecutePendingTestInEditor();
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
        Avatar = (BasisAvatar)target;
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
        Avatar = (BasisAvatar)target;
        rootElement = new VisualElement();
        if (visualTree != null)
        {
            uiElementsRoot = visualTree.CloneTree();
            rootElement.Add(uiElementsRoot);
            BasisAvatarValidator = new BasisAvatarValidator(Avatar, rootElement);
            Button button = DocumentationButton(rootElement, BasisEditorLocalization.Get("sdk.avatar.documentation.button"));
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
            var previewFoldout = PreviewFoldoutFactory?.Invoke();
            if (previewFoldout != null) rootElement.Add(previewFoldout);
            InspectorGuiCreated?.Invoke(this);
        }
        else
        {
            Debug.LogError("VisualTree is null. Make sure the UXML file is assigned correctly.");
        }
        return rootElement;
    }
    public Button DocumentationButton(VisualElement rootElement, string Text)
    {
        // Create the button
        Button fixMeButton = new Button();

        fixMeButton.text = Text; // Icon + Text

        Color backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        // Modern slick style

        fixMeButton.style.backgroundColor = new StyleColor(backgroundColor); // Material Red 500
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;

        // Padding and margin
        fixMeButton.style.paddingTop = 6;
        fixMeButton.style.paddingBottom = 6;
        fixMeButton.style.paddingLeft = 12;
        fixMeButton.style.paddingRight = 12;
        fixMeButton.style.marginBottom = 10;

        // Rounded corners
        fixMeButton.style.borderTopLeftRadius = 8;
        fixMeButton.style.borderTopRightRadius = 8;
        fixMeButton.style.borderBottomLeftRadius = 8;
        fixMeButton.style.borderBottomRightRadius = 8;

        // Border and shadow
        fixMeButton.style.borderLeftWidth = 0;
        fixMeButton.style.borderRightWidth = 0;
        fixMeButton.style.borderTopWidth = 0;
        fixMeButton.style.borderBottomWidth = 3;

        // Shadow-like effect via unityBackgroundImageTintColor or using USS later
        fixMeButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        fixMeButton.style.alignSelf = Align.Auto;

        // Hover effect via C# events (UI Toolkit lacks hover pseudoclass in C# directly)
        fixMeButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f, 1f));
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(backgroundColor);
        });

        // Add to root and store
        rootElement.Add(fixMeButton);
        return fixMeButton;
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
        Undo.RecordObject(Avatar, "Change Mouth Height");
        Avatar.AvatarMouthPosition = new Vector3(evt.newValue.x, evt.newValue.y, 0);
        EditorUtility.SetDirty(Avatar);
        ValueChanged?.Invoke();
    }
    private void OnEyeHeightValueChanged(ChangeEvent<Vector2> evt)
    {
        Undo.RecordObject(Avatar, "Change Eye Height");
        Avatar.AvatarEyePosition = new Vector3(evt.newValue.x, evt.newValue.y, 0);
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
        Avatar = (BasisAvatar)target;
        BasisAvatarGizmoEditor.UpdateGizmos(this, Avatar);
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
        AvatarIconField.value = Avatar.BasisBundleDescription.AssetBundleIcon;

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

        BasisSDKCommonInspector.CreateContentTagsFoldout(uiElementsRoot, Avatar);
        BasisSDKCommonInspector.CreateBuildTargetOptions(uiElementsRoot);
        BasisSDKCommonInspector.CreateBuildOptionsDropdown(uiElementsRoot);
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        AvatarIconField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(OnIconFieldChanged);
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

    private void OnIconFieldChanged(ChangeEvent<UnityEngine.Object> evt)
    {
        Avatar.BasisBundleDescription.AssetBundleIcon = evt.newValue as Texture2D;
        EditorUtility.SetDirty(Avatar);
        BasisDebug.Log($"Setting to {Avatar.BasisBundleDescription.AssetBundleIcon}");
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
                // Build from a stripped clone so the authored avatar stays untouched.
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
        var meshAssetsNeedingSave = new HashSet<Mesh>(); // for .asset meshes (and others without ModelImporter)

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
                // .asset Mesh (or otherwise not model-imported):
                // Generate Mesh LODs directly on the mesh asset.
                // meshLodLimit here = "max number of LOD levels to generate" (see docs).
                // 0 => generate none beyond original; negative => auto-stop at ~64 indices.  :contentReference[oaicite:3]{index=3}
                int meshLodLimit = lodLimit; // reuse your parameter; adjust semantics if you want
                MeshLodUtility.GenerateMeshLods(mesh, meshLodLimit);

                EditorUtility.SetDirty(mesh);
                meshAssetsNeedingSave.Add(mesh);
            }

            // Renderer-level knobs (work once mesh actually has Mesh LODs) :contentReference[oaicite:4]{index=4}
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
        if (!Application.isPlaying)
        {
            // GlobalObjectId for a prefab-stage instance resolves to null after
            // EnterPlaymode auto-closes the stage, so the avatar would silently
            // vanish post-transition. Bail rather than try to map back to the asset.
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && Avatar != null && Avatar.gameObject.scene == stage.scene)
            {
                EditorUtility.DisplayDialog(
                    "Test In Editor",
                    "Close the prefab edit mode and select the avatar from the Project window or a scene before testing.",
                    BasisEditorLocalization.Get("sdk.common.dialog.yes"));
                return;
            }

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
    }
    public void RequestAvatarLoad()
    {
        RequestAvatarLoad(Avatar);
    }

    private static void RequestAvatarLoad(BasisAvatar avatar)
    {
        if (BasisLocalPlayerData.PlayerReady)
        {
            BasisDebug.Log("Player Ready Loading", BasisDebug.LogTag.Editor);
            LoadAvatar(avatar);
        }
        else
        {
            ScheduledTestInEditorAvatar = avatar;
            BasisDebug.Log("Scheduling Load Avatar", BasisDebug.LogTag.Editor);
            BasisLocalPlayerData.OnLocalPlayerInitalized -= LoadScheduledAvatar;
            BasisLocalPlayerData.OnLocalPlayerInitalized += LoadScheduledAvatar;
        }
    }

    private static void LoadScheduledAvatar()
    {
        BasisLocalPlayerData.OnLocalPlayerInitalized -= LoadScheduledAvatar;
        if (ScheduledTestInEditorAvatar == null)
        {
            return;
        }

        BasisAvatar avatar = ScheduledTestInEditorAvatar;
        ScheduledTestInEditorAvatar = null;
        LoadAvatar(avatar);
    }

    private static async void LoadAvatar(BasisAvatar avatar)
    {
        BasisDebug.Log("LoadAvatar Called", BasisDebug.LogTag.Editor);

        var jigglesToReset = new List<MonoBehaviour>();
        foreach (MonoBehaviour jiggle in avatar.gameObject.GetComponentsInChildren<MonoBehaviour>(false))
        {
            if (jiggle != null
                && jiggle.GetType().FullName == "GatorDragonGames.JigglePhysics.JiggleRig"
                && jiggle.enabled)
            {
                jigglesToReset.Add(jiggle);
            }
        }
        GameObject inSceneItem;
        if (jigglesToReset.Count > 0)
        {
            BasisDebug.Log("Enabled Jiggles were found when Test in Editor was entered. The avatar will be disabled in order to reset the Jiggle transforms.", BasisDebug.LogTag.Editor);
            avatar.gameObject.SetActive(false);
            // It's a bit of a hack, but waiting three frames works.
            await Awaitable.NextFrameAsync();
            await Awaitable.NextFrameAsync();
            await Awaitable.NextFrameAsync();
            inSceneItem = GameObject.Instantiate(avatar.gameObject);
            avatar.gameObject.SetActive(true);
            inSceneItem.SetActive(true);
        }
        else
        {
            inSceneItem = GameObject.Instantiate(avatar.gameObject);
        }

        BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(inSceneItem);
        OnBeforeTestInEditor?.Invoke(inSceneItem);
        BasisAssetBundlePipeline.PostProcessAvatar(inSceneItem);

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
        await BasisLocalPlayerData.Instance.CreateAvatarFromMode(BasisLoadMode.ByGameobjectReference, LoadableBundle);
        BasisDebug.Log("Avatar Load Complete", BasisDebug.LogTag.Editor);
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
