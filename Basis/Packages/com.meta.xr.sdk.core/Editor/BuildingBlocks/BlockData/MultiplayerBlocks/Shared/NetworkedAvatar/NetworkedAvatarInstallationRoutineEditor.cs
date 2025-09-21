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

using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Shared.Editor
{
    [CustomEditor(typeof(NetworkedAvatarInstallationRoutine))]
    public class NetworkedAvatarInstallationRoutineEditor : InstallationRoutineEditor
    {
        private SerializedProperty _networkedAvatarPrefabProperty;

        protected override void OnEnable()
        {
            base.OnEnable();

            _networkedAvatarPrefabProperty = serializedObject.FindProperty(nameof(NetworkedAvatarInstallationRoutine.prefabV28Plus));
        }

        protected override void DrawCustomSection()
        {
            base.DrawCustomSection();

            if (_networkedAvatarPrefabProperty != null)
            {
                EditorGUILayout.PropertyField(_networkedAvatarPrefabProperty);
            }
        }
    }
}
