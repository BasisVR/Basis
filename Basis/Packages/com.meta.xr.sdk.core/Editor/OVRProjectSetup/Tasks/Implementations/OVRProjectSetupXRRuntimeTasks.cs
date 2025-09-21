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

using UnityEditor;

[InitializeOnLoad]
internal static class OVRProjectSetupXRRuntimeTasks
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Miscellaneous;

    static OVRProjectSetupXRRuntimeTasks()
    {
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            isDone: buildTargetGroup =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager == null)
                    return true;

                return ovrManager.trackingOriginType != OVRManager.TrackingOrigin.Stage;
            },
            message: "Using Stage tracking origin is not recommended for full immersive VR apps, or if " +
                "you want to have a consistent tracking space for mixed reality experiences under all " +
                "the cases of no boundaries (i.e. boundaryless), roomscale boundaries, and stationary boundaries.\n\n" +
                "You can click the 'Apply' button to set the tracking origin to Floor Level, and " +
                "use a Spatial Anchor to achieve that. Click the Documentation link to find more details.",
            url: "https://developers.meta.com/horizon/documentation/unity/unity-spatial-anchors-overview/",
            fix: buildTargetGroup =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager == null) return;
                Undo.RecordObject(ovrManager, "Modify OVRManager.trackingOriginType");
                ovrManager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            },
            fixMessage: "Not using the Stage tracking origin"
        );
    }
}
