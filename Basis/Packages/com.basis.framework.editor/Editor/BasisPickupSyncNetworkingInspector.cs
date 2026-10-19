using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Interactions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisPickupSyncNetworking))]
[CanEditMultipleObjects]
public class BasisPickupSyncNetworkingInspector : BasisSyncedTransformInspector
{
    public override VisualElement CreateInspectorGUI()
    {
        // Reuse the full BasisSyncedTransform UI (axis toggles, precision, smoothing, networking).
        VisualElement root = base.CreateInspectorGUI();

        // Swap the generic header for a pickup-specific one.
        if (root.childCount > 0) root.RemoveAt(0);
        root.Insert(0, BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.pickup.title"),
            BasisEditorLocalization.Get("editor.sync.pickup.description")));

        VisualElement pickup = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.pickup.card"));
        pickup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("BasisPickupInteractable"), BasisEditorLocalization.Get("editor.sync.pickup.interactable")),
            BasisEditorLocalization.Get("editor.sync.pickup.interactable.tooltip")));
        pickup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("CanNetworkSteal"), BasisEditorLocalization.Get("editor.sync.pickup.canNetworkSteal")),
            BasisEditorLocalization.Get("editor.sync.pickup.canNetworkSteal.tooltip")));
        pickup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("IsStatic"), BasisEditorLocalization.Get("editor.sync.pickup.isStatic")),
            BasisEditorLocalization.Get("editor.sync.pickup.isStatic.tooltip")));
        pickup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("RemoteDeadReckon"), BasisEditorLocalization.Get("editor.sync.pickup.remoteDeadReckon")),
            BasisEditorLocalization.Get("editor.sync.pickup.remoteDeadReckon.tooltip")));
        pickup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("AttachToHandOnGrab"), BasisEditorLocalization.Get("editor.sync.pickup.attachToHand")),
            BasisEditorLocalization.Get("editor.sync.pickup.attachToHand.tooltip")));
        root.Insert(1, pickup);
        pickup.Bind(serializedObject);

        return root;
    }

    protected override List<BasisSyncIssue> Validate()
    {
        var issues = base.Validate();
        var p = target as BasisPickupSyncNetworking;
        if (p != null && p.BasisPickupInteractable == null
            && p.GetComponentInChildren<BasisPickupInteractable>(true) == null)
        {
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.pickup.noInteractable")));
        }
        return issues;
    }
}
