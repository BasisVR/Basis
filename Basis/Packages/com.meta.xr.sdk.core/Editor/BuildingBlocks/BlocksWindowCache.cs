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

namespace Meta.XR.BuildingBlocks.Editor
{
    internal static class BlocksWindowCache
    {
        private static readonly Dictionary<string, BlockDataCache> _cache = new();

        public class BlockDataCache
        {
            private readonly BlockBaseData _block;
            private int? _numberOfBlocksInScene;
            private bool? _isInteractable;
            private bool? _hasMissingPackageDependencies;

            public int NumberOfBlocksInScene => _numberOfBlocksInScene ??=
                (_block as BlockData)?.ComputeNumberOfBlocksInScene() ?? 0;
            public bool IsInteractable => _isInteractable ??= _block.IsInteractable;

            public bool HasMissingPackageDependencies => _hasMissingPackageDependencies ??=
                (_block as BlockData)?.HasMissingPackageDependencies ?? false;

            public BlockDataCache(BlockBaseData block)
            {
                _block = block;
            }

            public void Reset()
            {
                _numberOfBlocksInScene = null;
                _isInteractable = null;
                _hasMissingPackageDependencies = null;
            }
        }

        public static BlockDataCache GetCache(this BlockBaseData block)
        {
            if (_cache.TryGetValue(block.Id, out var cache)) return cache;

            cache = new BlockDataCache(block);
            _cache.Add(block.Id, cache);
            return cache;
        }
    }
}
