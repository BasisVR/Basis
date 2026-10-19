using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Interactions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSeatSync))]
[CanEditMultipleObjects]
public class BasisSeatSyncInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.seat.title"),
            BasisEditorLocalization.Get("editor.sync.seat.description")));

        root.Add(BasisSyncInspectorUI.ValidationContainer(ValidateSeat));

        VisualElement seat = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.seat.card"));
        seat.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Seat"), BasisEditorLocalization.Get("editor.sync.seat.seatField")),
            BasisEditorLocalization.Get("editor.sync.seat.seatField.tooltip")));
        root.Add(seat);

        root.Bind(serializedObject);

        root.Add(BasisSyncInspectorUI.NetworkInfo(serializedObject.targetObject));

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }

    private List<BasisSyncIssue> ValidateSeat()
    {
        var issues = new List<BasisSyncIssue>();
        var s = target as BasisSeatSync;
        if (s == null) return issues;
        if (s.Seat == null && s.GetComponent<BasisSeat>() == null)
            issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.sync.seat.noSeat")));
        return issues;
    }
}
