using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    internal class VLayoutToggleObjects
    {
        private readonly HVRVixxyControl my;
        private readonly SerializedObject serializedObject;

        internal VLayoutToggleObjects(HVRVixxyControlEditor editor)
        {
            my = (HVRVixxyControl)editor.target;
            serializedObject = editor.serializedObject;
        }

        internal bool LayoutToggleObjects()
        {
            EditorGUILayout.Separator();

            var activationsSp = serializedObject.FindProperty(nameof(HVRVixxyControl.activations));
            if (my.IsRegularToggle)
            {
                EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.EnableTheseWhenActiveLabel);
                DisplayActivations(activationsSp, true, false);
                EditorGUILayout.Separator();
                EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.DisableTheseWhenActiveLabel);
                DisplayActivations(activationsSp, false, false);
            }
            else
            {
                for (var choiceIndex = 0; choiceIndex < my.NumberOfChoices; choiceIndex++)
                {
                    if (string.IsNullOrWhiteSpace(my.choices[choiceIndex].title)) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("");
                    if (choiceIndex != 0)
                    {
                        for (var i = 0; i < choiceIndex; i++)
                        {
                            EditorGUILayout.LabelField("", GUILayout.Width(EditorGUIUtility.singleLineHeight));
                        }
                    }
                    EditorGUILayout.LabelField($"#{choiceIndex + 1} {my.choices[choiceIndex].title}", GUILayout.Width(70 + 60 + 25));
                    for (var i = choiceIndex; i < my.NumberOfChoices; i++)
                    {
                        EditorGUILayout.LabelField("", GUILayout.Width(EditorGUIUtility.singleLineHeight));
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.AffectTheseObjectsLabel);
                for (var choiceIndex = 0; choiceIndex < my.NumberOfChoices; choiceIndex++)
                {
                    EditorGUILayout.LabelField($"#{choiceIndex + 1}", GUILayout.Width(EditorGUIUtility.singleLineHeight));
                }
                EditorGUILayout.LabelField("", GUILayout.Width(70));
                EditorGUILayout.LabelField("", GUILayout.Width(60));
                EditorGUILayout.LabelField("", GUILayout.Width(25));
                EditorGUILayout.EndHorizontal();

                DisplayActivations(activationsSp, false, true);
            }
            EditorGUILayout.Separator();

            return false;
        }

        private void DisplayActivations(SerializedProperty activationsSp, bool showThoseActive, bool displayMultiChoice)
        {
            // showThoseActive must be ignored when we have three or more choices

            for (var i = 0; i < activationsSp.arraySize; i++)
            {
                var activationSp = activationsSp.GetArrayElementAtIndex(i);
                var choicesSp = activationSp.FindPropertyRelative(nameof(HVRVixxyActivation.choices));
                var activeChoiceSp = choicesSp.arraySize >= 2 ? choicesSp.GetArrayElementAtIndex(HVRVixxyPropertyBase.ActiveIndex) : null;
                var isWhenActive = activeChoiceSp != null ? activeChoiceSp.boolValue : true;

                var shouldBeDisplayed = isWhenActive == showThoseActive;
                if (displayMultiChoice || shouldBeDisplayed)
                {
                    var componentSp = activationSp.FindPropertyRelative(nameof(HVRVixxyActivation.component));

                    EditorGUILayout.BeginHorizontal();
                    if (componentSp.objectReferenceValue != null && componentSp.objectReferenceValue.GetType() == typeof(Transform))
                    {
                        var t = (Transform)componentSp.objectReferenceValue;
                        var obj = EditorGUILayout.ObjectField(GUIContent.none, t.gameObject, typeof(GameObject));
                        if (obj != t.gameObject && obj != t)
                        {
                            componentSp.objectReferenceValue = obj;
                        }
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(componentSp, GUIContent.none);
                    }

                    if (displayMultiChoice)
                    {
                        for (var choiceIndex = 0; choiceIndex < choicesSp.arraySize; choiceIndex++)
                        {
                            EditorGUILayout.PropertyField(choicesSp.GetArrayElementAtIndex(choiceIndex), GUIContent.none, GUILayout.Width(EditorGUIUtility.singleLineHeight));
                        }
                    }

                    EditorGUILayout.PropertyField(activationSp.FindPropertyRelative(nameof(HVRVixxyActivation.threshold)), GUIContent.none, GUILayout.Width(70));

                    {
                        var allComponents = new [] { HVRVixxyLocalizationPhrase.TypeSelectionLabel }.Concat(((Component)(componentSp.objectReferenceValue)).gameObject
                                .GetComponents<Component>() // GetComponents may contain null values for unloadable MonoBehaviours
                                .Where(component => component != null)
                                // .Where(component => component.GetType() != element.objectReferenceValue.GetType())
                                .Select(component =>
                                {
                                    var name = component.GetType().Name;
                                    return (name == "Transform" ? "GameObject" : name) + (component.GetType() == componentSp.objectReferenceValue.GetType() ? $" {HVRVixxyLocalizationPhrase.CurrentLabel}" : "");
                                })
                                .Distinct())
                            .ToArray();
                        var switching = EditorGUILayout.Popup(0, allComponents, GUILayout.Width(60));
                        if (switching > 0)
                        {
                            var components = ((Component)(componentSp.objectReferenceValue)).gameObject
                                .GetComponents<Component>() // GetComponents may contain null values for unloadable MonoBehaviours
                                .Where(component => component != null)
                                // .Where(component => component.GetType() != element.objectReferenceValue.GetType())
                                .Distinct()
                                .ToArray();
                            componentSp.objectReferenceValue = components[switching - 1];
                        }
                    }

                    if (!displayMultiChoice && GUILayout.Button(HVR_EditorHelpers.SwapSymbol, GUILayout.Width(25)))
                    {
                        choicesSp.GetArrayElementAtIndex(HVRVixxyPropertyBase.InactiveIndex).boolValue = showThoseActive;
                        choicesSp.GetArrayElementAtIndex(HVRVixxyPropertyBase.ActiveIndex).boolValue = !showThoseActive;
                    }

                    if (GUILayout.Button(HVR_EditorHelpers.CrossSymbol, GUILayout.Width(25)))
                    {
                        activationsSp.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        activationsSp.DeleteArrayElementAtIndex(i);
                        return; // Reason why the return is here: Please check the comment in VixenLayoutChangeProperties, look for a DeleteArrayElementAtIndex invocation.
                    }

                    EditorGUILayout.EndHorizontal();

                    if (componentSp.objectReferenceValue != null && !HVR_VixxyPermitted.IsPermitted(componentSp.objectReferenceValue.GetType().FullName))
                    {
                        EditorGUILayout.HelpBox($"The type of the component {componentSp.objectReferenceValue.GetType().Name} is not in the list of allowed types, so it cannot be toggled.", MessageType.Error);
                    }
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(HVR_EditorHelpers.PlusSymbol, GUILayout.Width(15));
            var newComponent = EditorGUILayout.ObjectField(null, typeof(Component), true);
            if (newComponent != null)
            {
                var newIndex = activationsSp.arraySize;
                activationsSp.InsertArrayElementAtIndex(newIndex);
                var newElementSp = activationsSp.GetArrayElementAtIndex(newIndex);
                var choicesSp = newElementSp.FindPropertyRelative(nameof(HVRVixxyActivation.choices));
                choicesSp.ClearArray();
                choicesSp.arraySize = my.NumberOfChoices;
                choicesSp.GetArrayElementAtIndex(HVRVixxyPropertyBase.ActiveIndex).boolValue = showThoseActive;
                choicesSp.GetArrayElementAtIndex(HVRVixxyPropertyBase.InactiveIndex).boolValue = !showThoseActive;
                newElementSp.FindPropertyRelative(nameof(HVRVixxyActivation.component)).objectReferenceValue = newComponent;
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
