using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSyncedRigidbody))]
[CanEditMultipleObjects]
public class BasisSyncedRigidbodyInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.rigidbody.title"),
            BasisEditorLocalization.Get("editor.sync.rigidbody.description")));

        root.Add(BasisSyncInspectorUI.ValidationContainer(Validate));

        VisualElement body = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.rigidbody.card.body"));
        body.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Body"), BasisEditorLocalization.Get("editor.sync.rigidbody.body")),
            BasisEditorLocalization.Get("editor.sync.rigidbody.body.tooltip")));
        body.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("RelativeTo"), BasisEditorLocalization.Get("editor.sync.relativeTo")),
            BasisEditorLocalization.Get("editor.sync.relativeTo.tooltip")));
        var relHint = new Label(BasisEditorLocalization.Get("editor.sync.rigidbody.relativeToHint"));
        relHint.style.whiteSpace = WhiteSpace.Normal;
        relHint.style.fontSize = 10;
        relHint.style.opacity = 0.8f;
        body.Add(relHint);
        root.Add(body);

        VisualElement pos = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.channel.position"));
        pos.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.syncPosition")) { bindingPath = "SyncPosition", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.syncPosition.tooltip") });
        pos.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.interpolate")) { bindingPath = "InterpolatePosition", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.interpolatePosition.tooltip") });
        root.Add(pos);

        root.Add(RotationGroup());

        root.Add(ScaleGroup());

        VisualElement vel = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.rigidbody.card.velocity"));
        vel.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.rigidbody.syncLinearVelocity")) { bindingPath = "SyncLinearVelocity", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.syncLinearVelocity.tooltip") });
        vel.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.rigidbody.syncAngularVelocity")) { bindingPath = "SyncAngularVelocity", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.syncAngularVelocity.tooltip") });
        root.Add(vel);

        VisualElement remote = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.rigidbody.card.remote"), false);
        remote.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.rigidbody.driveRemoteKinematic")) { bindingPath = "DriveRemoteKinematic", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.driveRemoteKinematic.tooltip") });
        var remoteHint = new Label(BasisEditorLocalization.Get("editor.sync.rigidbody.remoteHint"));
        remoteHint.style.whiteSpace = WhiteSpace.Normal;
        remoteHint.style.fontSize = 10;
        remoteHint.style.opacity = 0.8f;
        remote.Add(remoteHint);
        root.Add(remote);

        root.Add(BuildCompression());

        root.Add(BasisSyncInspectorUI.SmoothingCard(serializedObject));
        root.Add(BasisSyncInspectorUI.NetworkingCard(serializedObject));

        root.Add(BasisSyncInspectorUI.WireSizeCard(FillWireSize));

        root.Bind(serializedObject);

        root.Add(BasisSyncInspectorUI.NetworkInfo(serializedObject.targetObject));

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }

    protected virtual List<BasisSyncIssue> Validate()
    {
        var issues = new List<BasisSyncIssue>();
        var t = target as BasisSyncedRigidbody;
        if (t == null) return issues;

        if (t.Body == null && t.GetComponent<UnityEngine.Rigidbody>() == null)
            issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.sync.rigidbody.noBody")));

        if (!(t.SyncPosition || t.SyncRotation || t.SyncScale || t.SyncLinearVelocity || t.SyncAngularVelocity))
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.rigidbody.nothingSynced")));

        return issues;
    }

    private VisualElement RotationGroup()
    {
        VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.channel.rotation"));

        var sync = new Toggle(BasisEditorLocalization.Get("editor.sync.syncRotation")) { bindingPath = "SyncRotation", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.syncRotation.tooltip") };
        var mode = new PropertyField(serializedObject.FindProperty("RotationMode"), BasisEditorLocalization.Get("editor.common.mode"));
        mode.tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.rotationMode.tooltip");
        var bits = new SliderInt(BasisEditorLocalization.Get("editor.sync.smallestThreeBits"), 6, 16) { showInputField = true, bindingPath = "SmallestThreeBits", tooltip = BasisEditorLocalization.Get("editor.sync.smallestThreeBits.tooltip") };
        var interp = new Toggle(BasisEditorLocalization.Get("editor.sync.interpolate")) { bindingPath = "InterpolateRotation", tooltip = BasisEditorLocalization.Get("editor.sync.interpolateRotation.tooltip") };

        void Apply(bool on, int modeIdx)
        {
            mode.SetEnabled(on);
            bits.style.display = (on && modeIdx == (int)BasisRotationSyncMode.SmallestThree) ? DisplayStyle.Flex : DisplayStyle.None;
        }
        Apply(serializedObject.FindProperty("SyncRotation").boolValue, serializedObject.FindProperty("RotationMode").enumValueIndex);
        sync.RegisterValueChangedCallback(evt => Apply(evt.newValue, serializedObject.FindProperty("RotationMode").enumValueIndex));
        mode.RegisterValueChangeCallback(evt => Apply(sync.value, serializedObject.FindProperty("RotationMode").enumValueIndex));

        card.Add(sync);
        card.Add(mode);
        card.Add(bits);
        card.Add(interp);
        return card;
    }

    private VisualElement ScaleGroup()
    {
        VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.channel.scale"));

        var sync = new Toggle(BasisEditorLocalization.Get("editor.sync.syncScale")) { bindingPath = "SyncScale", tooltip = BasisEditorLocalization.Get("editor.sync.rigidbody.syncScale.tooltip") };
        var uniform = new Toggle(BasisEditorLocalization.Get("editor.sync.scaleUniform")) { bindingPath = "ScaleUniform", tooltip = BasisEditorLocalization.Get("editor.sync.scaleUniform.tooltip") };
        VisualElement row = AxisRow("ScaleX", "ScaleY", "ScaleZ");
        row.tooltip = BasisEditorLocalization.Get("editor.sync.axisRow.tooltip");

        void Apply(bool on, bool uni)
        {
            uniform.SetEnabled(on);
            row.SetEnabled(on && !uni);
        }
        Apply(serializedObject.FindProperty("SyncScale").boolValue, serializedObject.FindProperty("ScaleUniform").boolValue);
        sync.RegisterValueChangedCallback(evt => Apply(evt.newValue, uniform.value));
        uniform.RegisterValueChangedCallback(evt => Apply(sync.value, evt.newValue));

        card.Add(sync);
        card.Add(uniform);
        card.Add(row);
        card.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.interpolate")) { bindingPath = "InterpolateScale", tooltip = BasisEditorLocalization.Get("editor.sync.interpolateScale.tooltip") });
        return card;
    }

    private VisualElement AxisRow(string x, string y, string z)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.marginTop = 2;
        row.style.marginLeft = 4;
        row.Add(AxisToggle("X", x));
        row.Add(AxisToggle("Y", y));
        row.Add(AxisToggle("Z", z));
        return row;
    }

    private static VisualElement AxisToggle(string label, string bindingPath)
    {
        var toggle = new Toggle(label) { bindingPath = bindingPath };
        toggle.style.marginRight = 14;
        var labelEl = toggle.Q<Label>();
        if (labelEl != null)
        {
            labelEl.style.minWidth = 0;
            labelEl.style.marginRight = 4;
        }
        return toggle;
    }

    private VisualElement BuildCompression()
    {
        VisualElement foldout = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.compression.card"), false);

        var note = new Label(BasisEditorLocalization.Get("editor.sync.rigidbody.compressionNote"));
        note.style.whiteSpace = WhiteSpace.Normal;
        note.style.fontSize = 10;
        note.style.opacity = 0.8f;
        note.style.marginBottom = 4;
        foldout.Add(note);

        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompX"), BasisEditorLocalization.Get("editor.sync.rigidbody.compPosX")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompY"), BasisEditorLocalization.Get("editor.sync.rigidbody.compPosY")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompZ"), BasisEditorLocalization.Get("editor.sync.rigidbody.compPosZ")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("LinearVelocityComp"), BasisEditorLocalization.Get("editor.sync.rigidbody.compLinVel")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("AngularVelocityComp"), BasisEditorLocalization.Get("editor.sync.rigidbody.compAngVel")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompX"), BasisEditorLocalization.Get("editor.sync.rigidbody.compScaleX")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompY"), BasisEditorLocalization.Get("editor.sync.rigidbody.compScaleY")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompZ"), BasisEditorLocalization.Get("editor.sync.rigidbody.compScaleZ")));
        return foldout;
    }

    private void FillWireSize(BasisWireSizeModel model)
    {
        var t = target as BasisSyncedRigidbody;
        if (t == null) return;
        model.Checksum = t.UseChecksum;
        model.SendIntervalSeconds = t.SendIntervalSeconds;
        model.KeyframeIntervalSeconds = t.KeyframeIntervalSeconds;

        int posBits = 0, posFields = 0;
        if (t.SyncPosition)
        {
            posBits = t.PosCompX.ToSpec().BitCount + t.PosCompY.ToSpec().BitCount + t.PosCompZ.ToSpec().BitCount;
            posFields = 3;
        }
        model.Add(BasisEditorLocalization.Get("editor.sync.channel.position"), posBits, posFields, BasisEditorLocalization.Get("editor.sync.wire.axes", 3));

        int rotBits = 0, rotFields = 0;
        string rotDetail = "";
        if (t.SyncRotation)
        {
            switch (t.RotationMode)
            {
                case BasisRotationSyncMode.QuaternionHalf:
                    rotBits = 64; rotFields = 4; rotDetail = BasisEditorLocalization.Get("editor.sync.wire.quatHalf");
                    break;
                case BasisRotationSyncMode.QuaternionRaw:
                    rotBits = 128; rotFields = 4; rotDetail = BasisEditorLocalization.Get("editor.sync.wire.quatRaw");
                    break;
                case BasisRotationSyncMode.Euler:
                    rotBits = 96; rotFields = 3; rotDetail = BasisEditorLocalization.Get("editor.sync.wire.axesEulerRaw", 3);
                    break;
                default:
                {
                    int magBits = UnityEngine.Mathf.Clamp(t.SmallestThreeBits, 2, 18);
                    rotBits = 2 + 3 * (1 + magBits);
                    rotFields = 1;
                    rotDetail = BasisEditorLocalization.Get("editor.sync.wire.smallestThree", magBits);
                    break;
                }
            }
        }
        model.Add(BasisEditorLocalization.Get("editor.sync.channel.rotation"), rotBits, rotFields, rotDetail);

        if (t.SyncLinearVelocity)
        {
            int b = t.LinearVelocityComp.ToSpec().BitCount;
            model.Add(BasisEditorLocalization.Get("editor.sync.wire.linearVel"), b * 3, 3, BasisEditorLocalization.Get("editor.sync.wire.axes", 3));
        }
        else model.Add(BasisEditorLocalization.Get("editor.sync.wire.linearVel"), 0, 0, "");

        if (t.SyncAngularVelocity)
        {
            int b = t.AngularVelocityComp.ToSpec().BitCount;
            model.Add(BasisEditorLocalization.Get("editor.sync.wire.angularVel"), b * 3, 3, BasisEditorLocalization.Get("editor.sync.wire.axes", 3));
        }
        else model.Add(BasisEditorLocalization.Get("editor.sync.wire.angularVel"), 0, 0, "");

        int scaleBits = 0, scaleFields = 0;
        string scaleDetail = "";
        if (t.SyncScale)
        {
            if (t.ScaleUniform)
            {
                scaleBits = t.ScaleCompX.ToSpec().BitCount;
                scaleFields = 1;
                scaleDetail = BasisEditorLocalization.Get("editor.sync.wire.uniform");
            }
            else
            {
                if (t.ScaleX) { scaleBits += t.ScaleCompX.ToSpec().BitCount; scaleFields++; }
                if (t.ScaleY) { scaleBits += t.ScaleCompY.ToSpec().BitCount; scaleFields++; }
                if (t.ScaleZ) { scaleBits += t.ScaleCompZ.ToSpec().BitCount; scaleFields++; }
                scaleDetail = BasisEditorLocalization.Get("editor.sync.wire.axes", scaleFields);
            }
        }
        model.Add(BasisEditorLocalization.Get("editor.sync.channel.scale"), scaleBits, scaleFields, scaleDetail);
    }
}
