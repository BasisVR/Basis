#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace GatorDragonGames.JigglePhysics {

[CustomPropertyDrawer(typeof(JiggleRigData))]
public class JiggleRigDataPropertyDrawer : PropertyDrawer {
    public override VisualElement CreatePropertyGUI(SerializedProperty property) {
        var visualTreeAsset =
            AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                AssetDatabase.GUIDToAssetPath("3b91b5cf6b975bd4d83d8a940258c420"));
        var visualElement = new VisualElement();
        visualTreeAsset.CloneTree(visualElement);

        var rootElement = visualElement.Q<ObjectField>("RootField");
        rootElement.objectType = typeof(Transform);
        var rootProp = property.FindPropertyRelative(nameof(JiggleRigData.rootBone));
        rootElement.BindProperty(rootProp);
        rootElement.Q<Label>().text = "Root Transform";

        var excludeRootToggleElement = visualElement.Q<Toggle>("ExcludeRootToggle");
        excludeRootToggleElement.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.excludeRoot)));
        excludeRootToggleElement.Q<Label>().text = "Motionless Root";
        excludeRootToggleElement.tooltip =
            "Exclude the root from the jiggle simulation. Use this to coalesce many branching jiggles.";

        var lockFromGrabbingToggle = new Toggle("Lock From Grabbing");
        lockFromGrabbingToggle.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.lockFromGrabbing)));
        lockFromGrabbingToggle.tooltip = "Prevents players from grabbing and pulling this jiggle chain.";
        excludeRootToggleElement.parent.Insert(
            excludeRootToggleElement.parent.IndexOf(excludeRootToggleElement) + 1, lockFromGrabbingToggle);

        var maxGrabStretchField = new FloatField("Max Grab Stretch");
        maxGrabStretchField.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.maxGrabStretch)));
        maxGrabStretchField.tooltip =
            "How far a grab may pull a bone from its animated pose, as a multiple of that bone's distance " +
            "from the chain root — so the limit scales with chain length. 1 lets a chain be pulled roughly " +
            "taut. 0 uses the default (" + JiggleGrabConstraint.DefaultMaxStretchFactor + ").";
        lockFromGrabbingToggle.parent.Insert(
            lockFromGrabbingToggle.parent.IndexOf(lockFromGrabbingToggle) + 1, maxGrabStretchField);

        var excludedTransformsElement = visualElement.Q<PropertyField>("IgnoredTransformsField");
        excludedTransformsElement.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.excludedTransforms)));

        var personalCollidersElement = visualElement.Q<PropertyField>("PersonalCollidersField");
        personalCollidersElement.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.jiggleColliders)));

        var container = visualElement.Q<VisualElement>("Contents");
        //var rig = (JiggleRigData)property.boxedValue;

        var inputParams = visualElement.Q<PropertyField>("JiggleTreeInputParameters");
        inputParams.BindProperty(property.FindPropertyRelative(nameof(JiggleRigData.jiggleTreeInputParameters)));
        container.Add(inputParams);

        var rootSection = visualElement.Q<VisualElement>("RootSection");
        excludeRootToggleElement.RegisterValueChangedCallback(evt => {
            if (evt == null || rootSection == null) {
                return;
            }
            rootSection.style.display = evt.newValue ? DisplayStyle.None : DisplayStyle.Flex;
        });

        var recursiveWarning = visualElement.Q<VisualElement>("PersonalColliderWarning");
        if (!property.serializedObject.isEditingMultipleObjects && Application.isPlaying) {
            var rootBone = (Transform)rootProp.objectReferenceValue;
            var isRecursiveRig = false;
            foreach (JiggleRig otherRig in Object.FindObjectsByType<JiggleRig>(FindObjectsInactive.Exclude)) {
                var otherRoot = otherRig.GetJiggleRigData().rootBone;
                if (rootBone && rootBone != otherRoot && rootBone.IsChildOf(otherRoot)) {
                    isRecursiveRig = true;
                    break;
                }
            }

            if (isRecursiveRig) {
                recursiveWarning.style.display = DisplayStyle.Flex;
                var warningText =
                    "Recursive rigs ignore colliders as they get merged with their parent rig. If colliders are needed, add them to the parent rig instead.";
                HelpBox helpBox = new HelpBox(warningText, HelpBoxMessageType.Warning);
                recursiveWarning.Add(helpBox);
            }
            else {
                recursiveWarning.style.display = DisplayStyle.None;
            }
        }
        else {
            recursiveWarning.style.display = DisplayStyle.None;
        }

        return visualElement;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
        EditorGUI.LabelField(position, "Jiggle Physics doesn't support IMGUI inspectors, sorry!");
    }
}

}

#endif