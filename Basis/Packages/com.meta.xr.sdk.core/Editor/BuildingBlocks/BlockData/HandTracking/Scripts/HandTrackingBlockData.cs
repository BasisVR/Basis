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
    public class HandTrackingBlockData : BlockData
    {
        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps
        {
            get
            {
                var cameraBlockData = Utils.GetBlockData(BlockDataIds.CameraRig);
                return new List<InstallationStepInfo>
                {
                    new(cameraBlockData, "Collects the reference of {0}."),
                    new(Prefab, "Instantiates {0} prefab for left hand."),
                    new(null, $"Sets left hand's configuration to <b>{nameof(OVRHand)}</b>, <b>{nameof(OVRSkeleton)}</b>, and <b>{nameof(OVRMesh)}</b> components."),
                    new(Prefab, "Instantiates {0} prefab for right hand."),
                    new(null, $"Sets right hand's configuration to <b>{nameof(OVRHand)}</b>, <b>{nameof(OVRSkeleton)}</b>, and <b>{nameof(OVRMesh)}</b> components.")
                };
            }
        }

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            var skeletonVersion = OVRRuntimeSettings.Instance.HandSkeletonVersion;

            var cameraRigBB = Utils.GetBlocksWithType<OVRCameraRig>().First();

            var leftHand = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            leftHand.SetActive(true);
            leftHand.name = $"{Utils.BlockPublicTag} {BlockName} left";
            Undo.RegisterCreatedObjectUndo(leftHand, "Create left hand.");
            Undo.SetTransformParent(leftHand.transform, cameraRigBB.leftHandAnchor, false, "Parent to camera rig.");

            leftHand.GetComponent<OVRHand>().HandType = OVRHand.Hand.HandLeft;
            leftHand.GetComponent<OVRSkeleton>().SetSkeletonType(OVRHand.Hand.HandLeft.AsSkeletonType(skeletonVersion));
            leftHand.GetComponent<OVRMesh>().SetMeshType(OVRHand.Hand.HandLeft.AsMeshType(skeletonVersion));

            var rightHand = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            rightHand.SetActive(true);
            rightHand.name = $"{Utils.BlockPublicTag} {BlockName} right";
            Undo.RegisterCreatedObjectUndo(rightHand, "Create right hand.");
            Undo.SetTransformParent(rightHand.transform, cameraRigBB.rightHandAnchor, false, "Parent to camera rig.");

            rightHand.GetComponent<OVRHand>().HandType = OVRHand.Hand.HandRight;
            rightHand.GetComponent<OVRSkeleton>().SetSkeletonType(OVRHand.Hand.HandRight.AsSkeletonType(skeletonVersion));
            rightHand.GetComponent<OVRMesh>().SetMeshType(OVRHand.Hand.HandRight.AsMeshType(skeletonVersion));

            return new List<GameObject> { leftHand, rightHand };
        }
    }
}
