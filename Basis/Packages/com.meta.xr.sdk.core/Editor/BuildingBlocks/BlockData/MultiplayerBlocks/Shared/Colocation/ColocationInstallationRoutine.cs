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

using Meta.XR.BuildingBlocks.Editor;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Shared.Editor
{
    public class ColocationInstallationRoutine : NetworkInstallationRoutine
    {
        [SerializeField]
        [Variant(Behavior = VariantAttribute.VariantBehavior.Definition,
            Description = "[Recommended] Use Bluetooth & WiFi based ColocationSession API (with Local Matchmaking block) " +
                          "to colocate players, then use networking framework for gameplay network sync.",
            Default = true)]
        private bool useColocationSession = true; // if this is chosen, don't need to show matchmaking options
        protected override bool CanInstallMatchmaking() => !useColocationSession;

#if META_MR_UTILITY_KIT_DEFINED
        [SerializeField]
        [Variant(Behavior = VariantAttribute.VariantBehavior.Parameter,
            Description = "[Recommended] Share the host's space to guests colocated in the same room so " +
                          "guests don't need to setup rooms. Only available when using colocation session",
            Condition = nameof(CanShareRoomToGuests))]
        private bool shareSpaceToGuests = true; // can only be chosen when useColocationSession enabled
        private bool CanShareRoomToGuests() => useColocationSession;
#endif // META_MR_UTILITY_KIT_DEFINED

        protected override bool RequireNetworkImplementationChoice() => !useColocationSession;
        internal override IEnumerable<BlockData> ComputeOptionalDependencies()
        {
            var dependencies = useColocationSession ?
                new[] { Utils.GetBlockData(BlockDataIds.LocalMatchmaking) } :
                new[] { Utils.GetBlockData(BlockDataIds.PlatformInit) }.Concat(base.ComputeOptionalDependencies());

#if META_MR_UTILITY_KIT_DEFINED
            dependencies = dependencies.Append(
                useColocationSession && shareSpaceToGuests ?
                    Utils.GetBlockData(BlockDataIds.MRUK) :
                    Utils.GetBlockData(BlockDataIds.SharedSpatialAnchorCore));
#else
            dependencies = dependencies.Append(Utils.GetBlockData(BlockDataIds.SharedSpatialAnchorCore));
#endif // META_MR_UTILITY_KIT_DEFINED
            return dependencies;
        }

#if META_MR_UTILITY_KIT_DEFINED
        public async override Task<List<GameObject>> InstallAsync(BlockData block, GameObject selectedGameObject)
        {
            var objs = await base.InstallAsync(block, selectedGameObject);
            if (objs.Count == 0)
            {
                Debug.LogWarning("Colocation object cannot be found, aborting installing Colocation block");
                return new List<GameObject>();
            }
            if (useColocationSession)
            {
                objs[0].GetComponent<ColocationSessionEventHandler>().basis = shareSpaceToGuests ?
                    ColocationSessionEventHandler.Basis.RoomAnchors : ColocationSessionEventHandler.Basis.SharedSpatialAnchor;
            }
            return objs;
        }
#endif // META_MR_UTILITY_KIT_DEFINED
    }
}
