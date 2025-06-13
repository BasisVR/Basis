using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityEngine;
using Basis.Scripts.Behaviour;
using Basis.Scripts.BasisSdk; // Adjust this based on your namespace location

[CustomEditor(typeof(BasisAvatarMonoBehaviour), true)]
public class BasisAvatarMonoBehaviourEditor : Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        // Get the default inspector container
        var root = new VisualElement();

        // Reference to the target object
        var behaviour = (BasisAvatarMonoBehaviour)target;

        //Layout(root, behaviour);

        // --- Default Inspector Section ---
        var defaultInspector = new VisualElement();
        InspectorElement.FillDefaultInspector(defaultInspector, serializedObject, this);
        root.Add(defaultInspector);


        return root;
    }

    private static void Layout(VisualElement root, BasisAvatarMonoBehaviour behaviour)
    {
        // Create a toggle for IsInitalized (read-only for display)
        var isInitializedToggle = new Toggle("Is Initialized")
        {
            value = behaviour.IsInitalized,
            tooltip = "Indicates whether this behaviour has been initialized.",
            pickingMode = PickingMode.Ignore
        };
        isInitializedToggle.SetEnabled(false);
        // root.Add(isInitializedToggle);

        // MessageIndex field
        var messageIndexField = new IntegerField("Message Index")
        {
            value = behaviour.MessageIndex,
            pickingMode = PickingMode.Ignore,
            isReadOnly = true
        };
        // root.Add(messageIndexField);

        Label messageField = new Label("Basis MonoBehaviour");
        root.Add(messageField);
    }
}
