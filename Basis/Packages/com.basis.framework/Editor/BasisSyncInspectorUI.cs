using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync;

/// <summary>One validation message for a synced-component inspector.</summary>
public struct BasisSyncIssue
{
    public bool IsError;
    public string Message;
    public BasisSyncIssue(bool isError, string message) { IsError = isError; Message = message; }
    public static BasisSyncIssue Error(string message) => new BasisSyncIssue(true, message);
    public static BasisSyncIssue Warning(string message) => new BasisSyncIssue(false, message);
}

/// <summary>One labelled section in a Wire Size readout (e.g. Position, Rotation).</summary>
public struct BasisWireSection
{
    public string Name;
    public int Bits;
    public int Fields;
    public string Detail;
}

/// <summary>Filled by a component inspector each refresh to drive <see cref="BasisSyncInspectorUI.WireSizeCard"/>.</summary>
public sealed class BasisWireSizeModel
{
    public readonly List<BasisWireSection> Sections = new List<BasisWireSection>();
    public bool Checksum;
    public float SendIntervalSeconds = 0.05f;
    public float KeyframeIntervalSeconds = 0.5f;
    public void Add(string name, int bits, int fields, string detail)
        => Sections.Add(new BasisWireSection { Name = name, Bits = bits, Fields = fields, Detail = detail });
}

/// <summary>Shared Basis-styled building blocks for the synced-object / synced-transform inspectors.</summary>
public static class BasisSyncInspectorUI
{
    // One source of truth for the Basis pink and the light/dark split, shared with the IMGUI
    // windows in BasisEditorUI.
    public static Color Accent => BasisEditorUI.Accent;
    private static bool Light => BasisEditorUI.Light;

    private static Color HeaderBg => Light ? new Color(1f, 1f, 1f, 0.60f) : new Color(0f, 0f, 0f, 0.54f);
    private static Color CardBg => Light ? new Color(1f, 1f, 1f, 0.40f) : new Color(0f, 0f, 0f, 0.30f);
    private static Color Subtle => Light ? new Color(0.32f, 0.32f, 0.32f, 1f) : new Color(0.8f, 0.8f, 0.8f, 1f);
    private static Color Strong => Light ? new Color(0.10f, 0.10f, 0.10f, 1f) : Color.white;
    private static Color ErrorBg => Light ? new Color(1f, 0.78f, 0.78f, 0.85f) : new Color(1f, 0.5f, 0.5f, 0.5f);
    private static Color WarnBg => Light ? new Color(0.98f, 0.92f, 0.70f, 0.85f) : new Color(0.651f, 0.631f, 0.051f, 0.5f);

    public static VisualElement Header(string title, string subtitle)
    {
        var box = new VisualElement();
        box.style.marginBottom = 10;
        box.style.paddingTop = 8;
        box.style.paddingBottom = 8;
        box.style.paddingLeft = 10;
        box.style.paddingRight = 10;
        box.style.backgroundColor = new StyleColor(HeaderBg);
        box.style.borderBottomWidth = 3;
        box.style.borderBottomColor = new StyleColor(Accent);
        Round(box, 5);

        var titleLabel = new Label(title);
        titleLabel.style.fontSize = 15;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.color = new StyleColor(Strong);

        var subtitleLabel = new Label(subtitle);
        subtitleLabel.style.fontSize = 11;
        subtitleLabel.style.color = new StyleColor(Subtle);
        subtitleLabel.style.whiteSpace = WhiteSpace.Normal;
        subtitleLabel.style.marginTop = 2;

        box.Add(titleLabel);
        box.Add(subtitleLabel);
        return box;
    }

    public static VisualElement Card(string title) => Card(title, true);

    /// <summary>
    /// A titled section that collapses to a single header bar (like the per-axis Compression dropdown), so a
    /// new user isn't faced with every field at once. Children added to it land in the collapsible body.
    /// Pass <paramref name="expanded"/> = false to start collapsed (advanced / verbose sections).
    /// </summary>
    public static VisualElement Card(string title, bool expanded)
    {
        var foldout = new Foldout { text = title, value = expanded };
        foldout.style.marginBottom = 8;
        foldout.style.paddingTop = 6;
        foldout.style.paddingBottom = 8;
        foldout.style.paddingLeft = 8;
        foldout.style.paddingRight = 8;
        foldout.style.backgroundColor = new StyleColor(CardBg);
        foldout.style.borderBottomWidth = 2;
        foldout.style.borderBottomColor = new StyleColor(Accent);
        Round(foldout, 5);

        Label titleLabel = foldout.Q<Label>();
        if (titleLabel != null)
        {
            titleLabel.style.fontSize = 12;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new StyleColor(Accent);
        }

        // Foldout indents its content by default; pull it back flush with the card padding and add a little
        // breathing room under the header, so a collapsible card reads the same as the old static one.
        VisualElement content = foldout.contentContainer;
        if (content != null)
        {
            content.style.marginLeft = 0;
            content.style.marginTop = 4;
        }
        return foldout;
    }

