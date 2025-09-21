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
    public class ControllerTrackingBlockData : BlockData
    {
        protected override bool IsBlockPresentInScene()
        {
            var blocksInScene =
                FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None)
                    .Where(block => block.BlockId == Id)
                    .Select(block => block.GetComponent<OVRControllerHelper>())
                    .Where(controller => controller != null)
                    .ToList();

            return blocksInScene.Any(controller => controller.m_controller == OVRInput.Controller.LTouch)
                   && blocksInScene.Any(controller => controller.m_controller == OVRInput.Controller.RTouch);
        }

        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps => new List<InstallationStepInfo>
        {
            new(Prefab, "Detects whether an <b>OVRControllerHelper</b> component is present for the left hand. Otherwise, instantiates and sets up a {0} GameObject for the left hand."),
            new(Prefab, "Detects whether an <b>OVRControllerHelper</b> component is present for the right hand. Otherwise, instantiates and sets up a {0} GameObject for the right hand.")
        };

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var installedObjects = new List<GameObject>();

            var leftController = InstantiateController(OVRInput.Hand.HandLeft);
            var rightController = InstantiateController(OVRInput.Hand.HandRight);

            if (leftController != null)
            {
                installedObjects.Add(leftController);
            }

            if (rightController != null)
            {
                installedObjects.Add(rightController);
            }

            return installedObjects;

        }
        private GameObject InstantiateController(OVRInput.Hand handedness)
        {
            var controllerType = handedness == OVRInput.Hand.HandLeft
                ? OVRInput.Controller.LTouch
                : OVRInput.Controller.RTouch;

            var cameraRigBB = Utils.GetBlocksWithType<OVRCameraRig>().First();

            var idealParent = handedness == OVRInput.Hand.HandLeft
                ? cameraRigBB.leftControllerAnchor
                : cameraRigBB.rightControllerAnchor;

            // Early out if we can find a pre-existing non block version. It will get blockified
            if (TryGetPreexistingController(cameraRigBB.transform, controllerType, idealParent, out var existingController))
            {
                return existingController.GetComponent<BuildingBlock>() ? null : existingController;
            }

            var controller = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            controller.SetActive(true);

            var handednessName = handedness == OVRInput.Hand.HandLeft ? "Left" : "Right";
            controller.name = $"{Utils.BlockPublicTag} {BlockName} {handednessName}";
            AssignControllerType(controller, controllerType);
            Undo.RegisterCreatedObjectUndo(controller, $"Create {BlockName} {handednessName}");
            Undo.SetTransformParent(controller.transform, idealParent, false, "Parent to camera rig");

            EditorApplication.delayCall += () =>
            {
                AssignControllerType(controller, controllerType);
            };

            return controller;
        }

        private static void AssignControllerType(GameObject controller, OVRInput.Controller controllerType)
        {
            if (controller == null)
            {
                return;
            }

            controller.GetComponent<OVRControllerHelper>().m_controller = controllerType;
        }

        private static bool TryGetPreexistingController(Transform root, OVRInput.Controller controllerType, Transform idealParent, out GameObject existingController)
        {
            existingController = root.GetComponentsInChildren<OVRControllerHelper>()
                .FirstOrDefault(controller => controller.m_controller == controllerType
                && controller.transform.parent == idealParent)?.gameObject;
            return existingController != null;
        }
    }
}
