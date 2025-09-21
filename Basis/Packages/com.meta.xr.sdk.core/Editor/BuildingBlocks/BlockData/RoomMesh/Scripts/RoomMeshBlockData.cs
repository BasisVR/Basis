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
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class RoomMeshBlockData : BlockData
    {
        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps
        {
            get
            {
                var installationSteps = new List<InstallationStepInfo>
                {
                    new(Utils.GetBlockData(BlockDataIds.CameraRig), "Collects the reference of <b>OVRManager</b> from {0}."),
                    new(null, $"Enables requesting of Scene data access permission on application startup from <b>{nameof(OVRManager)}</b> component.")
                };
                installationSteps.AddRange(base.InstallationSteps);
                return installationSteps;
            }
        }

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
            if (ovrManager)
            {
                ovrManager.requestScenePermissionOnStartup = true;
                EditorUtility.SetDirty(ovrManager);
            }
            return base.InstallRoutine(selectedGameObject);
        }
    }
}
