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

using Meta.XR.Samples.Telemetry;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Meta.XR.Samples
{
    [ExecuteAlways]
    internal class SampleMetadata : MonoBehaviour
    {
#if UNITY_EDITOR
        private static bool _scriptReloaded;

        [InitializeOnLoadMethod]
        private static void OnScriptReloaded()
        {
            if (!Application.isPlaying)
            {
                _scriptReloaded = true;
                EditorApplication.update += ScriptReloadedDismiss;
            }
        }

        private static void ScriptReloadedDismiss()
        {
            EditorApplication.update -= ScriptReloadedDismiss;
            _scriptReloaded = false;
        }
#endif

        private float _timestampOpen;

        private void Awake()
        {
            _timestampOpen = Time.realtimeSinceStartup;

#if UNITY_EDITOR
            Meta.XR.Editor.Callbacks.Shutdown.editorShutdown += OnEditorShutdown;
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            Meta.XR.Editor.Callbacks.Shutdown.editorShutdown -= OnEditorShutdown;
#endif
        }

        private void Start()
        {
            if (Application.isPlaying)
            {
                SendEvent(SampleTelemetryEvents.EventTypes.Run);
            }
            else
            {
#if UNITY_EDITOR
                if (!_scriptReloaded)
#endif
                {
                    SendEvent(SampleTelemetryEvents.EventTypes.Open);
                }
            }
        }

        public void OnEditorShutdown()
        {
            SendEvent(SampleTelemetryEvents.EventTypes.Close);
        }

        private void SendEvent(int eventType)
        {
            var timeSpent = Time.realtimeSinceStartup - _timestampOpen;
            OVRTelemetry.Start(eventType)
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.Sample, gameObject.scene.name)
#if UNITY_EDITOR
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.BuildTarget, EditorUserBuildSettings.selectedBuildTargetGroup.ToString())
#endif
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.RuntimePlatform, Application.platform.ToString())
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.InEditor, Application.isEditor.ToString())
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.TimeSinceEditorStart, Time.realtimeSinceStartup.ToString("F0"))
                .AddAnnotation(SampleTelemetryEvents.AnnotationTypes.TimeSpent, timeSpent.ToString("F0"))
                .Send();
        }
    }
}
