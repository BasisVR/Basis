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
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.MultiplayerBlocks.Shared.Editor;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Shared.Editor
{
    public class NetworkedAvatarInstallationRoutine : NetworkInstallationRoutine
    {
        [SerializeField] internal GameObject prefabV28Plus;
        private GameObject PrefabV28Plus => prefabV28Plus;

        public override List<GameObject> Install(BlockData block, GameObject selectedGameObject)
        {
            var instance = Instantiate(
#if META_AVATAR_SDK_28_OR_NEWER
            PrefabV28Plus,
#else
            Prefab,
#endif
            Vector3.zero, Quaternion.identity);

            instance.SetActive(true);
            instance.name = $"{Utils.BlockPublicTag} {block.BlockName}";
            Undo.RegisterCreatedObjectUndo(instance, "Create " + instance.name);
            return new List<GameObject> { instance };
        }
    }
}