    /// <summary>Collapsible, live-updating view of every network metadata field on the component (play mode only).</summary>
    public static VisualElement NetworkInfo(UnityEngine.Object target) => BasisNetworkInfoView.Build(target);

    /// <summary>Live validation panel (red errors / yellow warnings) that re-evaluates twice a second.</summary>
    public static VisualElement ValidationContainer(Func<List<BasisSyncIssue>> validate)
    {
        var container = new VisualElement();
        container.style.marginBottom = 4;

        void Refresh()
        {
            container.Clear();
            List<BasisSyncIssue> issues = validate != null ? validate() : null;
            if (issues == null || issues.Count == 0) return;

            var errors = issues.Where(i => i.IsError).Select(i => i.Message).ToList();
            var warnings = issues.Where(i => !i.IsError).Select(i => i.Message).ToList();
            if (errors.Count > 0) container.Add(IssuePanel(errors, true));
            if (warnings.Count > 0) container.Add(IssuePanel(warnings, false));
        }

        Refresh();
        container.schedule.Execute(Refresh).Every(500);
        return container;
    }

    private static VisualElement IssuePanel(List<string> messages, bool error)
    {
        var panel = new VisualElement();
        panel.style.backgroundColor = new StyleColor(error ? ErrorBg : WarnBg);
        panel.style.paddingTop = 5;
        panel.style.paddingBottom = 5;
        panel.style.paddingLeft = 6;
        panel.style.paddingRight = 6;
        panel.style.marginBottom = 6;
        panel.style.borderBottomWidth = 2;
        panel.style.borderBottomColor = new StyleColor(error ? BasisEditorUI.Bad : BasisEditorUI.Warn);
        Round(panel, 5);

        var label = new Label(string.Join("\n", messages.Select(m => "• " + m)));
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        label.style.whiteSpace = WhiteSpace.Normal;
        label.style.color = new StyleColor(Strong);
        panel.Add(label);
        return panel;
    }

