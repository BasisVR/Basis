using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Network.Vehicles;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisNetworkedVehicle))]
[CanEditMultipleObjects]
public class BasisNetworkedVehicleInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.vehicles.inspector.title"),
            BasisEditorLocalization.Get("editor.vehicles.inspector.description")));

        root.Add(BasisSyncInspectorUI.ValidationContainer(Validate));

        VisualElement core = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.vehicles.inspector.vehicleCard"));
        core.Add(new PropertyField(serializedObject.FindProperty("BasisVehicleBody"), BasisEditorLocalization.Get("editor.vehicles.inspector.vehicleBody")));
        core.Add(new PropertyField(serializedObject.FindProperty("Seat"), BasisEditorLocalization.Get("editor.sync.seat.seatField")));
        core.Add(new PropertyField(serializedObject.FindProperty("SeatSync"), BasisEditorLocalization.Get("editor.vehicles.inspector.seatSync")));
        core.Add(new PropertyField(serializedObject.FindProperty("Rigidbody"), BasisEditorLocalization.Get("editor.vehicles.inspector.rigidbody")));
        core.Add(new PropertyField(serializedObject.FindProperty("EngineAudio"), BasisEditorLocalization.Get("editor.vehicles.inspector.engineAudio")));
        core.Add(new PropertyField(serializedObject.FindProperty("SteeringWheel"), BasisEditorLocalization.Get("editor.vehicles.inspector.steeringWheel")));
        root.Add(core);

        VisualElement wheels = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.vehicles.inspector.wheelsCard"));
        wheels.Add(new PropertyField(serializedObject.FindProperty("Colliders"), BasisEditorLocalization.Get("editor.vehicles.inspector.colliders")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("Wheels"), BasisEditorLocalization.Get("editor.vehicles.inspector.wheels")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("SpinVisuals"), BasisEditorLocalization.Get("editor.vehicles.inspector.spinVisuals")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("SteerVisuals"), BasisEditorLocalization.Get("editor.vehicles.inspector.steerVisuals")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("SpinAxisLocal"), BasisEditorLocalization.Get("editor.vehicles.inspector.spinAxisLocal")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("SteerAxisLocal"), BasisEditorLocalization.Get("editor.vehicles.inspector.steerAxisLocal")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("SteerRangeDeg"), BasisEditorLocalization.Get("editor.vehicles.inspector.steerRangeDeg")));
        wheels.Add(new PropertyField(serializedObject.FindProperty("HalfPrecisionWheels"), BasisEditorLocalization.Get("editor.vehicles.inspector.halfPrecisionWheels")));
        root.Add(wheels);

        root.Add(BasisSyncInspectorUI.NetworkingCard(serializedObject));
        root.Add(BasisSyncInspectorUI.SmoothingCard(serializedObject));

        root.Bind(serializedObject);

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }

    private List<BasisSyncIssue> Validate()
    {
        var issues = new List<BasisSyncIssue>();
        var v = target as BasisNetworkedVehicle;
        if (v == null) return issues;

        if (v.BasisVehicleBody == null)
            issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.vehicles.inspector.noBody")));
        if (v.Seat == null)
            issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.vehicles.inspector.noSeat")));
        if (v.Rigidbody == null)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.vehicles.inspector.noRigidbody")));
        if (v.SeatSync == null)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.vehicles.inspector.noSeatSync")));
        if (v.Wheels == null || v.Wheels.Length == 0)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.vehicles.inspector.noWheels")));
        if (v.SpinVisuals == null || v.SpinVisuals.Length == 0)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.vehicles.inspector.noSpinVisuals")));
        return issues;
    }
}
