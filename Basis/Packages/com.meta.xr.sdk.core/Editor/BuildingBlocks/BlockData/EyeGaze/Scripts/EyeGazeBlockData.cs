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
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class EyeGazeBlockData : BlockData
    {
        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps =>
            new List<InstallationStepInfo>
            {
                new(Utils.GetBlockData(BlockDataIds.CameraRig), "Collects the reference of {0}."),
                new(Prefab, "Instantiates left and right eyes GameObject from {0} and makes them child of CameraRig's left eye anchor and right eye anchor respectively."),
                new(null, $"Renames the instantiated left and right eyes to <b>{Utils.BlockPublicTag} {BlockName} {OVREyeGaze.EyeId.Left.ToString()}</b> and <b>{Utils.BlockPublicTag} {BlockName} {OVREyeGaze.EyeId.Right.ToString()}</b>.")
            };

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var cameraRigBB = Utils.GetBlocksWithType<OVRCameraRig>().First();

            var createdGameObjects = new List<GameObject>
            {
                InstantiateEye(OVREyeGaze.EyeId.Left, cameraRigBB.leftEyeAnchor),
                InstantiateEye(OVREyeGaze.EyeId.Right, cameraRigBB.rightEyeAnchor)
            };

            return createdGameObjects;
        }

        private GameObject InstantiateEye(OVREyeGaze.EyeId eye, Transform parent)
        {
            var gameObject = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            gameObject.SetActive(true);
            gameObject.name = $"{Utils.BlockPublicTag} {BlockName} {eye.ToString()}";
            Undo.RegisterCreatedObjectUndo(gameObject, $"Create {eye.ToString()} eye.");
            Undo.SetTransformParent(gameObject.transform, parent, false, $"{eye.ToString()} eye parent");

            var eyeGaze = gameObject.GetComponentInChildren<OVREyeGaze>();
            if (eyeGaze == null)
            {
                throw new InvalidOperationException("The instantiated prefab should contain a OVREyeGaze component.");
            }

            eyeGaze.Eye = eye;
            return gameObject;
        }
    }
}
