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
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.MultiplayerBlocks.Shared;
using Meta.XR.MultiplayerBlocks.Shared.Editor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Shared.Editor
{
    public abstract class NetworkedGrabbableObjectInstallationRoutine : NetworkInstallationRoutine
    {
        [SerializeField]
        [Variant(
            Behavior = VariantAttribute.VariantBehavior.Parameter,
            Description = "Whether the target grabbable object has rigidbody and uses gravity for physics simulation.")]
        public bool _useGravity;

#if META_INTERACTION_SDK_DEFINED
        private const string HandGrabBlockId = Oculus.Interaction.Editor.BuildingBlocks.BlockDataIds.HandGrab;
        private const string TouchHandGrabBlockId =
            Oculus.Interaction.Editor.BuildingBlocks.BlockDataIds.TouchHandGrab;
#endif // META_INTERACTION_SDK_DEFINED

#pragma warning disable CS1998
        public override async Task<List<GameObject>> InstallAsync(BlockData blockData, GameObject selectedGameObject)
#pragma warning restore CS1998
        {
#if META_INTERACTION_SDK_DEFINED
            await base.InstallAsync(blockData, selectedGameObject); // get automatchmaking from base
            var handGrabBlockData = Utils.GetBlockData(_useGravity ? TouchHandGrabBlockId : HandGrabBlockId);
            if (selectedGameObject == null)
            {
                var handGrabObject = await handGrabBlockData.InstallWithDependencies();
                // the object HandGrab install onto is the parent
                selectedGameObject = handGrabObject.First().transform.parent.gameObject;
                // we should remove the block metadata added to this object so we can later add Grabbable metadata to it
                var metadata = selectedGameObject.GetComponent<BuildingBlock>();
                if (metadata != null)
                {
                    DestroyImmediate(metadata);
                }
            } else if (selectedGameObject.GetComponentInChildren<Oculus.Interaction.Grabbable>() == null)
            {
                await handGrabBlockData.InstallWithDependencies(selectedGameObject);
            }

            var transferOwnershipOnSelect = selectedGameObject.AddComponent<TransferOwnershipOnSelect>();
            transferOwnershipOnSelect.UseGravity = _useGravity;
            return new List<GameObject> { selectedGameObject };
#else
            throw new InvalidOperationException("It's required to install Meta Interaction SDK package to use this component");
#endif // META_INTERACTION_SDK_DEFINED
        }

#if META_INTERACTION_SDK_DEFINED
        internal override IReadOnlyCollection<InstallationStepInfo> GetInstallationSteps(VariantsSelection selection)
        {
            var installationSteps = new List<InstallationStepInfo>();
            installationSteps.AddRange(base.GetInstallationSteps(selection));
            if (!_useGravity)
            {
                var handGrabBlockData = Utils.GetBlockData(HandGrabBlockId);
                installationSteps.Add(new InstallationStepInfo(handGrabBlockData, "Installs {0}"));
            }
            else
            {
                var touchHandGrabBlockData = Utils.GetBlockData(TouchHandGrabBlockId);
                installationSteps.Add(new InstallationStepInfo(touchHandGrabBlockData, "Installs {0}"));
            }
            return installationSteps;
        }
#endif // META_INTERACTION_SDK_DEFINED
    }
}
