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

using Meta.XR.Editor.Settings;
using UnityEditor;

[InitializeOnLoad]
internal class OVREditorStart
{
    private static readonly CustomBool InitSession =
        new OnlyOncePerSessionBool()
        {
            Owner = null,
            Uid = "InitSession",
            SendTelemetry = false
        };

    static OVREditorStart()
    {
        Meta.XR.Editor.Callbacks.InitializeOnLoad.Register(OnEditorReady);
    }

    private static void OnEditorReady()
    {
        if (!OVREditorUtils.IsMainEditor())
        {
            return;
        }

        OVRTelemetryConsent.SetLibrariesConsent(OVRTelemetryConsent.ShareAdditionalData);

        if (InitSession.Value)
        {
            OVRTelemetry.Start(OVRTelemetryConstants.Editor.MarkerId.Start)
                .AddAnnotation(OVRTelemetryConstants.Editor.AnnotationType.UsesProSkin, EditorGUIUtility.isProSkin)
                .Send();
            OVRPlugin.SendEvent("editor_start");
        }
    }
}
