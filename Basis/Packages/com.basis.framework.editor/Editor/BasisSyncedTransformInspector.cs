using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSyncedTransform))]
[CanEditMultipleObjects]
public class BasisSyncedTransformInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.transform.title"),
            BasisEditorLocalization.Get("editor.sync.transform.description")));

        root.Add(BasisSyncInspectorUI.ValidationContainer(Validate));

        VisualElement target = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.common.target"));
        target.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Target"), BasisEditorLocalization.Get("editor.common.target")),
            BasisEditorLocalization.Get("editor.sync.transform.target.tooltip")));
        root.Add(target);

        root.Add(AxisGroup(BasisEditorLocalization.Get("editor.sync.channel.position"), "SyncPosition", "InterpolatePosition", "PositionX", "PositionY", "PositionZ"));
        root.Add(RotationGroup());
        root.Add(ScaleGroup());

        VisualElement space = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.transform.space.card"), false);
        space.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("WorldSpace"), BasisEditorLocalization.Get("editor.sync.transform.worldSpace")),
            BasisEditorLocalization.Get("editor.sync.transform.worldSpace.tooltip")));
        space.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("RelativeTo"), BasisEditorLocalization.Get("editor.sync.relativeTo")),
            BasisEditorLocalization.Get("editor.sync.relativeTo.tooltip")));
        var relHint = new Label(BasisEditorLocalization.Get("editor.sync.transform.relativeToHint"));
        relHint.style.whiteSpace = WhiteSpace.Normal;
        relHint.style.fontSize = 10;
        relHint.style.opacity = 0.8f;
        space.Add(relHint);
        root.Add(space);

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
        var t = target as BasisSyncedTransform;
        if (t == null) return issues;

        if (t.Target == null)
            issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.sync.transform.noTarget")));

        bool posAxes = t.PositionX || t.PositionY || t.PositionZ;
        bool rotAxes = t.RotationX || t.RotationY || t.RotationZ;
        bool scaleAxes = t.ScaleX || t.ScaleY || t.ScaleZ;
        bool rotEuler = t.RotationMode == BasisRotationSyncMode.Euler;

        bool posActive = t.SyncPosition && posAxes;
        bool rotActive = t.SyncRotation && (!rotEuler || rotAxes);
        bool scaleActive = t.SyncScale && (t.ScaleUniform || scaleAxes);

        if (t.SyncPosition && !posAxes)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.transform.positionAxesOff")));
        if (t.SyncRotation && rotEuler && !rotAxes)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.transform.rotationAxesOff")));
        if (t.SyncScale && !t.ScaleUniform && !scaleAxes)
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.transform.scaleAxesOff")));

        if (!(posActive || rotActive || scaleActive))
            issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.transform.nothingSynced")));

        return issues;
    }

    private VisualElement AxisGroup(string title, string syncProp, string interpProp, string x, string y, string z)
    {
        VisualElement card = BasisSyncInspectorUI.Card(title);

        var sync = new Toggle(BasisEditorLocalization.Get("editor.sync.syncPosition")) { bindingPath = syncProp, tooltip = BasisEditorLocalization.Get("editor.sync.transform.syncPosition.tooltip") };
        VisualElement row = AxisRow(x, y, z);
        row.tooltip = BasisEditorLocalization.Get("editor.sync.axisRow.tooltip");
        row.SetEnabled(serializedObject.FindProperty(syncProp).boolValue);
        sync.RegisterValueChangedCallback(evt => row.SetEnabled(evt.newValue));

        card.Add(sync);
        card.Add(row);
        card.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.interpolate")) { bindingPath = interpProp, tooltip = BasisEditorLocalization.Get("editor.sync.transform.interpolatePosition.tooltip") });
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

    private VisualElement RotationGroup()
    {
        VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.channel.rotation"));

        var sync = new Toggle(BasisEditorLocalization.Get("editor.sync.syncRotation")) { bindingPath = "SyncRotation", tooltip = BasisEditorLocalization.Get("editor.sync.transform.syncRotation.tooltip") };
        var mode = new PropertyField(serializedObject.FindProperty("RotationMode"), BasisEditorLocalization.Get("editor.common.mode"));
        mode.tooltip = BasisEditorLocalization.Get("editor.sync.transform.rotationMode.tooltip");
        var bits = new SliderInt(BasisEditorLocalization.Get("editor.sync.smallestThreeBits"), 6, 16) { showInputField = true, bindingPath = "SmallestThreeBits", tooltip = BasisEditorLocalization.Get("editor.sync.smallestThreeBits.tooltip") };
        VisualElement row = AxisRow("RotationX", "RotationY", "RotationZ");
        row.tooltip = BasisEditorLocalization.Get("editor.sync.transform.rotationAxes.tooltip");

        var hint = new Label(BasisEditorLocalization.Get("editor.sync.transform.rotationHint"));
        hint.style.whiteSpace = WhiteSpace.Normal;
        hint.style.fontSize = 10;
        hint.style.opacity = 0.8f;
        hint.style.marginTop = 2;

        void Apply(bool on, int modeIdx)
        {
            mode.SetEnabled(on);
            row.SetEnabled(on && modeIdx == (int)BasisRotationSyncMode.Euler);
            bits.style.display = (on && modeIdx == (int)BasisRotationSyncMode.SmallestThree) ? DisplayStyle.Flex : DisplayStyle.None;
        }
        Apply(serializedObject.FindProperty("SyncRotation").boolValue, serializedObject.FindProperty("RotationMode").enumValueIndex);
        sync.RegisterValueChangedCallback(evt => Apply(evt.newValue, serializedObject.FindProperty("RotationMode").enumValueIndex));
        mode.RegisterValueChangeCallback(evt => Apply(sync.value, serializedObject.FindProperty("RotationMode").enumValueIndex));

        card.Add(sync);
        card.Add(mode);
        card.Add(bits);
        card.Add(row);
        card.Add(hint);
        card.Add(new Toggle(BasisEditorLocalization.Get("editor.sync.interpolate")) { bindingPath = "InterpolateRotation", tooltip = BasisEditorLocalization.Get("editor.sync.interpolateRotation.tooltip") });
        return card;
    }

    private VisualElement ScaleGroup()
    {
        VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.channel.scale"));

        var sync = new Toggle(BasisEditorLocalization.Get("editor.sync.syncScale")) { bindingPath = "SyncScale", tooltip = BasisEditorLocalization.Get("editor.sync.transform.syncScale.tooltip") };
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

    private VisualElement BuildCompression()
    {
        VisualElement foldout = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.compression.card"), false);

        var note = new Label(BasisEditorLocalization.Get("editor.sync.transform.compressionNote"));
        note.style.whiteSpace = WhiteSpace.Normal;
        note.style.fontSize = 10;
        note.style.opacity = 0.8f;
        note.style.marginBottom = 4;
        foldout.Add(note);

        foldout.Add(GroupLabel(BasisEditorLocalization.Get("editor.sync.channel.position")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompX"), "X"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompY"), "Y"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("PosCompZ"), "Z"));

        foldout.Add(GroupLabel(BasisEditorLocalization.Get("editor.sync.transform.compressionRotationGroup")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("RotCompX"), "X"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("RotCompY"), "Y"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("RotCompZ"), "Z"));

        foldout.Add(GroupLabel(BasisEditorLocalization.Get("editor.sync.channel.scale")));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompX"), "X"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompY"), "Y"));
        foldout.Add(BasisSyncInspectorUI.CompressionAxisRow(serializedObject, serializedObject.FindProperty("ScaleCompZ"), "Z"));

        return foldout;
    }

    private static Label GroupLabel(string text)
    {
        var l = new Label(text);
        l.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
        l.style.whiteSpace = WhiteSpace.Normal;
        l.style.marginTop = 6;
        l.style.marginBottom = 2;
        return l;
    }

    private void FillWireSize(BasisWireSizeModel model)
    {
        var t = target as BasisSyncedTransform;
        if (t == null) return;
        model.Checksum = t.UseChecksum;
        model.SendIntervalSeconds = t.SendIntervalSeconds;
        model.KeyframeIntervalSeconds = t.KeyframeIntervalSeconds;

        int posBits = 0, posFields = 0;
        if (t.SyncPosition)
        {
            if (t.PositionX) { posBits += t.PosCompX.ToSpec().BitCount; posFields++; }
            if (t.PositionY) { posBits += t.PosCompY.ToSpec().BitCount; posFields++; }
            if (t.PositionZ) { posBits += t.PosCompZ.ToSpec().BitCount; posFields++; }
        }
        model.Add(BasisEditorLocalization.Get("editor.sync.channel.position"), posBits, posFields, BasisEditorLocalization.Get("editor.sync.wire.axes", posFields));

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
                    if (t.RotationX) { rotBits += t.RotCompX.ToSpec().BitCount; rotFields++; }
                    if (t.RotationY) { rotBits += t.RotCompY.ToSpec().BitCount; rotFields++; }
                    if (t.RotationZ) { rotBits += t.RotCompZ.ToSpec().BitCount; rotFields++; }
                    rotDetail = BasisEditorLocalization.Get("editor.sync.wire.axesEuler", rotFields);
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
