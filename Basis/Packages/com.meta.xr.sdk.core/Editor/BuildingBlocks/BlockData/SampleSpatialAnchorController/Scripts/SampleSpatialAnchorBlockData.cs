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
using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;

namespace Meta.XR.BuildingBlocks
{
    public class SampleSpatialAnchorBlockData : BlockData
    {
        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps => new List<InstallationStepInfo>
        {
            new(Prefab, "Instantiates a {0} prefab."),
            new(null, $"Names the instantiated prefab to <b>{Utils.BlockPublicTag} {BlockName}</b>."),
            new(Utils.GetBlockData(BlockDataIds.ControllerButtonsMapper), "Finds the reference of {0} block and binds button configurations for spawning, loading, and erasing a spatial anchor.")
        };

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var spatialAnchorControllerGameObject = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            spatialAnchorControllerGameObject.name = $"{Utils.BlockPublicTag} {BlockName}";
            Undo.RegisterCreatedObjectUndo(spatialAnchorControllerGameObject, "Create " + spatialAnchorControllerGameObject.name);

            var spawner = spatialAnchorControllerGameObject.GetComponent<SpatialAnchorSpawnerBuildingBlock>();
            var spatialAnchorLoader = spatialAnchorControllerGameObject.GetComponent<SpatialAnchorLoaderBuildingBlock>();
            var spatialAnchorCore = Utils.GetBlocksWithBaseClassType<SpatialAnchorCoreBuildingBlock>().First();
            var keyBindingHelper = OVRProjectSetupUtils.FindComponentInScene<ControllerButtonsMapper>();

            // Spawn anchor
            var buttonActionCreate = new ControllerButtonsMapper.ButtonClickAction
            {
                Title = "Spawn spatial anchor",
                Button = OVRInput.Button.One,
                ButtonMode = ControllerButtonsMapper.ButtonClickAction.ButtonClickMode.OnButtonUp,
                Callback = new UnityEvent()
            };
            UnityEventTools.AddPersistentListener(buttonActionCreate.Callback, spawner.SpawnSpatialAnchor);
            keyBindingHelper.ButtonClickActions.Add(buttonActionCreate);

            // Load anchors
            var buttonActionLoad = new ControllerButtonsMapper.ButtonClickAction
            {
                Title = "Load and spawn spatial anchors",
                Button = OVRInput.Button.Two,
                ButtonMode = ControllerButtonsMapper.ButtonClickAction.ButtonClickMode.OnButtonUp,
                Callback = new UnityEvent()
            };
            UnityEventTools.AddPersistentListener(buttonActionLoad.Callback, spatialAnchorLoader.LoadAnchorsFromDefaultLocalStorage);
            keyBindingHelper.ButtonClickActions.Add(buttonActionLoad);

            // Erase anchors
            var buttonActionErase = new ControllerButtonsMapper.ButtonClickAction
            {
                Title = "Erase all spatial anchors",
                Button = OVRInput.Button.PrimaryThumbstick,
                ButtonMode = ControllerButtonsMapper.ButtonClickAction.ButtonClickMode.OnButtonUp,
                Callback = new UnityEvent()
            };
            UnityEventTools.AddPersistentListener(buttonActionErase.Callback, spatialAnchorCore.EraseAllAnchors);
            keyBindingHelper.ButtonClickActions.Add(buttonActionErase);

            Undo.RegisterCompleteObjectUndo(keyBindingHelper, $"Controller buttons mapping config.");

            return new List<GameObject> { spatialAnchorControllerGameObject };
        }
    }
}
