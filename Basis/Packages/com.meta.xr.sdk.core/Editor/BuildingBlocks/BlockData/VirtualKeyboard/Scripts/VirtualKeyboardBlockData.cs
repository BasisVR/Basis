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
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class VirtualKeyboardBlockData : BlockData
    {
        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps => new List<InstallationStepInfo>
        {
            new(Prefab, "Instantiates a {0} prefab."), new(null, $"Names the instantiated prefab to <b>{Utils.BlockPublicTag} {BlockName}</b>."),
            new(Utils.GetBlockData(BlockDataIds.ControllerTracking), "Collects the references of <b>left controller</b> and <b>right controller</b> from {0} block."),
            new(null, "Creates the keyboard interactor for <b>left hand</b> and makes it child of <b>left controller</b>'s transform."),
            new(null, "Creates the keyboard interactor for <b>right hand</b> and makes it child of <b>right controller</b>'s transform."),
            new(Utils.GetBlockData(BlockDataIds.CameraRig), "Collects the reference of {0}."),
            new(null, $"Configures the <b>{nameof(OVRControllerHelper)}</b> component's public properties."),
            new(Utils.GetBlockData(BlockDataIds.HandTracking), "Collects the left and right {0} blocks and references them to virtual keyboard's component.")
        };

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var virtualKeyboardGo = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            virtualKeyboardGo.name = $"{Utils.BlockPublicTag} Virtual Keyboard";
            Undo.RegisterCreatedObjectUndo(virtualKeyboardGo, "Create virtual keyboard.");

            var controllerBuildingBlocks = Utils.GetBlocksWithType<OVRControllerHelper>();
            var leftController = controllerBuildingBlocks.First(controller => controller.m_controller == OVRInput.Controller.LTouch);
            var rightController = controllerBuildingBlocks.First(controller => controller.m_controller == OVRInput.Controller.RTouch);

            var interactorPos = new Vector3(0f, 0f, 0.062f);
            var interactorScale = 0.01f * Vector3.one;
            var interactorAnchorLeft = new GameObject
            {
                transform =
                {
                    localPosition = interactorPos,
                    localScale = interactorScale,
                    parent = leftController.gameObject.transform,
                    name = "KeyboardInteractorAnchorLeft"
                }
            };
            Undo.RegisterCreatedObjectUndo(interactorAnchorLeft, "Create left interactor.");

            var interactorAnchorRight = new GameObject
            {
                transform =
                {
                    localPosition = interactorPos,
                    localScale = interactorScale,
                    parent = rightController.gameObject.transform,
                    name = "KeyboardInteractorAnchorRight"
                }
            };
            Undo.RegisterCreatedObjectUndo(interactorAnchorRight, "Create right interactor.");

            var cameraRigBuildingBlock = Utils.GetBlocksWithType<OVRCameraRig>().First();

            var handBuildingBlocks = Utils.GetBlocksWithType<OVRHand>();
            var leftHand = handBuildingBlocks.First(hand => hand.HandType == OVRHand.Hand.HandLeft);
            var rightHand = handBuildingBlocks.First(hand => hand.HandType == OVRHand.Hand.HandRight);


            return new List<GameObject> { virtualKeyboardGo };
        }
    }
}
