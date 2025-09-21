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
using Meta.XR.BuildingBlocks;
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.BuildingBlocks.Shared.Editor;
using UnityEngine;
#if FUSION_WEAVER && FUSION2
using Fusion;
#if PHOTON_FUSION_PHYSICS_ADDON_DEFINED
using Fusion.Addons.Physics;
#endif // PHOTON_FUSION_PHYSICS_ADDON_DEFINED
#endif // FUSION_WEAVER && FUSION2

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    public class NetworkedGrabbableObjectFusionInstallationRoutine : NetworkedGrabbableObjectInstallationRoutine
    {
        protected override bool UsesPrefab => false;
#pragma warning disable CS1998
        public override async Task<List<GameObject>> InstallAsync(BlockData blockData, GameObject selectedGameObject)
#pragma warning restore CS1998
        {
#if FUSION_WEAVER && FUSION2
            var blocks = await base.InstallAsync(blockData, selectedGameObject);
            var visualGos = new List<GameObject>();

            foreach (var block in blocks)
            {
                var networkObject = block.AddComponent<NetworkObject>();
                networkObject.Flags |= NetworkObjectFlags.AllowStateAuthorityOverride;
                block.AddComponent<TransferOwnershipFusion>();
                // at this stage, the base installationRoutine have the selected object returned.
                // and the selected object can be null if dropping to scene directly so we use block as the target here.

#if PHOTON_FUSION_PHYSICS_ADDON_DEFINED
                ConfigureComponents(block);
#else
                var networkTransform = block.AddComponent<NetworkTransform>();
                networkTransform.DisableSharedModeInterpolation = true; // otherwise object cannot be grabbed by ISDK
#endif // PHOTON_FUSION_PHYSICS_ADDON_DEFINED
            }

            return blocks;
#else
            throw new InvalidOperationException("It's required to install the Photon Fusion package to use this component");
#endif // FUSION_WEAVER && FUSION2
        }


#if FUSION_WEAVER && FUSION2 && PHOTON_FUSION_PHYSICS_ADDON_DEFINED
        private void ConfigureComponents(GameObject destinationObject)
        {
            ExtractVisualsToChild(destinationObject, out var visualGo);
            if (visualGo == null)
            {
                return;
            }
            // This works for objects no matter they're using gravity or not
            var networkRigidBody = destinationObject.AddComponent<NetworkRigidbody3D>();
            networkRigidBody.InterpolationTarget = visualGo.transform;
        }

        private void ExtractVisualsToChild(GameObject destinationObject, out GameObject visualGo)
        {
            var visualComp = destinationObject.GetComponentInChildren<MeshFilter>();
            if (visualComp == null)
            {
                Debug.LogWarning("Target object doesn't contain visual components like MeshFilter/MeshRenderer");
                visualGo = null;
                return;
            }

            visualGo = visualComp.gameObject;
            if (visualGo == destinationObject)
            {
                // move visual components to Visual child
                visualGo = new GameObject("Visual")
                {
                    transform =
                    {
                        parent = destinationObject.transform,
                        localPosition = Vector3.zero,
                        localRotation = Quaternion.identity,
                        localScale = Vector3.one
                    }
                };
                var meshFilter = destinationObject.GetComponent<MeshFilter>();
                // some fields cannot be accessed in Editor time so skipping
                CopyComponentValues(meshFilter, visualGo, new List<string>{"name","mesh"});
                var meshRenderer = destinationObject.GetComponent<MeshRenderer>();
                CopyComponentValues(meshRenderer, visualGo, new List<string>{"name","materials", "material"});
                DestroyImmediate(meshFilter);
                DestroyImmediate(meshRenderer);
            }
        }

        private void CopyComponentValues(Component comp, GameObject destinationObject, List<string> skipFields)
        {
            Type type = comp.GetType();
            Component newComp = destinationObject.AddComponent(type);

            System.Reflection.FieldInfo[] fields = type.GetFields();
            foreach (var field in fields)
            {
                if(!skipFields.Contains(field.Name))
                {
                    try
                    {
                        field.SetValue(newComp, field.GetValue(comp));
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }

            System.Reflection.PropertyInfo[] props = type.GetProperties();
            foreach (var prop in props)
            {
                if (prop.CanWrite && !skipFields.Contains(prop.Name))
                {
                    try
                    {
                        prop.SetValue(newComp, prop.GetValue(comp));
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }

        }
#endif // FUSION_WEAVER && FUSION2 && PHOTON_FUSION_PHYSICS_ADDON_DEFINED

        internal override IReadOnlyCollection<InstallationStepInfo> GetInstallationSteps(VariantsSelection selection)
        {
            var installationSteps = new List<InstallationStepInfo>();
            installationSteps.AddRange(base.GetInstallationSteps(selection));
            installationSteps.Add(new InstallationStepInfo(null, $"Add <b>NetworkObject</b> component to the object"));
            installationSteps.Add(new InstallationStepInfo(null, $"Add <b>TransferOwnershipFusion</b> component to the object"));
#if PHOTON_FUSION_PHYSICS_ADDON_DEFINED
            installationSteps.Add(new InstallationStepInfo(null, $"Install <b>NetworkRigidbody3D</b> component to the object with mesh components extracted as a child game object"));
#else
            installationSteps.Add(new InstallationStepInfo(null, $"Install <b>NetworkTransform</b> component to the object"));
#endif // PHOTON_FUSION_PHYSICS_ADDON_DEFINED
            return installationSteps;
        }
    }
}
