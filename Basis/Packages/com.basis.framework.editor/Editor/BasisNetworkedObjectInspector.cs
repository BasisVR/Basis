using Basis.Scripts.BasisSdk.Helpers.Editor;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisNetworkedContent))]
public class BasisNetworkedObjectInspector : Editor
{
    public VisualTreeAsset visualTree;
    public BasisNetworkedContent BasisNetworkedObject;
    public VisualElement rootElement;
    public VisualElement uiElementsRoot;
    private Label resultLabel; // Store the result label for later clearing
    public BasisAssetBundleObject assetBundleObject;
    public void OnEnable()
    {
        visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BasisSDKConstants.PropuxmlPath);
        BasisNetworkedObject = (BasisNetworkedContent)target;
    }
    public override VisualElement CreateInspectorGUI()
    {
        BasisNetworkedObject = (BasisNetworkedContent)target;
        rootElement = new VisualElement();

        // Draw default inspector elements first
        InspectorElement.FillDefaultInspector(rootElement, serializedObject, this);

        if (visualTree != null)
        {
            uiElementsRoot = visualTree.CloneTree();
            rootElement.Add(uiElementsRoot);
            BasisSDKCommonInspector.CreateBuildTargetOptions(uiElementsRoot);
            BasisSDKCommonInspector.CreateBuildOptionsDropdown(uiElementsRoot);

            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            Button BuildButton = BasisHelpersGizmo.Button(uiElementsRoot, BasisSDKConstants.BuildButton);
            BuildButton.clicked += () => Build(BuildButton, assetBundleObject.selectedTargets);
        }
        else
        {
            Debug.LogError("VisualTree is null. Make sure the UXML file is assigned correctly.");
        }

        return rootElement;
    }

    private async void Build(Button buildButton, List<BuildTarget> targets)
    {
        if (targets == null || targets.Count == 0)
        {
            Debug.LogError("No build targets selected.");
            return;
        }

        Debug.Log($"Building Gameobject Bundles for: {string.Join(", ", targets.ConvertAll(t => BasisSDKConstants.targetDisplayNames[t]))}");
        (bool success, string message) = await BasisBundleBuild.GameObjectBundleBuild(BasisNetworkedObject, targets);
        EditorUtility.ClearProgressBar();
        // Clear any previous result label
        ClearResultLabel();

        // Display new result in the UI
        resultLabel = new Label
        {
            style = { fontSize = 14 }
        };

        if (success)
        {
            resultLabel.text = "Build successful";
            resultLabel.style.backgroundColor = Color.green;
            resultLabel.style.color = Color.black; // Success message color
        }
        else
        {
            resultLabel.text = $"Build failed: {message}";
            resultLabel.style.backgroundColor = Color.red;
            resultLabel.style.color = Color.black; // Error message color
        }

        // Add the result label to the UI
        uiElementsRoot.Add(resultLabel);
       // BuildReportViewerWindow.ShowWindow();
    }

    // Method to clear the result label
    private void ClearResultLabel()
    {
        if (resultLabel != null)
        {
            uiElementsRoot.Remove(resultLabel);  // Remove the label from the UI
            resultLabel = null; // Optionally reset the reference to null
        }
    }
}
