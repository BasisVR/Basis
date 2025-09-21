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

namespace Meta.XR.BuildingBlocks.Editor
{
    /// <summary>
    /// An inspector override that shows InstallationRoutine information.
    /// </summary>
    [CustomEditor(typeof(InstallationRoutine), true)]
    public class InstallationRoutineEditor : DataEditor<InstallationRoutine>
    {
        private readonly string _routineInstructions =
            $"<b>InstallationRoutines</b> provides more control on how the <b>{nameof(BuildingBlock)}</b> is being installed." +
            $"\n• Set a name and description to this <b>{nameof(InstallationRoutine)}</b>." +
            $"\n• Link the <b>{nameof(InstallationRoutine)}</b> to a specific block with the <i>{nameof(InstallationRoutine.TargetBlockDataId)}</i>." +
            $"\n• Inherit from <b>{nameof(InstallationRoutine)}</b> if you want to offer customisation on installation.";

        private readonly string _variantInstructions = $"<b>Variants</b> let you customize your Installation Routine." +
                                                       $"\n• Add <b>[{nameof(VariantAttribute)}]</b> to any field in your <b>{nameof(InstallationRoutine)}</b> class." +
                                                       $"\n• <i>Definition</i> variants are used to select the appropriate Installation Routine." +
                                                       $"\n• <i>Parameter</i> variants are used to pass additional information to the Installation Routine." +
                                                       $"\n\nThis Installation Routine's variants are listed below.";

        protected override BlockData BlockData => Data.TargetBlockData;
        protected override string Instructions => _routineInstructions;


        protected override void OnGUIImplementation()
        {
            // Sub-header
            DrawHeader("Information");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(InstallationRoutine.id)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(InstallationRoutine.targetBlockDataId)));
            UIHelpers.DrawBlockRow(BlockData, null, Origins.BlockInspector, BlockData, false);

            DrawHeader("Setup");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(InstallationRoutine.packageDependencies)));
            using (new EditorGUI.DisabledScope(!Data.GetUsesPrefab))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(InstallationRoutine.prefab)));
            }

            DrawCustomSection();

            {
                DrawHeader("Variants");
                DrawInstructions(_variantInstructions);
                DrawVariants("Definition Variants", Data.DefinitionVariants, serializedObject);
                DrawVariants("Parameter Variants", Data.ParameterVariants, serializedObject);
                DrawVariants("Constants", Data.Constants, serializedObject);
            }

        }

        private void DrawGuidedSetupField()
        {
            EditorGUI.BeginChangeCheck();
            Data.selectedGuidedSetupIndex = EditorGUILayout.Popup("Guided Setup", Data.selectedGuidedSetupIndex,
                Utils.GetClassNamesFromAssemblyQualifiedNames(GuideProcessor.GuidedSetupClasses));
            if (EditorGUI.EndChangeCheck())
            {
                Data.guidedSetupName = GuideProcessor.GuidedSetupClasses[Data.selectedGuidedSetupIndex];
                EditorUtility.SetDirty(Data);
            }
        }

        protected virtual void DrawCustomSection()
        {
        }
    }
}
