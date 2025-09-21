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
using System.Text;
using System.Threading.Tasks;
using Meta.XR.Editor.Tags;
using Oculus.VR.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class BlocksContentManager
    {
        private const double CacheDurationInHours = 6;
        private const string CommonTag = "Common";

        private const string DownloadPath = "https://www.facebook.com/building-blocks-content";

        private static readonly RemoteContentDownloader Downloader;

        private static BlockData[] _contentFilter;

        private static Dictionary<string, BlockModifiableProperty[]> _blockModifiableProperties;

        private static Dictionary<Tag, BlockUrl[]> _documentationsData;

        internal static readonly HashSet<Tag> RemoteCollectionTags = new();
        private static readonly Dictionary<Tag, List<Editor.BlockData>> RemoteCollections = new();

        static BlocksContentManager()
        {
            Downloader = new RemoteContentDownloader(CacheDurationInHours, "bb_content.json", DownloadPath);
#pragma warning disable CS4014
            InitializeAsync();
#pragma warning restore CS4014
        }

        private static async Task InitializeAsync()
        {
            var successfulLoad = await Reload(false);
            if (!successfulLoad)
            {
                await Reload(true);
            }
        }

        public static async Task<bool> Reload(bool forceRedownload)
        {
            if (forceRedownload)
            {
                Downloader.ClearCache();
            }
            var (success, jsonData) = await Downloader.RefreshAndLoad();
            return success && LoadContentJsonData(jsonData);
        }

        public static BlockModifiableProperty[] GetBlockModifiablePropertyById(string blockId)
        {
            return _blockModifiableProperties == null
                ? Array.Empty<BlockModifiableProperty>()
                : _blockModifiableProperties.GetValueOrDefault(blockId, Array.Empty<BlockModifiableProperty>());
        }

        internal static BlockUrl[] GetBlockUrls(Tag tag)
        {
            if (_documentationsData == null)
                return Array.Empty<BlockUrl>();

            return _documentationsData.TryGetValue(tag, out var urls) ? urls : Array.Empty<BlockUrl>();
        }

        // For common / generic docs related to building blocks.
        internal static BlockUrl[] GetCommonDocs() => GetBlockUrls(CommonTag);


        #region Data Parsing

        // ReSharper disable InconsistentNaming
        [Serializable]
        internal struct BlockData
        {
            public string id;
            public string blockName;
            public string description;
            public string[] tags;
            public BlockModifiableProperty[] modifiableProperties;
        }

        [Serializable]
        internal struct BlockModifiableProperty
        {
            public string highlightIdentifier;
            public string name;
            public string description;

        }
        [Serializable]
        internal struct BlockDocumentation
        {
            public string tag;
            public BlockUrl[] urls;
        }

        [Serializable]
        internal struct BlockUrl
        {
            public string title;
            public string url;
        }

        [Serializable]
        internal struct BlockDataResponse
        {
            public BlockData[] content;
            public BlockDocumentation[] docs;
            public TagsData tags;
        }

        [Serializable]
        internal struct TagsData
        {
            public CollectionTag[] collections;
            public FeatureTag[] features;
        }

        [Serializable]
        internal struct CollectionTag
        {
            public string tag;
            public string description;
            public BlockData[] blocks;
        }

        [Serializable]
        internal struct FeatureTag
        {
            public string tag;
        }
        // ReSharper restore InconsistentNaming

        internal static BlockDataResponse ParseJsonData(string jsonData)
        {
            BlockDataResponse response;
            try
            {
                response = JsonUtility.FromJson<BlockDataResponse>(jsonData);
            }
            catch (Exception)
            {
                response = default;
            }

            response.content ??= Array.Empty<BlockData>();
            response.docs ??= Array.Empty<BlockDocumentation>();
            response.tags.collections ??= Array.Empty<CollectionTag>();
            response.tags.features ??= Array.Empty<FeatureTag>();

            return response;
        }

        #endregion

        #region Blocks Content

        internal static bool LoadContentJsonData(string jsonData)
        {
            var response = ParseJsonData(jsonData);

            _contentFilter = response.content;

            ParseModifiableProperties(response);

            ParseDocumentations(response);

            ParseTagsData(response);

            return _contentFilter is { Length: > 0 };
        }

        private static void ParseModifiableProperties(BlockDataResponse response)
        {
            if (response.content is { Length: > 0 })
            {
                _blockModifiableProperties = response.content
                    .ToDictionary(item => item.id, item => item.modifiableProperties);
            }
        }

        private static void ParseDocumentations(BlockDataResponse response)
        {
            _documentationsData = new Dictionary<Tag, BlockUrl[]>();
            foreach (var doc in response.docs)
            {
                _documentationsData[doc.tag] = doc.urls;
            }

        }
        private static void ParseTagsData(BlockDataResponse response)
        {
            RemoteCollections.Clear();
            RemoteCollectionTags.Clear();

            foreach (var collection in response.tags.collections)
            {
                var tag = new Tag(collection.tag);

                // Currently we don't support any new Collection tag coming from remote
                if (!CustomTagBehaviors.CollectionTags.Contains(tag))
                    continue;

                RemoteCollectionTags.Add(tag);
                RemoteCollections.TryAdd(tag, new());

                foreach (var block in collection.blocks ?? Array.Empty<BlockData>())
                {
                    var blockData = Utils.GetBlockData(block.id);
                    if (blockData != null &&
                        !RemoteCollections[tag].Contains(blockData) &&
                        _contentFilter.Any(b => b.id == block.id))
                    {
                        RemoteCollections[tag].Add(blockData);
                    }
                }
            }
        }

        public static IReadOnlyList<BlockBaseData> FilterBlockWindowContent(IReadOnlyList<BlockBaseData> content)
        {
            return FilterBlockWindowContent(content, _contentFilter);
        }

        private static void ClearBlocksOverrides(IEnumerable<BlockBaseData> content)
        {
            foreach (var block in content)
            {
                block.BlockName.RemoveOverride();
                block.Description.RemoveOverride();
                block.OverridableTags.RemoveOverride();
            }
        }

        internal static IReadOnlyList<BlockBaseData> FilterBlockWindowContent(IReadOnlyList<BlockBaseData> content,
            BlockData[] contentFilter)
        {
            if (contentFilter == null || contentFilter.Length == 0)
            {
                ClearBlocksOverrides(content);
                return content;
            }

            var contentFilterDictionary = contentFilter
                .Select((value, index) => new { value, index })
                .ToDictionary(pair => pair.value.id, pair => new { pair.value, pair.index });

            var filteredContent = content
                .Where(block => contentFilterDictionary.ContainsKey(block.Id))
                .OrderBy(block => contentFilterDictionary[block.Id].index)
                .ToList();

            foreach (var blockBaseData in filteredContent)
            {
                blockBaseData.BlockName.SetOverride(contentFilterDictionary[blockBaseData.Id].value.blockName);
                blockBaseData.Description.SetOverride(contentFilterDictionary[blockBaseData.Id].value.description);
                blockBaseData.OverridableTags.SetOverride(GenerateTagArrayFromTags(contentFilterDictionary[blockBaseData.Id].value.tags));
            }

            return filteredContent;
        }

        private static TagArray GenerateTagArrayFromTags(string[] tags)
        {
            if (tags == null)
            {
                return null;
            }

            var tagArray = new TagArray();
            tagArray.Add(tags.Select(tag => new Tag(tag)));
            return tagArray;
        }


        public static IReadOnlyCollection<BlockBaseData> GetCollection(CollectionTagBehavior collection)
        {
            if (collection == null) return null;
            return RemoteCollections.GetValueOrDefault(collection.Tag, null);
        }

        #endregion
    }
}
