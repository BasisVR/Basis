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
using System.Collections.Generic;
using Meta.XR.Guides.Editor;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class OcclusionBlockData : BlockData
    {

        internal override bool CanBeAddedOverGameObject => true;

        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps
        {
            get
            {
                var matRef = Prefab.GetComponentsInChildren<MeshRenderer>(true).First().sharedMaterial;
                return new List<InstallationStepInfo>
                {
                    new(Utils.GetBlockData(BlockDataIds.Cube), "Collects the reference of selected GameObject, otherwise, adds {0} block to the scene."),
                    new(matRef, "Applies the {0} to the selected GameObject or instantiated Cube.")
                };
            }
        }

        protected override async Task<List<GameObject>> InstallRoutineAsync(GameObject selectedGameObject)
        {
            var gameObjects = await base.InstallRoutineAsync(selectedGameObject);

            if (selectedGameObject == null)
            {
                // Install Dummy Cube
                var cubeBlockData = Utils.GetBlockData(BlockDataIds.Cube);
                var cubeBlockObjects = await cubeBlockData.InstallWithDependencies();

                // Install on Dummy Cube
                selectedGameObject = cubeBlockObjects.First();
            }

            Undo.RegisterFullObjectHierarchyUndo(selectedGameObject, "Apply occlusion.");

            if (!selectedGameObject.TryGetComponent<Renderer>(out var renderer))
            {
                throw new InstallationCancelledException("A Renderer component is missing. Unable to use this surface for occlusion.");
            }
            renderer.sharedMaterial = Prefab.GetComponentsInChildren<MeshRenderer>(true).First().sharedMaterial;

#if XR_OCULUS_4_2_0_OR_NEWER && UNITY_2022_3_OR_NEWER
            return gameObjects;
#else
            Undo.PerformUndo();
            throw new InstallationCancelledException($"Dependencies are not met for {BlockName} block. Requires Oculus XR Plugin 4.2.0, Unity editor version at least 2022.3.1 or 2023.2.");
#endif // XR_OCULUS_4_2_0_OR_NEWER && UNITY_2022_3_OR_NEWER
        }

        protected override Type ComponentType => typeof(OcclusionBuildingBlock);

    }
}
