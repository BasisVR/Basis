using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HVR.Basis.Comms;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Vixxy.Editor
{
    internal class VLayoutChangeProperties
    {
        private const int MaxSearchQueryLength = 100;
        private const string ObjectGroupIndicator = "◆";
        private static readonly Regex HasAnyNonLetterNonSpace = new Regex(@"[^A-Za-z\s]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly HVRVixxyControl my;
        private readonly SerializedObject serializedObject;
        private readonly ReorderableList subjectsReorderableList;

        private Object _previousRootObject;
        private readonly Dictionary<Type, int> _typeToWhichOpened = new();
        private List<Type> _types;
        private List<string> _blendshapes;
        private Dictionary<MaterialPropertyType, List<string>> _materialProperties;

        private string _search;
        private bool _focusNext;
        private List<Type> _nonTypes;

        internal VLayoutChangeProperties(HVRVixxyControlEditor editor)
        {
            my = (HVRVixxyControl)editor.target;
            serializedObject = editor.serializedObject;

            // reference: https://blog.terresquall.com/2020/03/creating-reorderable-lists-in-the-unity-inspector/
            subjectsReorderableList = new ReorderableList(
                serializedObject,
                serializedObject.FindProperty(nameof(HVRVixxyControl.subjects)),
                true, true, true, true
            );
            subjectsReorderableList.drawElementCallback = SubjectsListElement;
            subjectsReorderableList.drawHeaderCallback =
                rect => EditorGUI.LabelField(rect, HVRVixxyLocalizationPhrase.ObjectGroupsLabel);
            subjectsReorderableList.onAddCallback = list =>
            {
                ++list.serializedProperty.arraySize;
                var newIndex = list.serializedProperty.arraySize - 1;
                var element = list.serializedProperty.GetArrayElementAtIndex(newIndex);
                element.FindPropertyRelative(nameof(HVRVixxySubject.selection)).intValue = (int)HVRVixxySelection.Normal;
                element.FindPropertyRelative(nameof(HVRVixxySubject.targets)).arraySize = 0;
                element.FindPropertyRelative(nameof(HVRVixxySubject.childrenOf)).arraySize = 0;
                element.FindPropertyRelative(nameof(HVRVixxySubject.exceptions)).arraySize = 0;
                element.FindPropertyRelative(nameof(HVRVixxySubject.properties)).arraySize = 0;
                serializedObject.ApplyModifiedProperties();
                subjectsReorderableList.index = newIndex;
            };
            subjectsReorderableList.elementHeight = EditorGUIUtility.singleLineHeight * 1;
        }

        public bool LayoutChangePropertiesPart()
        {
            EditorGUILayout.Separator();
            subjectsReorderableList.DoLayoutList();
            var selectedIndex = subjectsReorderableList.index;
            if (selectedIndex != -1 && selectedIndex < subjectsReorderableList.count)
            {
                EditorGUILayout.BeginVertical(HVR_EditorHelpers.GroupBoxStyle);
                var subjectSp = subjectsReorderableList.serializedProperty.GetArrayElementAtIndex(selectedIndex);
                var mySelectedElement = my.subjects[selectedIndex];

                EditorGUILayout.LabelField($"{HVRVixxyLocalizationPhrase.ObjectGroupLabel} {ObjectGroupIndicator}{selectedIndex + 1}", EditorStyles.boldLabel);

                EditorGUILayout.PropertyField(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.selection)));
                if (mySelectedElement.selection == HVRVixxySelection.Normal)
                {
                    EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.ChangeTheseObjectsLabel);
                    CreateArrayAddition(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.targets)), typeof(GameObject));
                }
                else if (mySelectedElement.selection == HVRVixxySelection.RecursiveSearch)
                {
                    EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.ChangeTheseObjectsAndTheirChildrenLabel);
                    CreateArrayAddition(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.childrenOf)),
                        typeof(GameObject));

                    EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.DoNotChangeTheseObjectsLabel);
                    CreateArrayAddition(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.exceptions)),
                        typeof(GameObject));
                }
                else if (mySelectedElement.selection == HVRVixxySelection.Everything)
                {
                    EditorGUILayout.HelpBox(HVRVixxyLocalizationPhrase.MsgEverthingInContext, MessageType.Info);

                    EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.DoNotChangeTheseObjectsLabel);
                    CreateArrayAddition(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.exceptions)),
                        typeof(GameObject));
                }

                EditorGUILayout.Separator();

                var validTargets = mySelectedElement.targets
                    .Where(o => o != null)
                    .Distinct()
                    .ToArray();

                EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.PropertiesLabel, EditorStyles.boldLabel);
                if (mySelectedElement.selection != HVRVixxySelection.Normal || mySelectedElement.targets.Length > 1)
                {
                    EditorGUILayout.LabelField(HVRVixxyLocalizationPhrase.SampleFromLabel);
                    EditorGUI.BeginDisabledGroup(mySelectedElement.selection == HVRVixxySelection.Normal);
                    CreateArrayAddition(subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.targets)), typeof(GameObject), true);
                    EditorGUI.EndDisabledGroup();
                }

                var propertiesSp = subjectSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));
                if (validTargets.Length > 0)
                {
                    var targetObject = validTargets.First();
                    var rootObject = targetObject;

                    if (_previousRootObject != rootObject || _types == null)
                    {
                        _previousRootObject = rootObject;
                        (_types, _nonTypes) = targetObject.GetComponents<Component>()
                            .Where(component => component != null)
                            .Select(component => component.GetType())
                            .Distinct()
                            .Partition(type => HVR_VixxyPermitted.IsPermitted(type.FullName));
                        _types.Remove(typeof(Transform));
                        _types.Add(typeof(Transform));

                        foreach (var type in _types)
                        {
                            _typeToWhichOpened[type] = -1;
                        }

                        // Note to self: the "<SkinnedMeshRenderer>() is { } smr" syntax can return true even if there is no SkinnedMeshRenderer, so don't use that.
                        var foundSmr = targetObject.TryGetComponent<SkinnedMeshRenderer>(out var smr);
                        if (foundSmr)
                        {
                            _blendshapes = HVR_EditorHelpers.ListAllBlendshapes(smr);
                        }
                        else
                        {
                            _blendshapes = null;
                        }

                        var foundRenderer = targetObject.TryGetComponent<Renderer>(out var renderer);
                        if (foundRenderer)
                        {
                            _materialProperties = HVR_EditorHelpers.ListMostMaterialProperties(renderer);
                        }
                        else
                        {
                            _materialProperties = null;
                        }
                    }

                    foreach (var type in _types)
                    {
                        GUILayout.BeginVertical(HVR_EditorHelpers.GroupBoxStyle);
                        DisplayComponentBox(type, targetObject, subjectSp, mySelectedElement, rootObject);

                        for (var propertyIndex = 0; propertyIndex < propertiesSp.arraySize; propertyIndex++)
                        {
                            var propertySp = propertiesSp.GetArrayElementAtIndex(propertyIndex);
                            if (propertySp != null) // SerializeReference
                            {
                                var fullClassNameSp = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.fullClassName));
                                if (fullClassNameSp != null) // SerializeReference
                                {
                                    var fullClassName = fullClassNameSp.stringValue;

                                    if (fullClassName != type.FullName) continue;
                                    if (DrawPropertyOrReturn(targetObject, propertySp, propertyIndex, propertiesSp)) return true;
                                }
                            }
                        }

                        GUILayout.EndVertical();
                    }

                    if (_nonTypes.Count > 0)
                    {
                        EditorGUILayout.HelpBox("The types of the following components are not in the list of allowed types, so they cannot be modified nor toggled.", MessageType.Info);
                        foreach (var nonType in _nonTypes)
                        {
                            EditorGUI.BeginDisabledGroup(true);
                            DisplayComponentObjectField(targetObject, nonType);
                            EditorGUI.EndDisabledGroup();
                        }
                    }
                }

                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.Separator();

            return false;
        }

        private bool DrawPropertyOrReturn(GameObject targetObject, SerializedProperty propertySp, int propertyIndex, SerializedProperty propertiesSp)
        {
            EditorGUILayout.BeginVertical(HVR_EditorHelpers.GroupBoxStyle);
            EditorGUILayout.BeginHorizontal();
            var managedReferenceValue = propertySp.managedReferenceValue;
            var managedReferenceValueType = managedReferenceValue.GetType();

            var inheritsFromVixxyProperty = false;
            Type genericType = null;
            {
                var currentType = managedReferenceValueType;
                while (currentType != null && currentType != typeof(object))
                {
                    if (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(HVRVixxyProperty<>))
                    {
                        inheritsFromVixxyProperty = true;
                        if (genericType == null)
                        {
                            genericType = currentType.GetGenericArguments()[0];
                        }
                        break;
                    }
                    currentType = currentType.BaseType;
                }
            }

            var variantSp = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.variant));
            if (inheritsFromVixxyProperty)
            {
                if (variantSp.intValue == (int)HVRVixxyPropertyVariant.BlendShape)
                {
                    EditorGUILayout.LabelField("Blendshape", EditorStyles.boldLabel, GUILayout.Width(100));
                }
                else
                {
                    var genericTypeName = managedReferenceValueType.Name;
                    if (genericTypeName.StartsWith("HVRVixxyProperty")) genericTypeName = genericTypeName.Substring("HVRVixxyProperty".Length);
                    EditorGUILayout.LabelField($"{genericTypeName}", EditorStyles.boldLabel, GUILayout.Width(100));
                }
            }
            else if (managedReferenceValue is HVRVixxyPropertyBase)
            {
                EditorGUILayout.LabelField($"Specialized Property of type {managedReferenceValueType.Name}", EditorStyles.boldLabel);
            }
            else
            {
                EditorGUILayout.LabelField($"CAUTION: Not a HVRVixxyPropertyBase, type is {managedReferenceValueType.FullName}", EditorStyles.boldLabel);
            }

            if (variantSp.intValue != (int)HVRVixxyPropertyVariant.BlendShape)
            {
                EditorGUI.BeginDisabledGroup(true);
                // EditorGUILayout.PropertyField(propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.fullClassName)));
                EditorGUILayout.PropertyField(variantSp, GUIContent.none);
                EditorGUI.EndDisabledGroup();
            }

            EditorGUI.BeginDisabledGroup(managedReferenceValue is HVRVixxyPropertyBase { variant: HVRVixxyPropertyVariant.Standard });
            EditorGUILayout.PropertyField(propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.propertyName)), GUIContent.none);
            EditorGUI.EndDisabledGroup();

            {
                var currentFullClassName = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.fullClassName)).stringValue;

                var previousPropertyIndex = -1;
                var nextPropertyIndex = -1;
                for (var i = 0; i < propertiesSp.arraySize; i++)
                {
                    if (i == propertyIndex) continue;
                    var otherPropertySp = propertiesSp.GetArrayElementAtIndex(i);
                    if (otherPropertySp == null) continue;
                    var otherFullClassNameSp = otherPropertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.fullClassName));
                    if (otherFullClassNameSp == null) continue;
                    if (otherFullClassNameSp.stringValue != currentFullClassName) continue;

                    if (i < propertyIndex)
                    {
                        previousPropertyIndex = i;
                    }
                    else if (i > propertyIndex && nextPropertyIndex == -1)
                    {
                        nextPropertyIndex = i;
                    }
                }

                EditorGUI.BeginDisabledGroup(previousPropertyIndex == -1);
                if (GUILayout.Button(HVR_EditorHelpers.ArrowUpSymbol, GUILayout.Width(HVR_EditorHelpers.SwapElementWidth)))
                {
                    propertiesSp.MoveArrayElement(propertyIndex, previousPropertyIndex);
                }
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(nextPropertyIndex == -1);
                if (GUILayout.Button(HVR_EditorHelpers.ArrowDownSymbol, GUILayout.Width(HVR_EditorHelpers.SwapElementWidth)))
                {
                    propertiesSp.MoveArrayElement(propertyIndex, nextPropertyIndex);
                }
                EditorGUI.EndDisabledGroup();
            }

            if (GUILayout.Button($"{HVR_EditorHelpers.CrossSymbol}", GUILayout.Width(HVR_EditorHelpers.DeleteButtonWidth)))
            {
                propertiesSp.DeleteArrayElementAtIndex(propertyIndex);

                serializedObject.ApplyModifiedProperties();
                return true; // Workaround array size change error
            }

            EditorGUILayout.EndHorizontal();

            if (managedReferenceValueType == typeof(HVRVixxyPropertyColor)
                && managedReferenceValue is HVRVixxyPropertyColor color
                && color.propertyName.ToLowerInvariant().Contains("hdr"))
            {
                EditorGUILayout.HelpBox("If you need HDR colors, then delete this property and add it again, but click on HDR when adding the property.", MessageType.Warning);
            }

            if (inheritsFromVixxyProperty)
            {
                var choicesSp = propertySp.FindPropertyRelative(nameof(HVRVixxyProperty<object>.choices));

                var choices = my.choices;
                if (choicesSp.arraySize < my.NumberOfChoices)
                {
                    choicesSp.arraySize = my.NumberOfChoices;
                }

                for (var choiceIndex = 0; choiceIndex < my.NumberOfChoices; choiceIndex++)
                {
                    EditorGUILayout.BeginHorizontal();
                    var choiceElementSp = choicesSp.GetArrayElementAtIndex(choiceIndex);

                    var description = HVRVixxyControlEditor.EditorChoiceDescription(choiceIndex, choices);
                    if (managedReferenceValueType == typeof(HVRVixxyPropertyColorHDR))
                    {
                        var from = choiceElementSp.colorValue;
                        var to = EditorGUILayout.ColorField(new GUIContent(description), from, true, true, true);
                        if (from != to)
                        {
                            choiceElementSp.colorValue = to;
                        }
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(choiceElementSp, new GUIContent(description));
                    }

                    if (inheritsFromVixxyProperty)
                    {
                        if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Download-Available").image, HVRVixxyLocalizationPhrase.SampleValueFromSceneLabel), GUILayout.Width(25)))
                        {
                            if (HVR_EditorHelpers.TryCaptureProperty(targetObject, managedReferenceValue, propertySp, out var capturedValue))
                            {
                                TryApplyProperty(capturedValue, choiceElementSp);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (managedReferenceValueType == typeof(HVRVixxyPropertyColor))
            {
                EditorGUILayout.PropertyField(propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyColor.interpolation)));
            }
            if (managedReferenceValueType == typeof(HVRVixxyPropertyColorHDR))
            {
                EditorGUILayout.PropertyField(propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyColorHDR.interpolation)));
            }
            if (managedReferenceValueType == typeof(HVRVixxyPropertyQuaternion))
            {
                var interpolationSp = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyColor.interpolation));
                EditorGUILayout.PropertyField(interpolationSp);
                if (interpolationSp.intValue == (int)HVRVixxyPropertyQuaternionInterpolation.Euler)
                {
                    // https://docs.unity3d.com/6000.4/Documentation/Manual/AnimationRotate.html#:~:text=if%20the%20rotation%20is%20greater%20than%20360%20degrees%2C%20the%20GameObject%20rotates%20fully
                    EditorGUILayout.HelpBox("Euler interpolation allows rotations greater than 180 degrees (for example, 720 degrees).", MessageType.Info);
                }
            }

            EditorGUILayout.EndVertical();
            return false;
        }

        private static void TryApplyProperty(object capturedValue, SerializedProperty choiceElementSp)
        {
            if (capturedValue is Color colorValue) { choiceElementSp.colorValue = colorValue; }
            else if (capturedValue is float floatValue) { choiceElementSp.floatValue = floatValue; }
            else if (capturedValue is int intValue) { choiceElementSp.intValue = intValue; }
            else if (capturedValue is Quaternion quaternionValue) { choiceElementSp.vector3Value = quaternionValue.eulerAngles; } // !!!
            else if (capturedValue is Vector4 vector4Value) { choiceElementSp.vector4Value = vector4Value; }
            else if (capturedValue is Vector3 vector3Value) { choiceElementSp.vector3Value = vector3Value; }
            else if (capturedValue is Vector2 vector2Value) { choiceElementSp.vector2Value = vector2Value; }
            else if (capturedValue is Object objectValue) { choiceElementSp.objectReferenceValue = objectValue; }
        }

        private static void CreateArrayAddition(SerializedProperty whichArrayProperty, Type arrayType, bool limitToOne = false)
        {
            var useComponentDropdown = arrayType == typeof(Component);
            for (var i = 0; i < (limitToOne ? Math.Min(1, whichArrayProperty.arraySize) : whichArrayProperty.arraySize); i++)
            {
                EditorGUILayout.BeginHorizontal();
                var element = whichArrayProperty.GetArrayElementAtIndex(i);

                if (useComponentDropdown && element.objectReferenceValue != null && element.objectReferenceValue.GetType() == typeof(Transform))
                {
                    var t = (Transform)element.objectReferenceValue;
                    var obj = EditorGUILayout.ObjectField(GUIContent.none, t.gameObject, typeof(GameObject), true);
                    if (obj != t.gameObject && obj != t)
                    {
                        element.objectReferenceValue = obj;
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(element, GUIContent.none);
                }

                if (useComponentDropdown && element.objectReferenceValue != null)
                {
                    var allComponents = new []{ "Type..." }.Concat(((Component)(element.objectReferenceValue)).gameObject
                        .GetComponents<Component>() // GetComponents may contain null values for unloadable MonoBehaviours
                        .Where(component => component != null)
                        // .Where(component => component.GetType() != element.objectReferenceValue.GetType())
                        .Select(component =>
                        {
                            var name = component.GetType().Name;
                            return (name == "Transform" ? "GameObject" : name) + (component.GetType() == element.objectReferenceValue.GetType() ? " (current)" : "");
                        })
                        .Distinct())
                        .ToArray();
                    var switching = EditorGUILayout.Popup(0, allComponents, GUILayout.Width(60));
                    if (switching > 0)
                    {
                        var components = ((Component)(element.objectReferenceValue)).gameObject
                            .GetComponents<Component>() // GetComponents may contain null values for unloadable MonoBehaviours
                            .Where(component => component != null)
                            // .Where(component => component.GetType() != element.objectReferenceValue.GetType())
                            .Distinct()
                            .ToArray();
                        element.objectReferenceValue = components[switching - 1];
                    }
                }

                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button(HVR_EditorHelpers.ArrowUpSymbol, GUILayout.Width(HVR_EditorHelpers.SwapElementWidth)))
                {
                    whichArrayProperty.MoveArrayElement(i, i - 1);
                }
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(i == whichArrayProperty.arraySize - 1);
                if (GUILayout.Button(HVR_EditorHelpers.ArrowDownSymbol, GUILayout.Width(HVR_EditorHelpers.SwapElementWidth)))
                {
                    whichArrayProperty.MoveArrayElement(i, i + 1);
                }
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button(HVR_EditorHelpers.CrossSymbol, GUILayout.Width(HVR_EditorHelpers.DeleteButtonWidth)))
                {
                    whichArrayProperty.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    whichArrayProperty.DeleteArrayElementAtIndex(i);
                    return; // Reason why the return is here: Please check the comment in VixenLayoutChangeProperties, look for a DeleteArrayElementAtIndex invocation.
                }

                EditorGUILayout.EndHorizontal();
            }

            if (!limitToOne || whichArrayProperty.arraySize == 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(HVR_EditorHelpers.PlusSymbol, GUILayout.Width(15));
                var newAddition = EditorGUILayout.ObjectField(GUIContent.none, null, arrayType, true);
                EditorGUILayout.LabelField(GUIContent.none, GUILayout.Width(25));
                EditorGUILayout.EndHorizontal();
                if (newAddition != null)
                {
                    var newIndex = whichArrayProperty.arraySize;
                    whichArrayProperty.InsertArrayElementAtIndex(newIndex);
                    whichArrayProperty.GetArrayElementAtIndex(newIndex).objectReferenceValue = newAddition;
                }
            }
        }

        private void DisplayComponentBox(Type targetedType, GameObject targetObject,
            SerializedProperty selectedElementSp, HVRVixxySubject mySelectedElement, GameObject rootObject)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(true);
            var componentNullable = DisplayComponentObjectField(targetObject, targetedType);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            NEW(targetedType, selectedElementSp, mySelectedElement, rootObject, componentNullable);
        }

        private void NEW(Type targetedType, SerializedProperty selectedElementSp, HVRVixxySubject mySelectedElement, GameObject rootObject, Component componentNullable)
        {
            var isRenderer = typeof(Renderer).IsAssignableFrom(targetedType);
            var isSkinnedMeshRenderer = targetedType == typeof(SkinnedMeshRenderer);

            EditorGUILayout.BeginHorizontal();
            _typeToWhichOpened[targetedType] = GUILayout.Toolbar(_typeToWhichOpened[targetedType], new[]
            {
                HVRVixxyLocalizationPhrase.JustPropertiesLabel,
                isRenderer ? HVRVixxyLocalizationPhrase.MaterialLabel : null,
                isSkinnedMeshRenderer ? HVRVixxyLocalizationPhrase.BlendshapesLabel : null,
            }.Where(s => s != null).ToArray());

            EditorGUI.BeginDisabledGroup(_typeToWhichOpened[targetedType] == -1);
            if (GUILayout.Button("_", GUILayout.Width(25)))
            {
                _typeToWhichOpened[targetedType] = -1;
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            var showProperties = _typeToWhichOpened[targetedType] == 0;
            var showMaterials = isRenderer && _typeToWhichOpened[targetedType] == 1;
            var showBlendshapes = isSkinnedMeshRenderer && _typeToWhichOpened[targetedType] == 2;

            if (_typeToWhichOpened[targetedType] != -1)
            {
                GUI.SetNextControlName("search");
                _search = EditorGUILayout.TextField(HVRVixxyLocalizationPhrase.SearchLabel, _search);
                if (_focusNext)
                {
                    _focusNext = false;
                    EditorGUI.FocusTextInControl("search");
                }
            }

            var hasSearch = !string.IsNullOrEmpty(_search) && _search.Length < MaxSearchQueryLength && (_search.Length >= 3 || (_search.Length == 2 && HasAnyNonLetterNonSpace.IsMatch(_search)) || _search.StartsWith(" "));

            if (showProperties)
            {
                List<VProp> props = new List<VProp>()
                    // .Concat(targetedType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    //     .Where(field => !field.IsInitOnly && !field.IsLiteral)
                    //     .Select(field => new VProp
                    //     {
                    //         name = field.Name,
                    //         type = field.FieldType,
                    //         isPermitted = HVRVixxyPermitted.AllowArbitraryFieldAccess || HVRVixxyPermitted.IsStandardAccessPermitted(targetedType.FullName, field.Name),
                    //     }))
                    .Concat(targetedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                        .Where(prop => prop.CanWrite)
                        .Select(prop =>
                        {
                            var type = prop.PropertyType;
                            return new VProp
                            {
                                name = prop.Name,
                                type = type,
                                isPermitted = HVR_VixxyPermitted.AllowArbitraryPropertyAccess || HVR_VixxyPermitted.IsStandardAccessPermitted(targetedType.FullName, prop.Name),
                                isStoredTypeSupported = IsStoredTypeSupported(type)
                            };
                        }))
                    .Distinct()
                    .OrderBy(prop => !prop.isPermitted)
                    .ThenBy(prop => !prop.isStoredTypeSupported)
                    .ThenBy(prop => prop.type.FullName)
                    .ThenBy(tuple => tuple.name)
                    .ToList();

                foreach (var prop in props)
                {
                    if (!prop.isPermitted && !HVRVixxyControlEditor._developerViewFoldout) continue;

                    EditorGUI.BeginDisabledGroup(!prop.isPermitted);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.TextField(prop.name);
                    if (prop.type.IsEnum)
                    {
                        EditorGUILayout.TextField($"enum {prop.type.Name}");
                    }
                    else
                    {
                        if (prop.isStoredTypeSupported)
                        {
                            EditorGUILayout.TextField(prop.type.Name);
                        }
                        else
                        {
                            EditorGUILayout.TextField($"{prop.type.Name} (not supported)");
                        }
                    }

                    if (GUILayout.Button(HVR_EditorHelpers.PlusSymbol, GUILayout.Width(HVR_EditorHelpers.PlusButtonWidth)))
                    {
                        var managedReferenceValue = ToPropertyOrNull(targetedType, prop);
                        if (managedReferenceValue != null)
                        {
                            var propertiesSp = selectedElementSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));

                            var indexToPutData = propertiesSp.arraySize;
                            propertiesSp.arraySize = indexToPutData + 1;
                            propertiesSp.GetArrayElementAtIndex(indexToPutData).managedReferenceValue = managedReferenceValue;
                        }
                    }

                    EditorGUILayout.EndHorizontal();
                    EditorGUI.EndDisabledGroup();
                }
            }

            if (showBlendshapes && _blendshapes != null)
            {
                void AddBlendshape(string blendshape)
                {
                    var propertiesSp = selectedElementSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));

                    var indexToPutData = propertiesSp.arraySize;
                    propertiesSp.arraySize = indexToPutData + 1;
                    propertiesSp.GetArrayElementAtIndex(indexToPutData).managedReferenceValue = new HVRVixxyPropertyFloat
                    {
                        fullClassName = targetedType.FullName,
                        variant = HVRVixxyPropertyVariant.BlendShape,
                        propertyName = blendshape,
                    };
                }
                if (GUILayout.Button($"+ {HVRVixxyLocalizationPhrase.AddArbitraryBlendshapeLabel}"))
                {
                    AddBlendshape("");
                }
                foreach (var blendshape in _blendshapes)
                {
                    if (hasSearch && !IsSearchMatch(blendshape)) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.TextField(blendshape);
                    if (GUILayout.Button(HVRVixxyLocalizationPhrase.AddLabel, GUILayout.Width(60)))
                    {
                        AddBlendshape(blendshape);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (showMaterials && _materialProperties != null)
            {
                foreach (var mptypeToProperties in _materialProperties)
                {
                    if (mptypeToProperties.Value.Count <= 0) continue;

                    EditorGUILayout.LabelField(mptypeToProperties.Key.ToString(), EditorStyles.boldLabel);

                    foreach (var materialProperty in mptypeToProperties.Value)
                    {
                        if (hasSearch && !IsSearchMatch(materialProperty)) continue;

                        var mptype = mptypeToProperties.Key;

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.TextField(materialProperty);
                        if (GUILayout.Button(mptype == MaterialPropertyType.Vector ? "Vector4" : HVRVixxyLocalizationPhrase.AddLabel, GUILayout.Width(55)))
                        {
                            var propertiesSp = selectedElementSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));

                            var indexToPutData = propertiesSp.arraySize;
                            propertiesSp.arraySize = indexToPutData + 1;
                            propertiesSp.GetArrayElementAtIndex(indexToPutData).managedReferenceValue = GenerateProperty(mptype, targetedType, materialProperty);
                        }
                        if (mptype == MaterialPropertyType.Vector
                            && GUILayout.Button("HDR", GUILayout.Width(55)))
                        {
                            var propertiesSp = selectedElementSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));

                            var indexToPutData = propertiesSp.arraySize;
                            propertiesSp.arraySize = indexToPutData + 1;
                            propertiesSp.GetArrayElementAtIndex(indexToPutData).managedReferenceValue = new HVRVixxyPropertyColorHDR
                            {
                                fullClassName = targetedType.FullName,
                                variant = HVRVixxyPropertyVariant.MaterialProperty,
                                propertyName = materialProperty,
                            };
                        }
                        if (mptype == MaterialPropertyType.Vector
                            && GUILayout.Button("Color", GUILayout.Width(55)))
                        {
                            var propertiesSp = selectedElementSp.FindPropertyRelative(nameof(HVRVixxySubject.properties));

                            var indexToPutData = propertiesSp.arraySize;
                            propertiesSp.arraySize = indexToPutData + 1;
                            propertiesSp.GetArrayElementAtIndex(indexToPutData).managedReferenceValue = new HVRVixxyPropertyColor
                            {
                                fullClassName = targetedType.FullName,
                                variant = HVRVixxyPropertyVariant.MaterialProperty,
                                propertyName = materialProperty,
                            };
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }

        private static object ToPropertyOrNull(Type targetedType, VProp prop)
        {
            var memberInfo = prop.type;
            if (memberInfo == typeof(string))
            {
                return new HVRVixxyPropertyString
                {
                    fullClassName = targetedType.FullName,
                    variant = HVRVixxyPropertyVariant.Standard,
                    propertyName = prop.name,
                };
            }
            if (memberInfo == typeof(Quaternion))
            {
                return new HVRVixxyPropertyQuaternion
                {
                    fullClassName = targetedType.FullName,
                    variant = HVRVixxyPropertyVariant.Standard,
                    propertyName = prop.name,
                };
            }
            if (memberInfo == typeof(Vector3))
            {
                return new HVRVixxyPropertyVector3
                {
                    fullClassName = targetedType.FullName,
                    variant = HVRVixxyPropertyVariant.Standard,
                    propertyName = prop.name,
                };
            }
            return null;
        }

        private static bool IsStoredTypeSupported(Type type)
        {
            return type == typeof(float)
                   || type == typeof(bool)
                   || type == typeof(int)
                   || type == typeof(short)
                   || type == typeof(string)
                   || type == typeof(Material)
                   || type == typeof(Mesh)
                   || type == typeof(Vector2)
                   || type == typeof(Vector3)
                   || type == typeof(Vector4)
                   || type == typeof(Vector2Int)
                   || type == typeof(Vector3Int)
                   || type == typeof(Color)
                   || type == typeof(Color32)
                   || type == typeof(Quaternion)
                   || type.IsEnum
                   || type == typeof(Material[]);
        }

        private static HVRVixxyPropertyBase GenerateProperty(MaterialPropertyType mptype, Type targetedType, string materialProperty)
        {
            switch (mptype)
            {
                case MaterialPropertyType.Float:
                    return new HVRVixxyPropertyFloat
                    {
                        fullClassName = targetedType.FullName,
                        variant = HVRVixxyPropertyVariant.MaterialProperty,
                        propertyName = materialProperty,
                    };
                case MaterialPropertyType.Int:
                    return new HVRVixxyPropertyInt
                    {
                        fullClassName = targetedType.FullName,
                        variant = HVRVixxyPropertyVariant.MaterialProperty,
                        propertyName = materialProperty,
                    };
                case MaterialPropertyType.Vector:
                    return new HVRVixxyPropertyVector4
                    {
                        fullClassName = targetedType.FullName,
                        variant = HVRVixxyPropertyVariant.MaterialProperty,
                        propertyName = materialProperty,
                    };
                case MaterialPropertyType.Texture:
                    return new HVRVixxyPropertyTexture
                    {
                        fullClassName = targetedType.FullName,
                        variant = HVRVixxyPropertyVariant.MaterialProperty,
                        propertyName = materialProperty,
                    };
                case MaterialPropertyType.Matrix:
                case MaterialPropertyType.ConstantBuffer:
                case MaterialPropertyType.ComputeBuffer:
                default:
                    throw new ArgumentOutOfRangeException(nameof(mptype), mptype, null);
            }
        }

        private static Component DisplayComponentObjectField(GameObject targetObject, Type targetedType)
        {
            Component componentNullable = null;
            if (typeof(Component).IsAssignableFrom(targetedType))
            {
                var didFindComp = targetObject.TryGetComponent(targetedType, out var foundComp);
                componentNullable = didFindComp ? foundComp : null;
                EditorGUILayout.ObjectField(foundComp, targetedType, true);
            }
            else if (targetedType == typeof(GameObject))
            {
                EditorGUILayout.ObjectField(targetObject, typeof(GameObject), true);
            }
            else
            {
                EditorGUILayout.TextField(targetedType.Name);
            }

            return componentNullable;
        }

        private bool IsSearchMatch(string isPropertyNameMatch)
        {
            var propertyName = isPropertyNameMatch.ToLowerInvariant();
            return (_search ?? "").ToLowerInvariant().Split(' ').All(needle => propertyName.Contains(needle));
        }

        private void SubjectsListElement(Rect rect, int index, bool isactive, bool isfocused)
        {
            var element = subjectsReorderableList.serializedProperty.GetArrayElementAtIndex(index);

            // EditorGUI.PropertyField(
            // new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight),
            // element.FindPropertyRelative(nameof(HVRVixxySubject.targets))
            // );
            var subject = my.subjects[index];
            EditorGUI.LabelField(
                new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight),
                ComputeLabel(index, subject)
            );
        }

        private static string ComputeLabel(int index, HVRVixxySubject subject)
        {
            // FIXME: This is an expensive way to avoid showing ghost classes
            var mainTargetFullClassNames = subject.targets != null && subject.targets.Length > 0 && subject.targets[0] != null ? subject.targets[0].GetComponents<Component>()
                // GetComponents may contain null values for unloadable MonoBehaviours
                .Where(component => component != null)
                .Select(component => component.GetType().FullName)
                .Distinct()
                .ToArray() : Array.Empty<string>();

            var classNames = subject.properties
                .Where(property => property != null) // SerializeReference
                .Select(property => property.fullClassName)
                .Distinct()
                .Where(fullClassName => mainTargetFullClassNames.Any(existingFullClassNamesInTarget => fullClassName == existingFullClassNamesInTarget))
                .Select(s => s.LastIndexOf('.') != -1 ? s.Substring(s.LastIndexOf('.') + 1) : s)
                .ToArray();

            if (subject.selection == HVRVixxySelection.Normal)
            {
                var targetNames = subject.targets.Where(o => o != null).Select(o => o.name).ToArray();
                return
                    ObjectGroupIndicator + $"{index + 1} {(targetNames.Length > 1 ? $"[{targetNames.Length} objects] " : "")}{string.Join(", ", targetNames)} ({string.Join(", ", classNames)})";
            }
            else
            {
                var label = subject.selection == HVRVixxySelection.RecursiveSearch ? HVRVixxyLocalizationPhrase.RecursiveSearchLabel : HVRVixxyLocalizationPhrase.EverythingLabel;
                return ObjectGroupIndicator + $"{index + 1} {label}: {(classNames.Length > 1 ? $"[{classNames.Length} types] " : "")}{string.Join(", ", classNames)}";
            }
        }
    }

    internal class VProp
    {
        public string name;
        public Type type;
        public bool isPermitted;
        public bool isStoredTypeSupported;
    }
}
