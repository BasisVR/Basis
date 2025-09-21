/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Meta.XR.Editor.Id;
using Meta.XR.Guides.Editor;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.BuildingBlocks.Editor
{
    /// <summary>
    /// An inspector override that shows BlockData information.
    /// </summary>
    [CustomEditor(typeof(BlockData), true)]
    public class BlockDataEditor : DataEditor<BlockData>
    {
        private ReorderableList _dependencyList;
        private readonly string _blockInstructions = $"<b>{nameof(BlockData)}s</b> let you describe a block and its installation routine." +
                                                     $"\n• Setup the Block here to have it populated in the <b>{Utils.BlocksPublicName}</b> tool." +
                                                     $"\n• Check out the documentation for more information on best practices and how to customize your block.";

        private readonly string _routineInstructions = $"When inheriting from <b>{nameof(InterfaceBlockData)}</b>, you can setup your own custom <b>{nameof(InstallationRoutine)}s</b>." +
                                                       $"\n• <b>{nameof(InstallationRoutine)}</b> are directly linked to <b>{nameof(BlockData)}s</b>, those that are linked to this block id will be listed below.";

        protected override string Instructions => _blockInstructions;
        protected override BlockData BlockData => Data;

        protected override void OnEnable()
        {
            base.OnEnable();

            var depProperty = serializedObject.FindProperty(nameof(BlockData.dependencies));
            _dependencyList = new ReorderableList(serializedObject, depProperty)
            {
                drawElementCallback = DrawListItems,
                drawHeaderCallback = rect => { EditorGUI.LabelField(rect, "Dependencies", EditorStyles.boldLabel); },
                // default behaviour is to duplicate last element when Add button clicked, overriding as empty
                onAddDropdownCallback = (rect, list) =>
                {
                    list.serializedProperty.arraySize++;
                    list.index = list.serializedProperty.arraySize - 1;
                    list.serializedProperty.GetArrayElementAtIndex(list.index).stringValue = "";
                }
            };
        }

        void DrawListItems(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty element = _dependencyList.serializedProperty.GetArrayElementAtIndex(index);
            var blockData = Utils.GetBlockData(element.stringValue);
            var obj = EditorGUI.ObjectField(rect, "", blockData, typeof(BlockData), false) as BlockData;
            if (obj != blockData & obj != null)
            {
                var deps = serializedObject.FindProperty(nameof(BlockData.dependencies));
                deps.GetArrayElementAtIndex(index).stringValue = obj.Id;
            }
        }


        protected override void OnGUIImplementation()
        {
            DrawHeader("Information");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.blockName)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.description)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.thumbnail)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.tags)));
            UIHelpers.DrawBlockRow(Data, null, Origins.BlockInspector, Data, false);

            DrawHeader("Setup");
            _dependencyList.DoLayoutList();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.externalBlockDependencies)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.packageDependencies)));
            using (new EditorGUI.DisabledScope(!Data.GetUsesPrefab))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlockData.prefab)));
            }


            DrawInstallationRoutines();

        }

        private void DrawGuidedSetupField()
        {
            EditorGUI.BeginChangeCheck();

            BlockData.selectedGuidedSetupIndex = EditorGUILayout.Popup("Guided Setup", BlockData.selectedGuidedSetupIndex,
                Utils.GetClassNamesFromAssemblyQualifiedNames(GuideProcessor.GuidedSetupClasses));

            if (EditorGUI.EndChangeCheck())
            {
                BlockData.GuidedSetupName = GuideProcessor.GuidedSetupClasses[BlockData.selectedGuidedSetupIndex];
                EditorUtility.SetDirty(BlockData);
            }
        }


        private void DrawInstallationRoutines()
        {
            if (serializedObject.targetObject is not InterfaceBlockData interfaceBlockData) return;

            DrawHeader("Installation Routines");
            DrawInstructions(_routineInstructions);

            var installationRoutines = interfaceBlockData.GetAvailableInstallationRoutines();
            foreach (var installationRoutine in installationRoutines)
            {
                DrawInstallRoutine(installationRoutine, null, 12.0f);
            }
        }

        public static void DrawInstallRoutine(InstallationRoutine installationRoutine, Color? pillColor, float offset)
        {
            EditorGUILayout.BeginHorizontal();
            if (pillColor.HasValue)
            {
                using (new Meta.XR.Editor.UserInterface.Utils.ColorScope(ColorScope.Scope.Background, pillColor.Value))
                {
                    EditorGUILayout.BeginHorizontal(Styles.GUIStyles.PillBox);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.BeginVertical(GUIStyles.ContentBox);

            EditorGUILayout.BeginHorizontal();
            var foldout = Foldout(installationRoutine, installationRoutine.name, offset);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(installationRoutine, typeof(InstallationRoutine), true);
            }
            EditorGUILayout.EndHorizontal();

            if (foldout)
            {
                using (new IndentScope(EditorGUI.indentLevel + 1))
                {
                    using (new IndentScope(EditorGUI.indentLevel + 1))
                    {
                        DrawVariants("Definition Variants", installationRoutine.DefinitionVariants, null);
                        DrawVariants("Parameter Variants", installationRoutine.ParameterVariants, null);
                        DrawVariants("Constants", installationRoutine.Constants, null);
                    }
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }
    }
}
