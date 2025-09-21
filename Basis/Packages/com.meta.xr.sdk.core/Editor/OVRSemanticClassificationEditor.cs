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

using System;
using UnityEditor;


[CustomEditor(typeof(OVRSemanticClassification))]
[Obsolete(OVRSceneManager.DeprecationMessage)]
public class OVRSemanticClassificationEditor : UnityEditor.Editor
{
    OVRSemanticClassification _object;
    MonoScript _script;

    private void OnEnable()
    {
        _object = (OVRSemanticClassification)target;
        _script = MonoScript.FromMonoBehaviour(_object);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ObjectField("Script", _script, GetType(), false);

            var labels = _object.Labels.Count != 0 ?
                string.Join(",", _object.Labels) : "(None)";
            EditorGUILayout.TextField(nameof(_object.Labels), labels);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
