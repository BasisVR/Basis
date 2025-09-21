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

#if UNITY_NGO_MODULE_DEFINED

using System.Reflection;
using Meta.XR.BuildingBlocks;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    [InitializeOnLoad]
    internal static class SceneListenerNGO
    {
        static SceneListenerNGO()
        {
            ObjectChangeEvents.changesPublished += ChangesPublished;
        }

        private static void ChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (var i = 0; i < stream.length; i++)
            {
                ParseEvent(stream, i);
            }
        }

        private static void ParseEvent(ObjectChangeEventStream stream, int i)
        {
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (stream.GetEventType(i))
            {
                case ObjectChangeKind.CreateGameObjectHierarchy:
                    stream.GetCreateGameObjectHierarchyEvent(i, out var createGameObjectHierarchyEvent);
                    ProcessGameObject(
                        EditorUtility.InstanceIDToObject(createGameObjectHierarchyEvent.instanceId) as GameObject);
                    break;
                case ObjectChangeKind.ChangeGameObjectStructure:
                    stream.GetChangeGameObjectStructureEvent(i, out var changeGameObjectStructure);
                    ProcessGameObject(
                        EditorUtility.InstanceIDToObject(changeGameObjectStructure.instanceId) as GameObject);
                    break;
            }
        }

        private static void ProcessGameObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            if (go.GetComponent<BuildingBlock>() == null)
            {
                return;
            }

            RefreshComponents(go.GetComponentInChildren<NetworkObject>());
        }


        private static void RefreshComponents(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                return;
            }

            var previousPrefabIdHash = networkObject.PrefabIdHash;

            // Force OnValidate call to make sure the network object's prefab id is set to the right value
            // Attempt to fix a similar issue to https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/issues/1499
            var onValidateMethod = typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance);
            if (onValidateMethod != null)
            {
                onValidateMethod.Invoke(networkObject, null);
            }

            if (networkObject.PrefabIdHash == previousPrefabIdHash)
            {
                return;
            }

            EditorUtility.SetDirty(networkObject);
            EditorSceneManager.MarkSceneDirty(networkObject.gameObject.scene);
        }
    }
}

#endif // UNITY_NGO_MODULE_DEFINED