    public static VisualElement NetworkingCard(SerializedObject so)
    {
        VisualElement card = Card(BasisEditorLocalization.Get("sdk.cilbox.group.networking"), false);
        card.Add(RateSlider(so));
        card.Add(KeyframeSlider(so));
        card.Add(Described(new PropertyField(so.FindProperty("Delivery"), BasisEditorLocalization.Get("editor.sync.networking.delivery")),
            BasisEditorLocalization.Get("editor.sync.networking.delivery.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("KeyframeDelivery"), BasisEditorLocalization.Get("editor.sync.networking.keyframeDelivery")),
            BasisEditorLocalization.Get("editor.sync.networking.keyframeDelivery.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("UseChecksum"), BasisEditorLocalization.Get("editor.sync.networking.checksum")),
            BasisEditorLocalization.Get("editor.sync.networking.checksum.tooltip")));

        var p2p = new Toggle(BasisEditorLocalization.Get("editor.sync.networking.directP2P")) { bindingPath = "UseDirectP2P", tooltip = BasisEditorLocalization.Get("editor.sync.networking.directP2P.tooltip") };
        var forceP2P = Described(new PropertyField(so.FindProperty("ForceP2POnly"), BasisEditorLocalization.Get("editor.sync.networking.forceP2P")),
            BasisEditorLocalization.Get("editor.sync.networking.forceP2P.tooltip"));
        var overrideP2P = new Toggle(BasisEditorLocalization.Get("editor.sync.networking.overrideP2PRate")) { bindingPath = "OverrideP2PRate", tooltip = BasisEditorLocalization.Get("editor.sync.networking.overrideP2PRate.tooltip") };
        var p2pRate = RateSlider(so, "P2PSendIntervalSeconds", BasisEditorLocalization.Get("editor.sync.networking.p2pSendRate"), 120f);
        p2pRate.tooltip = BasisEditorLocalization.Get("editor.sync.networking.p2pSendRate.tooltip");
        var p2pKey = KeyframeSlider(so, "P2PKeyframeIntervalSeconds", BasisEditorLocalization.Get("editor.sync.networking.p2pKeyframeInterval"));
        p2pKey.tooltip = BasisEditorLocalization.Get("editor.sync.networking.p2pKeyframeInterval.tooltip");

        bool useP2P0 = so.FindProperty("UseDirectP2P").boolValue;
        bool ovr0 = so.FindProperty("OverrideP2PRate").boolValue;
        forceP2P.SetEnabled(useP2P0);
        overrideP2P.SetEnabled(useP2P0);
        p2pRate.SetEnabled(useP2P0 && ovr0);
        p2pKey.SetEnabled(useP2P0 && ovr0);

        p2p.RegisterValueChangedCallback(evt =>
        {
            forceP2P.SetEnabled(evt.newValue);
            overrideP2P.SetEnabled(evt.newValue);
            p2pRate.SetEnabled(evt.newValue && overrideP2P.value);
            p2pKey.SetEnabled(evt.newValue && overrideP2P.value);
        });
        overrideP2P.RegisterValueChangedCallback(evt =>
        {
            p2pRate.SetEnabled(p2p.value && evt.newValue);
            p2pKey.SetEnabled(p2p.value && evt.newValue);
        });

        card.Add(p2p);
        card.Add(forceP2P);
        card.Add(overrideP2P);
        card.Add(p2pRate);
        card.Add(p2pKey);

        card.Add(Described(new PropertyField(so.FindProperty("ContinuousEpsilon"), BasisEditorLocalization.Get("editor.sync.networking.positionDeadband")),
            BasisEditorLocalization.Get("editor.sync.networking.positionDeadband.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("RotationSendThresholdDegrees"), BasisEditorLocalization.Get("editor.sync.networking.rotationDeadband")),
            BasisEditorLocalization.Get("editor.sync.networking.rotationDeadband.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("DistanceReduction"), BasisEditorLocalization.Get("editor.sync.networking.distanceReduction")),
            BasisEditorLocalization.Get("editor.sync.networking.distanceReduction.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("RelevanceCulling"), BasisEditorLocalization.Get("editor.sync.networking.relevanceCulling")),
            BasisEditorLocalization.Get("editor.sync.networking.relevanceCulling.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("RelevanceRadius"), BasisEditorLocalization.Get("editor.sync.networking.relevanceRadius")),
            BasisEditorLocalization.Get("editor.sync.networking.relevanceRadius.tooltip")));
        return card;
    }

    public static VisualElement SmoothingCard(SerializedObject so)
    {
        VisualElement card = Card(BasisEditorLocalization.Get("sdk.visemes.backendSmoothing"), false);
        card.Add(Described(new PropertyField(so.FindProperty("Extrapolate"), BasisEditorLocalization.Get("editor.sync.smoothing.extrapolate")),
            BasisEditorLocalization.Get("editor.sync.smoothing.extrapolate.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("MaxExtrapolationSeconds"), BasisEditorLocalization.Get("editor.sync.smoothing.maxExtrapolation")),
            BasisEditorLocalization.Get("editor.sync.smoothing.maxExtrapolation.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("UseTeleportThreshold"), BasisEditorLocalization.Get("editor.sync.smoothing.useTeleportThreshold")),
            BasisEditorLocalization.Get("editor.sync.smoothing.useTeleportThreshold.tooltip")));
        card.Add(Described(new PropertyField(so.FindProperty("TeleportThreshold"), BasisEditorLocalization.Get("editor.sync.smoothing.teleportThreshold")),
            BasisEditorLocalization.Get("editor.sync.smoothing.teleportThreshold.tooltip")));
        return card;
    }

    /// <summary>Sets a hover description on a control and returns it, for inline use in Add(...).</summary>
    public static T Described<T>(T element, string tooltip) where T : VisualElement
    {
        element.tooltip = tooltip;
        return element;
    }

    /// <summary>Live wire-size readout. The component supplies its section breakdown via <paramref name="compute"/> each refresh.</summary>
    public static VisualElement WireSizeCard(Action<BasisWireSizeModel> compute)
    {
        VisualElement card = Card(BasisEditorLocalization.Get("editor.sync.wire.card"), false);

        var note = new Label(BasisEditorLocalization.Get("editor.sync.wire.note"));
        note.style.whiteSpace = WhiteSpace.Normal;
        note.style.fontSize = 10;
        note.style.opacity = 0.8f;
        note.style.marginBottom = 4;
        card.Add(note);

        var sectionsBox = new VisualElement();
        card.Add(sectionsBox);

        var divider = new VisualElement();
        divider.style.height = 1;
        divider.style.backgroundColor = new StyleColor(Light ? new Color(0f, 0f, 0f, 0.17f) : new Color(1f, 1f, 1f, 0.15f));
        divider.style.marginTop = 4;
        divider.style.marginBottom = 4;
        card.Add(divider);

        Label overhead = WireRow(card, BasisEditorLocalization.Get("editor.sync.wire.overhead"), false);
        Label keyframe = WireRow(card, BasisEditorLocalization.Get("editor.sync.wire.keyframeTotal"), true);
        Label delta = WireRow(card, BasisEditorLocalization.Get("editor.sync.wire.deltaTotal"), true);
        Label bandwidth = WireRow(card, BasisEditorLocalization.Get("editor.sync.wire.bandwidth"), true);

        // A packet past the single-datagram budget cannot go out on an unfragmentable delivery
        // method — at runtime BasisSyncedObject escalates it to ReliableUnordered and logs an error.
        // Surfaced here so it's a schema decision at author time rather than a surprise in a session.
        var oversize = new Label();
        oversize.style.whiteSpace = WhiteSpace.Normal;
        oversize.style.fontSize = 10;
        oversize.style.marginTop = 4;
        oversize.style.color = new StyleColor(BasisEditorUI.Bad);
        oversize.style.display = DisplayStyle.None;
        card.Add(oversize);

        var model = new BasisWireSizeModel();

        void Refresh()
        {
            model.Sections.Clear();
            model.Checksum = false;
            model.SendIntervalSeconds = 0.05f;
            model.KeyframeIntervalSeconds = 0.5f;
            compute(model);

            sectionsBox.Clear();
            int fields = 0, payloadBits = 0;
            for (int i = 0; i < model.Sections.Count; i++)
            {
                BasisWireSection sec = model.Sections[i];
                fields += sec.Fields;
                payloadBits += sec.Bits;
                Label v = WireRow(sectionsBox, sec.Name, false);
                v.text = sec.Fields == 0 ? BasisEditorLocalization.Get("editor.sync.wire.off") : $"{sec.Bits} b  •  {(sec.Bits / 8f):0.##} B   ({sec.Detail})";
            }

            BasisSyncCodec.WireBytes(payloadBits, fields, model.Checksum, out int payloadBytes, out int maskBytes, out int keyframeBytes, out int deltaBytes);
            int checksum = model.Checksum ? BasisSyncCodec.ChecksumSize : 0;

            overhead.text = checksum > 0
                ? BasisEditorLocalization.Get("editor.sync.wire.overheadValueChecksum", BasisSyncCodec.HeaderSize, maskBytes, checksum)
                : BasisEditorLocalization.Get("editor.sync.wire.overheadValue", BasisSyncCodec.HeaderSize, maskBytes);

            keyframe.text = BasisEditorLocalization.Get("editor.sync.wire.keyframeValue", keyframeBytes, payloadBits, payloadBytes);
            delta.text = $"{deltaBytes} B";

            float sendHz = model.SendIntervalSeconds > 0f ? 1f / model.SendIntervalSeconds : 0f;
            float kfHz = model.KeyframeIntervalSeconds > 0f ? 1f / model.KeyframeIntervalSeconds : 0f;
            float bps = deltaBytes * sendHz + Mathf.Max(0, keyframeBytes - deltaBytes) * kfHz;
            bandwidth.text = $"≈ {FormatRate(bps)}  @ {sendHz:0} Hz";
            bandwidth.style.color = new StyleColor(bps > 2000f ? BasisEditorUI.Warn : Accent);

            int budget = Basis.Network.Core.BasisNetworkCommons.MaxUnfragmentedPayload;
            int worst = Mathf.Max(keyframeBytes, deltaBytes);
            bool over = worst > budget;
            oversize.style.display = over ? DisplayStyle.Flex : DisplayStyle.None;
            if (over)
            {
                oversize.text = BasisEditorLocalization.Get("editor.sync.wire.oversize", worst, budget);
            }
        }

        Refresh();
        card.schedule.Execute(Refresh).Every(400);
        return card;
    }

    private static string FormatRate(float bytesPerSec) =>
        bytesPerSec >= 1024f ? $"{bytesPerSec / 1024f:0.0} KB/s" : $"{bytesPerSec:0} B/s";

    private static Label WireRow(VisualElement parent, string key, bool strong)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.paddingTop = 1;
        row.style.paddingBottom = 1;

        var k = new Label(key);
        k.style.flexShrink = 0;
        k.style.whiteSpace = WhiteSpace.NoWrap;
        if (strong)
        {
            k.style.unityFontStyleAndWeight = FontStyle.Bold;
            k.style.color = new StyleColor(Accent);
        }

        var v = new Label("—");
        v.style.flexGrow = 1;
        v.style.whiteSpace = WhiteSpace.Normal;
        v.style.unityTextAlign = TextAnchor.MiddleRight;
        if (strong) v.style.unityFontStyleAndWeight = FontStyle.Bold;
        else v.style.color = new StyleColor(Subtle);

        row.Add(k);
        row.Add(v);
        parent.Add(row);
        return v;
    }

    /// <summary>One per-axis compression control: mode dropdown, Min/Max/Bits when Ranged, plus a live precision/size readout (the "fitness" check).</summary>
    public static VisualElement CompressionAxisRow(SerializedObject so, SerializedProperty axisProp, string axisLabel)
    {
        SerializedProperty modeProp = axisProp.FindPropertyRelative("Mode");
        SerializedProperty minProp = axisProp.FindPropertyRelative("Min");
        SerializedProperty maxProp = axisProp.FindPropertyRelative("Max");
        SerializedProperty bitsProp = axisProp.FindPropertyRelative("Bits");

        var box = new VisualElement();
        box.style.marginBottom = 4;
        box.style.paddingLeft = 4;
        box.style.paddingTop = 2;
        box.style.paddingBottom = 2;

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;

        var label = new Label(axisLabel);
        label.style.minWidth = 26;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        label.style.color = new StyleColor(Accent);

        var modeField = new PropertyField(modeProp, "");
        modeField.style.flexGrow = 1;
        modeField.tooltip = BasisEditorLocalization.Get("editor.sync.compression.mode.tooltip");

        header.Add(label);
        header.Add(modeField);
        box.Add(header);

        var ranged = new VisualElement();
        ranged.style.marginLeft = 26;
        var minmax = new VisualElement();
        minmax.style.flexDirection = FlexDirection.Row;
        var minField = new PropertyField(minProp, BasisEditorLocalization.Get("editor.sync.compression.min"));
        minField.style.flexGrow = 1;
        minField.style.marginRight = 6;
        minField.tooltip = BasisEditorLocalization.Get("editor.sync.compression.min.tooltip");
        var maxField = new PropertyField(maxProp, BasisEditorLocalization.Get("editor.sync.compression.max"));
        maxField.style.flexGrow = 1;
        maxField.tooltip = BasisEditorLocalization.Get("editor.sync.compression.max.tooltip");
        minmax.Add(minField);
        minmax.Add(maxField);
        var bitsField = new SliderInt(BasisEditorLocalization.Get("editor.sync.compression.bits"), 1, 31) { showInputField = true, bindingPath = bitsProp.propertyPath, tooltip = BasisEditorLocalization.Get("editor.sync.compression.bits.tooltip") };
        ranged.Add(minmax);
        ranged.Add(bitsField);

        var fitRow = new VisualElement();
        fitRow.style.flexDirection = FlexDirection.Row;
        fitRow.style.alignItems = Align.Center;
        var stepField = new FloatField(BasisEditorLocalization.Get("editor.sync.compression.fitStep")) { value = 0.001f };
        stepField.style.flexGrow = 1;
        stepField.style.marginRight = 6;
        stepField.tooltip = BasisEditorLocalization.Get("editor.sync.compression.fitStep.tooltip");
        var fitBtn = new Button(() =>
        {
            so.Update();
            float span = maxProp.floatValue - minProp.floatValue;
            float step = stepField.value;
            if (span > 0f && step > 0f)
            {
                bitsProp.intValue = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(span / step + 1f, 2f)), 1, 31);
                so.ApplyModifiedProperties();
            }
        }) { text = BasisEditorLocalization.Get("editor.sync.compression.fitBits"), tooltip = BasisEditorLocalization.Get("editor.sync.compression.fitBits.tooltip") };
        fitRow.Add(stepField);
        fitRow.Add(fitBtn);
        ranged.Add(fitRow);

        box.Add(ranged);

        var info = new Label();
        info.style.marginLeft = 26;
        info.style.fontSize = 10;
        info.style.whiteSpace = WhiteSpace.Normal;
        box.Add(info);

        box.Bind(so);

        void Refresh()
        {
            if (modeProp == null || axisProp == null) return;
            so.Update();
            string modeName = ModeName(modeProp);
            bool isRanged = modeName == "Ranged";
            ranged.style.display = isRanged ? DisplayStyle.Flex : DisplayStyle.None;

            if (isRanged)
            {
                float min = minProp.floatValue, max = maxProp.floatValue;
                int bits = Mathf.Clamp(bitsProp.intValue < 1 ? 1 : bitsProp.intValue, 1, 31);
                float range = max - min;
                if (range <= 0f)
                {
                    info.text = BasisEditorLocalization.Get("editor.sync.compression.minMaxError");
                    info.style.color = new StyleColor(BasisEditorUI.Bad);
                }
                else
                {
                    double step = (double)range / ((1L << bits) - 1L);
                    info.text = BasisEditorLocalization.Get("editor.sync.compression.stepInfo", step, range, bits, bits / 8f);
                    info.style.color = new StyleColor(bits <= 4 ? BasisEditorUI.Warn : Subtle);
                }
            }
            else
            {
                switch (modeName)
                {
                    case "Half": info.text = BasisEditorLocalization.Get("editor.sync.compression.halfInfo"); break;
                    case "Raw": info.text = BasisEditorLocalization.Get("editor.sync.compression.rawInfo"); break;
                    case "Inherit": info.text = BasisEditorLocalization.Get("editor.sync.compression.inheritInfo"); break;
                    default: info.text = ""; break;
                }
                info.style.color = new StyleColor(Subtle);
            }
        }

        Refresh();
        box.schedule.Execute(Refresh).Every(300);
        return box;
    }

    private static string ModeName(SerializedProperty modeProp)
    {
        int idx = modeProp.enumValueIndex;
        string[] names = modeProp.enumNames;
        return (idx >= 0 && idx < names.Length) ? names[idx] : "";
    }

    public static VisualElement RateSlider(SerializedObject so) => RateSlider(so, "SendIntervalSeconds", BasisEditorLocalization.Get("editor.sync.networking.sendRate"), 60f);

    public static VisualElement RateSlider(SerializedObject so, string propName, string label, float maxHz)
    {
        SerializedProperty prop = so.FindProperty(propName);
        float hz = prop.floatValue > 0f ? 1f / prop.floatValue : 20f;

        var slider = new Slider(label, 1f, maxHz) { value = hz, showInputField = true };
        slider.tooltip = BasisEditorLocalization.Get("editor.sync.networking.sendRate.tooltip");
        slider.RegisterValueChangedCallback(evt =>
        {
            float h = Mathf.Clamp(evt.newValue, 1f, maxHz);
            so.Update();
            prop.floatValue = 1f / h;
            so.ApplyModifiedProperties();
        });
        return slider;
    }

    public static VisualElement KeyframeSlider(SerializedObject so) => KeyframeSlider(so, "KeyframeIntervalSeconds", BasisEditorLocalization.Get("editor.sync.networking.keyframeInterval"));

    public static VisualElement KeyframeSlider(SerializedObject so, string propName, string label)
    {
        SerializedProperty prop = so.FindProperty(propName);

        var slider = new Slider(label, 0.1f, 5f) { value = prop.floatValue, showInputField = true };
        slider.tooltip = BasisEditorLocalization.Get("editor.sync.networking.keyframeInterval.tooltip");
        slider.RegisterValueChangedCallback(evt =>
        {
            so.Update();
            prop.floatValue = evt.newValue;
            so.ApplyModifiedProperties();
        });
        return slider;
    }

    private static void Round(VisualElement e, float r)
    {
        e.style.borderTopLeftRadius = r;
        e.style.borderTopRightRadius = r;
        e.style.borderBottomLeftRadius = r;
        e.style.borderBottomRightRadius = r;
    }
}
