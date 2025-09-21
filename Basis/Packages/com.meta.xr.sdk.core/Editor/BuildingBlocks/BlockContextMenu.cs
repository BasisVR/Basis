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

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class BlockContextMenu : MonoBehaviour
    {
        private const string ItemPath = "GameObject/Building Blocks/Break Block Connection";
        private const int ItemPriority = 10;

        [MenuItem(ItemPath, false, ItemPriority)]
        private static void RemoveBuildingBlock(MenuCommand menuCommand)
        {
            BreakBlockConnection(GetSelectedBuildingBlock());
        }

        [MenuItem(ItemPath, true, ItemPriority)]
        private static bool ValidateRemoveBuildingBlock(MenuCommand menuCommand)
        {
            return GetSelectedBuildingBlock() != null;
        }

        private static void BreakBlockConnection(BuildingBlock buildingBlock)
        {
            Utils.BreakBlockConnection(buildingBlock);
        }

        private static BuildingBlock GetSelectedBuildingBlock()
        {
            var go = Selection.activeGameObject;
            return go == null ? null : go.GetComponent<BuildingBlock>();
        }
    }
}
