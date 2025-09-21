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
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.BuildingBlocks.Shared.Editor;
using UnityEngine;

#if UNITY_NGO_MODULE_DEFINED
using Unity.Netcode;
using Unity.Netcode.Components;
#endif // UNITY_NGO_MODULE_DEFINED

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    public class NetworkedGrabbableObjectNGOInstallationRoutine : NetworkedGrabbableObjectInstallationRoutine
    {
        protected override bool UsesPrefab => false;
#pragma warning disable CS1998
        public override async Task<List<GameObject>> InstallAsync(BlockData blockData, GameObject selectedGameObject)
#pragma warning restore CS1998
        {
#if UNITY_NGO_MODULE_DEFINED
            var blocks = await base.InstallAsync(blockData, selectedGameObject);

            foreach (var block in blocks)
            {
                block.AddComponent<NetworkObject>();
                block.AddComponent<ClientNetworkTransform>();
                block.AddComponent<TransferOwnershipNGO>();

                if (!_useGravity) continue;
                if (block.GetComponent<Rigidbody>() == null)
                {
                    throw new InvalidOperationException("Trying to install NetworkedGrabbableObject and UseGravity" +
                                                        "failed: missing Rigidbody component on the game object.");
                }
                // This NGO NetworkRigidbody will set the owner to isKinematic = false, if not using gravity
                // the object will float around which is normally not desired. So we only add it when UseGravity is true.
                block.AddComponent<NetworkRigidbody>();
            }

            return blocks;
#else
            throw new InvalidOperationException("It's required to install the Unity Netcode package to use this component");
#endif // UNITY_NGO_MODULE_DEFINED
        }
        internal override IReadOnlyCollection<InstallationStepInfo> GetInstallationSteps(VariantsSelection selection)
        {
            var installationSteps = new List<InstallationStepInfo>();
            installationSteps.AddRange(base.GetInstallationSteps(selection));
            installationSteps.Add(new InstallationStepInfo(null, $"Add <b>NetworkObject</b> component to the object"));
            installationSteps.Add(new InstallationStepInfo(null, $"Add <b>ClientNetworkTransform</b> component to the object"));
            installationSteps.Add(new InstallationStepInfo(null, $"Add <b>TransferOwnershipNGO</b> component to the object"));
            if (_useGravity)
            {
                installationSteps.Add(new InstallationStepInfo(null, $"Add <b>NetworkRigidbody</b> component to the object"));
            }
            return installationSteps;
        }
    }
}
