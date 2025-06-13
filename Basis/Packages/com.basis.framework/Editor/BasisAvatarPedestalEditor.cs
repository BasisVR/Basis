using UnityEditor.UIElements;
using UnityEditor;
using UnityEngine.UIElements;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using System.Collections.Generic;

[CustomEditor(typeof(BasisAvatarPedestal))]
public class BasisAvatarPedestalEditor : Editor
{
    private VisualElement warningPanel;
    private Label warningMessageLabel;

    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        var titleLabel = new Label("Basis Avatar Pedestal");
        titleLabel.name = "Basis";
        //   titleLabel.se = false;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.fontSize = 40;
        titleLabel.style.unityTextAlign = TextAnchor.UpperCenter;
        titleLabel.style.backgroundColor = new StyleColor(new Color(0.1058824f, 0.1058824f, 0.1058824f, 1));
        titleLabel.style.color = new StyleColor(new Color(239f / 255f, 40f / 255f, 90f / 255f));
        titleLabel.style.flexDirection = FlexDirection.Row;
        titleLabel.style.alignItems = Align.Auto;
        root.Add(titleLabel);



        var pedestal = (BasisAvatarPedestal)target;

        SerializedProperty InteractRangeProp = serializedObject.FindProperty("InteractRange");
        SerializedProperty loadModeProp = serializedObject.FindProperty("LoadMode");
        SerializedProperty avatarProp = serializedObject.FindProperty("Avatar");
        SerializedProperty uniqueIDProp = serializedObject.FindProperty("UniqueID");
        SerializedProperty showAvatarProp = serializedObject.FindProperty("ShowAvatarOnPedestal");
        SerializedProperty wasJustPressedProp = serializedObject.FindProperty("WasJustPressed");
        SerializedProperty bundleProp = serializedObject.FindProperty("LoadableBundle");
        SerializedProperty progressReportProp = serializedObject.FindProperty("BasisProgressReport");
        SerializedProperty cancellationTokenProp = serializedObject.FindProperty("cancellationToken");
        SerializedProperty pedestalAnimatorProp = serializedObject.FindProperty("PedestalAnimatorController");

        var loadModeField = new PropertyField(loadModeProp) { label = "Load Mode" };
        root.Add(loadModeField);

        CreateWarningPanel(root);

        var avatarField = new PropertyField(avatarProp) { label = "Avatar" };
        var bundleField = new PropertyField(bundleProp) { label = "Loadable Bundle" };

        root.Add(bundleField);
        root.Add(avatarField);

        var AvatarField = new PropertyField(showAvatarProp) { label = "Visually Show Avatar" };

        root.Add(AvatarField);

        root.Add(new PropertyField(progressReportProp) { label = "Progress Report" });
        root.Add(new PropertyField(cancellationTokenProp) { label = "Cancellation Token" });
        root.Add(new PropertyField(pedestalAnimatorProp) { label = "Pedestal Animator Controller" });
        void UpdateVisibility()
        {
            var loadMode = (BasisLoadMode)loadModeProp.enumValueIndex;
            bundleField.style.display = (loadMode == BasisLoadMode.Download || loadMode == BasisLoadMode.Local) ? DisplayStyle.Flex : DisplayStyle.None;
            avatarField.style.display = (loadMode == BasisLoadMode.ByGameobjectReference) ? DisplayStyle.Flex : DisplayStyle.None;

            if (loadMode == BasisLoadMode.ByGameobjectReference)
            {
                ShowWarningPanel("When using Gameobject Reference Mode remote players will use fallback avatar.");
            }
            else
            {
                HideWarningPanel();
            }
        }

        UpdateVisibility();

        loadModeField.RegisterValueChangeCallback(evt =>
        {
            serializedObject.Update();
            UpdateVisibility();
        });

        root.TrackSerializedObjectValue(serializedObject, _ =>
        {
            serializedObject.ApplyModifiedProperties();
        });

        var InteractRangeField = new PropertyField(InteractRangeProp) { label = "Interact Range" };
        root.Add(InteractRangeField);

        return root;
    }

    private void HideWarningPanel()
    {
        warningPanel.style.display = DisplayStyle.None;
    }

    private void ShowWarningPanel(string Warning)
    {
        warningMessageLabel.text = Warning;
        warningPanel.style.display = DisplayStyle.Flex;
    }

    public void CreateWarningPanel(VisualElement rootElement)
    {
        warningPanel = new VisualElement();
        warningPanel.style.backgroundColor = new StyleColor(new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
        warningPanel.style.paddingTop = 5;
        warningPanel.style.flexGrow = 1;
        warningPanel.style.paddingBottom = 5;
        warningPanel.style.marginBottom = 10;
        warningPanel.style.borderTopLeftRadius = 5;
        warningPanel.style.borderTopRightRadius = 5;
        warningPanel.style.borderBottomLeftRadius = 5;
        warningPanel.style.borderBottomRightRadius = 5;
        warningPanel.style.borderLeftWidth = 2;
        warningPanel.style.borderRightWidth = 2;
        warningPanel.style.borderTopWidth = 2;
        warningPanel.style.borderBottomWidth = 2;
        warningPanel.style.borderBottomColor = new StyleColor(Color.yellow);

        warningMessageLabel = new Label("No Errors");
        warningMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        warningPanel.Add(warningMessageLabel);

        warningPanel.style.display = DisplayStyle.None;
        rootElement.Add(warningPanel);
    }
}
