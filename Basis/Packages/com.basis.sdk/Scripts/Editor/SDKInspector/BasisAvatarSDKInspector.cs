using System;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers.Editor;
using Basis.Scripts.Editor;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Basis.Scripts.BasisSdk.Players;
[CustomEditor(typeof(BasisAvatar))]
public partial class BasisAvatarSDKInspector : Editor
{
    public static event Action<BasisAvatarSDKInspector> InspectorGuiCreated;
    public static event Action ButtonClicked;
    public static event Action ValueChanged;
    public VisualTreeAsset visualTree;
    public BasisAvatar Avatar;
    public VisualElement uiElementsRoot;
    public bool AvatarEyePositionState = false;
    public bool AvatarMouthPositionState = false;
    public VisualElement rootElement;
    //Deprecated 15052025 Use BasisJiggleBonesComponent instead public AvatarSDKJiggleBonesView AvatarSDKJiggleBonesView = new AvatarSDKJiggleBonesView();
    public AvatarSDKVisemes AvatarSDKVisemes = new AvatarSDKVisemes();
    public Button EventCallbackAvatarBundleButton { get; private set; }
    public Texture2D Texture;
    private Label resultLabel; // Store the result label for later clearing
    public string Error;
    public BasisAvatarValidator BasisAvatarValidator;
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
            Button button = new Button();
            button.text = "Open Avatar Documentation";
            button.clicked += delegate
            {
                if (EditorUtility.DisplayDialog("Open Documentation", "Open Documentation", "Yes I want to open the documentation", "no send me back"))
                {
                    Application.OpenURL(BasisSDKConstants.AvatarDocumentationURL);
                }
            };
            rootElement.Add(button);
            BasisAutomaticSetupAvatarEditor.TryToAutomatic(this);
            SetupItems();
            //deprecated 15/05/2025 use BasisJiggleBonesComponent  AvatarSDKJiggleBonesView.Initialize(this);
            AvatarSDKVisemes.Initialize(this);
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
        Button.text = "Eye Position Gizmo " + AvatarHelper.BoolToText(AvatarEyePositionState);
        EditorUtility.SetDirty(Avatar);
        ButtonClicked?.Invoke();
    }

    public void ClickedAvatarMouthPositionButton(Button Button)
    {
        Undo.RecordObject(Avatar, "Toggle Mouth Position Gizmo");
        AvatarMouthPositionState = !AvatarMouthPositionState;
        Button.text = "Mouth Position Gizmo " + AvatarHelper.BoolToText(AvatarMouthPositionState);
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
        Button AvatarTestInEditorClick = BasisHelpersGizmo.Button(uiElementsRoot,BasisSDKConstants.AvatarTestInEditor);

        // Initialize Event Callbacks for Vector2 fields (for Avatar Eye and Mouth Position)
        BasisHelpersGizmo.CallBackVector2Field(uiElementsRoot, BasisSDKConstants.avatarEyePositionField, Avatar.AvatarEyePosition, OnEyeHeightValueChanged);
        BasisHelpersGizmo.CallBackVector2Field(uiElementsRoot, BasisSDKConstants.avatarMouthPositionField, Avatar.AvatarMouthPosition, OnMouthHeightValueChanged);

        // Initialize ObjectFields and assign references
        ObjectField animatorField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.animatorField);
        ObjectField faceBlinkMeshField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.FaceBlinkMeshField);
        ObjectField faceVisemeMeshField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.FaceVisemeMeshField);

        TextField AvatarNameField = uiElementsRoot.Q<TextField>(BasisSDKConstants.AvatarName);
        TextField AvatarDescriptionField = uiElementsRoot.Q<TextField>(BasisSDKConstants.AvatarDescription);

        TextField AvatarPasswordField = uiElementsRoot.Q<TextField>(BasisSDKConstants.Avatarpassword);

        ObjectField AvatarIconField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.AvatarIcon);

        animatorField.allowSceneObjects = true;
        faceBlinkMeshField.allowSceneObjects = true;
        faceVisemeMeshField.allowSceneObjects = true;
        AvatarIconField.allowSceneObjects = true;

        AvatarIconField.value = null;
        animatorField.value = Avatar.Animator;
        faceBlinkMeshField.value = Avatar.FaceBlinkMesh;
        faceVisemeMeshField.value = Avatar.FaceVisemeMesh;

        AvatarNameField.value = Avatar.BasisBundleDescription.AssetBundleName;
        AvatarDescriptionField.value = Avatar.BasisBundleDescription.AssetBundleDescription;

        AvatarNameField.RegisterCallback<ChangeEvent<string>>(AvatarName);
        AvatarDescriptionField.RegisterCallback<ChangeEvent<string>>(AvatarDescription);

        AvatarIconField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(OnAssignTexture2D);

        // Button click events
        avatarEyePositionClick.clicked += () => ClickedAvatarEyePositionButton(avatarEyePositionClick);
        avatarMouthPositionClick.clicked += () => ClickedAvatarMouthPositionButton(avatarMouthPositionClick);
        avatarAutomaticVisemeDetectionClick.clicked += AutomaticallyFindVisemes;
        avatarAutomaticBlinkDetectionClick.clicked += AutomaticallyFindBlinking;
        AvatarTestInEditorClick.clicked += AvatarTestInEditorClickFunction;// unity editor window button

        BasisSDKCommonInspector.CreateBuildTargetOptions(uiElementsRoot);
        BasisSDKCommonInspector.CreateBuildOptionsDropdown(uiElementsRoot);
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        avatarBundleButton.clicked += () => EventCallbackAvatarBundle(assetBundleObject.selectedTargets);

        // Register Animator field change event
        animatorField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackAnimator(evt, ref Avatar.Animator));

        // Register Blink and Viseme Mesh field change events
        faceBlinkMeshField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackFaceVisemeMesh(evt, ref Avatar.FaceBlinkMesh));
        faceVisemeMeshField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(evt => EventCallbackFaceVisemeMesh(evt, ref Avatar.FaceVisemeMesh));

        // Update Button Text
        avatarEyePositionClick.text = "Eye Position Gizmo " + AvatarHelper.BoolToText(AvatarEyePositionState);
        avatarMouthPositionClick.text = "Mouth Position Gizmo " + AvatarHelper.BoolToText(AvatarMouthPositionState);
    }
    private async void EventCallbackAvatarBundle(List<BuildTarget> targets)
    {
        if (targets == null || targets.Count == 0)
        {
            Debug.LogError("No build targets selected.");
            return;
        }
        if (BasisAvatarValidator.ValidateAvatar(out List<string> Errors, out List<string> Warnings, out List<string> Passes))
        {
            if (Avatar.Animator.runtimeAnimatorController != null)
            {
                string path = AssetDatabase.GetAssetPath(Avatar.Animator.runtimeAnimatorController);
                if (path == BasisSDKConstants.AvatarAnimatorControllerPath)
                {
                    Debug.Log("Animator Controller Used was the default! UnAssigning");
                    Avatar.Animator.runtimeAnimatorController = null;
                    EditorUtility.SetDirty(Avatar.Animator);
                    AssetDatabase.SaveAssetIfDirty(Avatar);
                }
            }

            Debug.Log($"Building Gameobject Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
            (bool success, string message) = await BasisBundleBuild.GameObjectBundleBuild(Avatar, targets);
            EditorUtility.ClearProgressBar();
            // Clear any previous result label
            ClearResultLabel();

            // Display new result in the UI
            resultLabel = new Label
            {
                style = { fontSize = 14 }
            };
            resultLabel.style.color = Color.black; // Error message color
            if (success)
            {
                resultLabel.text = "Build successful";
                resultLabel.style.backgroundColor = Color.green;
            }
            else
            {
                resultLabel.text = $"Build failed: {message}";
                resultLabel.style.backgroundColor = Color.red;
            }

            // Add the result label to the UI
            uiElementsRoot.Add(resultLabel);
          //  BuildReportViewerWindow.ShowWindow();
        }
        else
        {
            if (EditorUtility.DisplayDialog("Avatar Build Error", $"Please Resolve Or Consult The Documentation. \n {string.Join("\n", Errors)}", "OK", "Open Documentation"))
            {

            }
            else
            {
                Application.OpenURL(BasisSDKConstants.AvatarDocumentationURL);
            }
        }
    }
    public void AvatarTestInEditorClickFunction()
    {
        if (!Application.isPlaying)
        {
            int result = EditorUtility.DisplayDialogComplex("Confirmation","this feature requires the editor to be in playmode. do you want to enter play mode now?", "Yes","No",""
        );

            switch (result)
            {
                case 0: // Yes
                    EditorApplication.EnterPlaymode();
                    break;
                case 1: // No
                    break;
                default:
                    break;
            }
        }
        else
        {
            RequestAvatarLoad();
        }
    }
    public void RequestAvatarLoad()
    {
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisDebug.Log("Player Ready Loading", BasisDebug.LogTag.Editor);
            LoadAvatar();
        }
        else
        {
            ScheduleCallback = true;
            BasisDebug.Log("Scheduling Load Avatar", BasisDebug.LogTag.Editor);
            BasisLocalPlayer.OnLocalPlayerCreatedAndReady += LoadAvatar;
        }
    }
    public bool ScheduleCallback = false;
    public async void LoadAvatar()
    {
        if (ScheduleCallback)
        {
            BasisLocalPlayer.OnLocalPlayerCreatedAndReady -= LoadAvatar;
            ScheduleCallback = false;
        }
        BasisDebug.Log("LoadAvatar Called", BasisDebug.LogTag.Editor);
        BasisLoadableBundle LoadableBundle = new BasisLoadableBundle
        {
            LoadableGameobject = new BasisLoadableGameobject() { InSceneItem = GameObject.Instantiate(Avatar.gameObject) }
        };
        LoadableBundle.LoadableGameobject.InSceneItem.transform.parent = null;
        LoadableBundle.BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
        {
            RemoteBeeFileLocation = BasisGenerateUniqueID.GenerateUniqueID()
        };
        BasisDebug.Log("Requesting Avatar Load", BasisDebug.LogTag.Editor);
        await BasisLocalPlayer.Instance.CreateAvatarFromMode(BasisLoadMode.ByGameobjectReference, LoadableBundle);
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
    public void OnAssignTexture2D(ChangeEvent<UnityEngine.Object> Texture2D)
    {
        Texture = (Texture2D)Texture2D.newValue;
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
}
