using System.Collections.Generic;
using System.Linq;
using HVR.Basis.Comms;
using HVR.Basis.Comms.Editor;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    internal class VLayoutSettings
    {
        private const string MenuLabel = "Menu";
        private readonly HVRVixxyControl my;
        private readonly SerializedObject serializedObject;

        private readonly HVRVixxyControlEditor _editor;
        private readonly List<HVRVixxyMenuItem> _outsideMenus = new();

        internal VLayoutSettings(HVRVixxyControlEditor editor)
        {
            _editor = editor;
            my = (HVRVixxyControl)editor.target;
            serializedObject = editor.serializedObject;

            if (my.GetComponent<HVRVixxyMenuItem>() == null)
            {
                var avatar = HVRCommsUtil.GetAvatar(my);
                if (avatar != null)
                {
                    _outsideMenus = avatar.GetComponentsInChildren<HVRVixxyMenuItem>(true)
                        .Where(menu => menu.control == my)
                        .ToList();
                }
            }
        }

        public bool LayoutSettings()
        {
            EditorGUILayout.Separator();

            EditorGUILayout.LabelField(MenuLabel, EditorStyles.boldLabel);
            var menuNullable = my.GetComponent<HVRVixxyMenuItem>();
            EditorGUI.BeginDisabledGroup(true);
            foreach (var outsideMenu in _outsideMenus)
            {
                EditorGUILayout.ObjectField(outsideMenu, typeof(HVRVixxyMenuItem), true);
            }

            if (menuNullable != null)
            {
                EditorGUILayout.ObjectField(menuNullable, typeof(HVRVixxyMenuItem), true);
            }
            EditorGUI.EndDisabledGroup();

            if (_outsideMenus.Count == 0 && menuNullable == null)
            {
                if (GUILayout.Button(HVRVixxyLocalizationPhrase.CreateMenuOnThisControlLabel))
                {
                    var comp = Undo.AddComponent<HVRVixxyMenuItem>(my.gameObject);
                    ComponentUtility.MoveComponentUp(comp);
                }
                if (GUILayout.Button(HVRVixxyLocalizationPhrase.CreateMenuInASeparateGameObjectLabel))
                {
                    var go = new GameObject($"{my.gameObject.name} Menu");
                    Undo.RegisterCreatedObjectUndo(go, HVRVixxyLocalizationPhrase.CreateMenuInASeparateGameObjectLabel);
                    go.transform.SetParent(my.transform);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;

                    var comp = Undo.AddComponent<HVRVixxyMenuItem>(go);
                    comp.control = my;

                    _outsideMenus.Add(comp);
                }
            }
            EditorGUILayout.Separator();

            EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.ControlLabel, EditorStyles.boldLabel);
            HVRVixxyControlEditor.LayoutAddressSelector(serializedObject.FindProperty(nameof(HVRVixxyControl.address)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyControl.networked)));
            if (my.networked)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(HVRVixxyControl.advancedNetworking)));
                if (my.advancedNetworking == HVRVixxyNetworkingType.UpdatedExtremelyFrequently)
                {
                    EditorGUILayout.HelpBox(HVRVixxyLocalizationPhrase.MsgNetworkingUsesHighFrequency, MessageType.Warning);
                }
            }
            EditorGUILayout.Separator();

            {
                EditorGUILayout.LabelField($"{HVRVixxyLocalizationPhrase.ChoicesLabel} ({my.NumberOfChoices})", EditorStyles.boldLabel);
                LayoutDefaultValueSlider();
                var choicesSp = serializedObject.FindProperty(nameof(HVRVixxyControl.choices));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("", GUILayout.Width(30));
                EditorGUILayout.LabelField("Description / Icon / Value");
                EditorGUILayout.LabelField("", GUILayout.Width(20));
                EditorGUILayout.EndHorizontal();

                for (var choiceIndex = 0; choiceIndex < choicesSp.arraySize; choiceIndex++)
                {
                    var choiceSp = choicesSp.GetArrayElementAtIndex(choiceIndex);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"#{choiceIndex + 1}", GUILayout.Width(30));

                    EditorGUILayout.PropertyField(choiceSp.FindPropertyRelative(nameof(HVRVixxyChoiceControl.title)), GUIContent.none);
                    EditorGUILayout.PropertyField(choiceSp.FindPropertyRelative(nameof(HVRVixxyChoiceControl.icon)), GUIContent.none);
                    var valueSp = choiceSp.FindPropertyRelative(nameof(HVRVixxyChoiceControl.value));
                    EditorGUILayout.PropertyField(valueSp, GUIContent.none, GUILayout.Width(30));
                    var choiceValue = valueSp.floatValue;
                    if (HaiEFCommon.ColoredBackground(Mathf.Approximately(my.defaultValue, choiceValue), HVRVixxyControlEditor.FilledColor,
                            () => GUILayout.Button(HVRVixxyLocalizationPhrase.DefaultLabel, GUILayout.Width(60))))
                    {
                        var defaultValueSp = serializedObject.FindProperty(nameof(HVRVixxyControl.defaultValue));
                        defaultValueSp.floatValue = choiceValue;
                    }

                    EditorGUI.BeginDisabledGroup(!my.HasThreeOrMoreChoices);
                    if (GUILayout.Button(HVR_EditorHelpers.CrossSymbol, GUILayout.Width(20)))
                    {
                        _editor.RemoveChoice(choiceIndex);
                        return true;
                    }
                    EditorGUI.EndDisabledGroup();

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("", GUILayout.Width(30));
                if (GUILayout.Button($"{HVR_EditorHelpers.PlusSymbol} {HVRVixxyLocalizationPhrase.AddChoiceLabel}"))
                {
                    _editor.AddChoice();
                    return true;
                }
                EditorGUILayout.LabelField("", GUILayout.Width(20));
                EditorGUILayout.EndHorizontal();

            }
            EditorGUILayout.Separator();

            return false;
        }

        private void LayoutDefaultValueSlider()
        {
            var defaultValueSp = serializedObject.FindProperty(nameof(HVRVixxyControl.defaultValue));
            EditorGUILayout.Slider(defaultValueSp, my.Min(), my.Max());
            if (!my.HasThreeOrMoreChoices)
            {
                if (Mathf.Approximately(my.Min(), 0f) && Mathf.Approximately(my.Max(), 1f))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("", GUILayout.Width(50));
                    if (HaiEFCommon.ColoredBackground(Mathf.Approximately(defaultValueSp.floatValue, 0f), Color.cyan, () => GUILayout.Button(HVRVixxyLocalizationPhrase.InactiveLabel)))
                    {
                        defaultValueSp.floatValue = 0f;
                    }

                    if (HaiEFCommon.ColoredBackground(Mathf.Approximately(defaultValueSp.floatValue, 1f), Color.cyan, () => GUILayout.Button(HVRVixxyLocalizationPhrase.ActiveLabel)))
                    {
                        defaultValueSp.floatValue = 1f;
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }
        }
    }
}
