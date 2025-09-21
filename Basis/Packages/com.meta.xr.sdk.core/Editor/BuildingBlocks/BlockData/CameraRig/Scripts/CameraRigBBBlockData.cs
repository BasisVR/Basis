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

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class CameraRigBBBlockData : BlockData
    {

        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps
        {
            get
            {
                var installationSteps = new List<InstallationStepInfo>
                {
                    new(null, "Looks for an existing <b>VR CameraRig</b>."),
                    new(null, "If no CameraRig is found, do the following:")
                };
                installationSteps.AddRange(base.InstallationSteps);
                installationSteps.Add(new InstallationStepInfo(null, "Set <b>OVRManager.trackingOriginType</b> to <i>FloorLevel</i>."));
                return installationSteps;
            }
        }
        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var cameraRig = FindFirstObjectByType<OVRCameraRig>();
            if (cameraRig == null)
            {
                // Instantiate Prefab (will be automatically unpacked)
                var createdObjects = base.InstallRoutine(selectedGameObject);
                cameraRig = createdObjects.FirstOrDefault()?.GetComponent<OVRCameraRig>();

                // Update Manager to ensure Tracking Origin is Floor Level
                var manager = cameraRig?.GetComponent<OVRManager>();
                if (manager != null)
                {
                    manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
                }

                return createdObjects;
            }

#if UNITY_2021
            // Unpack pre-existing prefab, required for Unity 2021
            if (cameraRig != null && PrefabUtility.GetPrefabInstanceStatus(cameraRig.gameObject) != PrefabInstanceStatus.NotAPrefab)
            {
                PrefabUtility.UnpackPrefabInstance(
                    PrefabUtility.GetOutermostPrefabInstanceRoot(cameraRig.gameObject),
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }
#endif

            return new List<GameObject> { cameraRig.gameObject };
        }
    }
}
