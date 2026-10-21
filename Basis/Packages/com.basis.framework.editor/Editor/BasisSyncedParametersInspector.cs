using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSyncedParameters))]
[CanEditMultipleObjects]
public class BasisSyncedParametersInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            BasisEditorLocalization.Get("editor.sync.parameters.title"),
            BasisEditorLocalization.Get("editor.sync.parameters.description")));

        root.Add(BasisSyncInspectorUI.ValidationContainer(Validate));

        VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.parameters.card"));
        var list = new VisualElement();
        card.Add(list);

        var add = new Button(() =>
        {
            SerializedProperty prop = serializedObject.FindProperty("Parameters");
            serializedObject.Update();
            prop.arraySize++;
            SerializedProperty added = prop.GetArrayElementAtIndex(prop.arraySize - 1);
            added.FindPropertyRelative("Name").stringValue = "Parameter" + prop.arraySize;
            serializedObject.ApplyModifiedProperties();
            RebuildList(list);
        })
        { text = BasisEditorLocalization.Get("editor.sync.parameters.add"), tooltip = BasisEditorLocalization.Get("editor.sync.parameters.add.tooltip") };
        add.style.marginTop = 4;
        card.Add(add);
        root.Add(card);

        RebuildList(list);

        root.Add(BasisSyncInspectorUI.NetworkingCard(serializedObject));
        root.Add(BasisSyncInspectorUI.SmoothingCard(serializedObject));

        root.Bind(serializedObject);

        root.Add(BasisSyncInspectorUI.NetworkInfo(serializedObject.targetObject));

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }

    private void RebuildList(VisualElement list)
    {
        list.Clear();
        SerializedProperty prop = serializedObject.FindProperty("Parameters");
        if (prop == null) return;

        for (int i = 0; i < prop.arraySize; i++)
        {
            list.Add(BuildElement(prop, i, list));
        }

        if (prop.arraySize == 0)
        {
            var empty = new Label(BasisEditorLocalization.Get("editor.sync.parameters.empty"));
            empty.style.unityFontStyleAndWeight = FontStyle.Italic;
            empty.style.opacity = 0.7f;
            list.Add(empty);
        }
    }

    private VisualElement BuildElement(SerializedProperty listProp, int index, VisualElement list)
    {
        SerializedProperty element = listProp.GetArrayElementAtIndex(index);
        SerializedProperty nameProp = element.FindPropertyRelative("Name");
        SerializedProperty typeProp = element.FindPropertyRelative("Type");
        SerializedProperty interpProp = element.FindPropertyRelative("Interpolate");

        var foldout = new Foldout { value = true };
        foldout.style.marginBottom = 6;
        foldout.style.borderLeftWidth = 2;
        foldout.style.borderLeftColor = new StyleColor(BasisSyncInspectorUI.Accent);
        foldout.style.paddingLeft = 4;

        foldout.Add(BasisSyncInspectorUI.Described(new PropertyField(nameProp, BasisEditorLocalization.Get("editor.common.name")),
            BasisEditorLocalization.Get("editor.sync.parameters.name.tooltip")));
        foldout.Add(BasisSyncInspectorUI.Described(new PropertyField(typeProp, BasisEditorLocalization.Get("editor.common.type")),
            BasisEditorLocalization.Get("editor.sync.parameters.type.tooltip")));
        var interpField = BasisSyncInspectorUI.Described(new PropertyField(interpProp, BasisEditorLocalization.Get("editor.sync.interpolate")),
            BasisEditorLocalization.Get("editor.sync.parameters.interpolate.tooltip"));
        foldout.Add(interpField);

        var axisCard = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.sync.compression.card"), false);
        VisualElement[] axes =
        {
            BasisSyncInspectorUI.CompressionAxisRow(serializedObject, element.FindPropertyRelative("AxisX"), "X"),
            BasisSyncInspectorUI.CompressionAxisRow(serializedObject, element.FindPropertyRelative("AxisY"), "Y"),
            BasisSyncInspectorUI.CompressionAxisRow(serializedObject, element.FindPropertyRelative("AxisZ"), "Z"),
            BasisSyncInspectorUI.CompressionAxisRow(serializedObject, element.FindPropertyRelative("AxisW"), "W"),
        };
        foreach (VisualElement a in axes) axisCard.Add(a);
        foldout.Add(axisCard);

        var remove = new Button(() =>
        {
            serializedObject.Update();
            listProp.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            RebuildList(list);
        })
        { text = BasisEditorLocalization.Get("editor.common.remove"), tooltip = BasisEditorLocalization.Get("editor.sync.parameters.remove.tooltip") };
        remove.style.alignSelf = Align.FlexEnd;
        foldout.Add(remove);

        foldout.Bind(serializedObject);

        void Refresh()
        {
            if (typeProp == null) return;
            serializedObject.Update();
            var type = (BasisSyncedParameterType)typeProp.enumValueIndex;
            int comps = BasisSyncedParameters.ComponentCount(type);
            bool discrete = typeProp.enumValueIndex >= (int)BasisSyncedParameterType.Int;

            string name = string.IsNullOrEmpty(nameProp.stringValue) ? BasisEditorLocalization.Get("editor.sync.parameters.unnamed") : nameProp.stringValue;
            foldout.text = $"{name}  ·  {type}";

            interpField.style.display = discrete ? DisplayStyle.None : DisplayStyle.Flex;
            axisCard.style.display = comps > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            for (int a = 0; a < axes.Length; a++)
                axes[a].style.display = a < comps ? DisplayStyle.Flex : DisplayStyle.None;
        }

        Refresh();
        foldout.schedule.Execute(Refresh).Every(300);
        return foldout;
    }

    private List<BasisSyncIssue> Validate()
    {
        var issues = new List<BasisSyncIssue>();
        var c = target as BasisSyncedParameters;
        if (c == null || c.Parameters == null) return issues;

        var seen = new HashSet<string>();
        int empty = 0;
        foreach (BasisSyncedParameter p in c.Parameters)
        {
            if (p == null) continue;
            if (string.IsNullOrWhiteSpace(p.Name)) { empty++; continue; }
            if (!seen.Add(p.Name))
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.sync.parameters.duplicate", p.Name)));
        }
        if (empty > 0) issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.parameters.unnamedSkipped", empty)));
        if (c.Parameters.Count == 0) issues.Add(BasisSyncIssue.Warning(BasisEditorLocalization.Get("editor.sync.parameters.none")));
        return issues;
    }
}
