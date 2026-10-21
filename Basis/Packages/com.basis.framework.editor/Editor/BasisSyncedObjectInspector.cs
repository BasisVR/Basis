using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSyncedObject))]
[CanEditMultipleObjects]
public class BasisSyncedObjectInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.object.title"),
            BasisEditorLocalization.Get("editor.sync.object.description")));

        var note = new HelpBox(
            BasisEditorLocalization.Get("editor.sync.object.note"),
            HelpBoxMessageType.Info);
        note.style.marginBottom = 8;
        root.Add(note);

        root.Add(BasisSyncInspectorUI.NetworkingCard(serializedObject));
        root.Add(BasisSyncInspectorUI.SmoothingCard(serializedObject));

        root.Bind(serializedObject);

        root.Add(BasisSyncInspectorUI.NetworkInfo(serializedObject.targetObject));

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }
}
