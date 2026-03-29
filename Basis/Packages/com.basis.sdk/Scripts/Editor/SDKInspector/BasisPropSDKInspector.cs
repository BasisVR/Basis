using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Helpers.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static BasisAvatarValidator;

[CustomEditor(typeof(BasisProp))]
public class BasisPropSDKInspector : Editor
{
    public VisualTreeAsset visualTree;
    public BasisProp BasisProp;
    public VisualElement rootElement;
    public VisualElement uiElementsRoot;
    private Label resultLabel;
    public BasisAssetBundleObject assetBundleObject;
    public BasisPropValidator BasisPropValidator;

    public void OnEnable()
    {
        visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BasisSDKConstants.PropuxmlPath);
        BasisProp = (BasisProp)target;
    }

    public void OnDisable()
    {
        if (BasisPropValidator != null)
        {
            BasisPropValidator.OnDestroy();
        }
    }

    public override VisualElement CreateInspectorGUI()
    {
        BasisProp = (BasisProp)target;
        rootElement = new VisualElement();

        if (visualTree != null)
        {
            uiElementsRoot = visualTree.CloneTree();
            rootElement.Add(uiElementsRoot);

            BasisPropValidator = new BasisPropValidator(BasisProp, rootElement);

            // Documentation button
            Button docButton = DocumentationButton(rootElement, "Open Prop Documentation");
            docButton.clicked += delegate
            {
                if (EditorUtility.DisplayDialog("Open Documentation", "Open Documentation", "Yes I want to open the documentation", "no send me back"))
                {
                    Application.OpenURL(BasisSDKConstants.PropDocumentationURL);
                }
            };
            rootElement.Add(docButton);

            // Name and description fields
            TextField PropNameField = uiElementsRoot.Q<TextField>(BasisSDKConstants.PropName);
            TextField PropDescriptionField = uiElementsRoot.Q<TextField>(BasisSDKConstants.PropDescription);

            PropNameField.value = BasisProp.BasisBundleDescription.AssetBundleName;
            PropDescriptionField.value = BasisProp.BasisBundleDescription.AssetBundleDescription;

            PropNameField.RegisterCallback<ChangeEvent<string>>(PropNameChanged);
            PropDescriptionField.RegisterCallback<ChangeEvent<string>>(PropDescriptionChanged);

            // Icon field
            ObjectField PropIconField = uiElementsRoot.Q<ObjectField>(BasisSDKConstants.PropIcon);
            PropIconField.objectType = typeof(Texture2D);
            PropIconField.allowSceneObjects = true;
            PropIconField.value = BasisProp.BasisBundleDescription.AssetBundleIcon;
            PropIconField.RegisterCallback<ChangeEvent<UnityEngine.Object>>(OnIconFieldChanged);

            // Build options
            BasisSDKCommonInspector.CreateBuildTargetOptions(uiElementsRoot);
            BasisSDKCommonInspector.CreateBuildOptionsDropdown(uiElementsRoot);

            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            Button BuildButton = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.BuildButton);
            BuildButton.clicked += () => Build(BuildButton, assetBundleObject.selectedTargets, BasisProp.BasisBundleDescription.AssetBundleIcon);
        }
        else
        {
            Debug.LogError("VisualTree is null. Make sure the UXML file is assigned correctly.");
        }

        return rootElement;
    }

    private void OnIconFieldChanged(ChangeEvent<UnityEngine.Object> evt)
    {
        BasisProp.BasisBundleDescription.AssetBundleIcon = evt.newValue as Texture2D;
        EditorUtility.SetDirty(BasisProp);
        BasisDebug.Log($"Setting to {BasisProp.BasisBundleDescription.AssetBundleIcon}");
    }

    private void PropNameChanged(ChangeEvent<string> evt)
    {
        BasisProp.BasisBundleDescription.AssetBundleName = evt.newValue;
        EditorUtility.SetDirty(BasisProp);
    }

    private void PropDescriptionChanged(ChangeEvent<string> evt)
    {
        BasisProp.BasisBundleDescription.AssetBundleDescription = evt.newValue;
        EditorUtility.SetDirty(BasisProp);
    }

    private async void Build(Button buildButton, List<BuildTarget> targets, Texture2D Image)
    {
        if (targets == null || targets.Count == 0)
        {
            Debug.LogError("No build targets selected.");
            return;
        }

        if (BasisPropValidator.ValidateProp(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> suggestions, out List<string> passes))
        {
            if (Image == null)
            {
                Image = AssetPreview.GetAssetPreview(BasisProp.gameObject);
            }
            string ImageBytes = null;
            if (Image != null)
            {
                ImageBytes = BasisTextureCompression.ToPngBytes(Image);
            }
            Debug.Log($"Building Gameobject Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            (bool success, string message) = await BasisBundleBuild.GameObjectBundleBuild(ImageBytes, BasisProp, targets, assetBundleObject.UseCustomPassword, assetBundleObject.UserSelectedPassword);
            EditorUtility.ClearProgressBar();
            ClearResultLabel();

            resultLabel = new Label
            {
                style = { fontSize = 14 }
            };

            if (success)
            {
                resultLabel.text = "Build successful";
                resultLabel.style.backgroundColor = Color.green;
                resultLabel.style.color = Color.black;
            }
            else
            {
                resultLabel.text = $"Build failed: {message}";
                resultLabel.style.backgroundColor = Color.red;
                resultLabel.style.color = Color.black;
            }

            uiElementsRoot.Add(resultLabel);
        }
        else
        {
            EditorUtility.DisplayDialog("Prop Build Error",
                $"Please resolve the following issues before building:\n{string.Join("\n", errors.ConvertAll(e => e.Message))}",
                "OK");
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

    public Button DocumentationButton(VisualElement rootElement, string Text)
    {
        Button fixMeButton = new Button();
        fixMeButton.text = Text;

        Color backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        fixMeButton.style.backgroundColor = new StyleColor(backgroundColor);
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;
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
            fixMeButton.style.backgroundColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f, 1f));
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(backgroundColor);
        });

        rootElement.Add(fixMeButton);
        return fixMeButton;
    }
}
