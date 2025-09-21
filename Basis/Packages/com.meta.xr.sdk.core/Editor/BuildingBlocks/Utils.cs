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
using System.Text.RegularExpressions;
using Meta.XR.Editor.Callbacks;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using Meta.XR.Editor.Tags;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using Meta.XR.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Meta.XR.Editor.UserInterface.Styles.Colors;
using static Meta.XR.Editor.UserInterface.Styles.Contents;
using Object = UnityEngine.Object;


namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class Utils
    {
        internal const string BlocksPublicName = "Building Blocks";
        internal const string BlockPublicName = "Building Block";
        internal const string BlockPublicTag = "[BuildingBlock]";

        internal static readonly TextureContent.Category BuildingBlocksIcons = new("BuildingBlocks/Icons");
        internal static readonly TextureContent.Category BuildingBlocksThumbnails = new("BuildingBlocks/Thumbnails");
        internal static readonly TextureContent.Category BuildingBlocksAnimations = new("BuildingBlocks/Animations");

        internal static readonly TextureContent StatusIcon = TextureContent.CreateContent("ovr_bb_icon.png",
            Utils.BuildingBlocksIcons, $"Open {BlocksPublicName}");

        internal static readonly TextureContent GotoIcon = TextureContent.CreateContent("ovr_icon_link.png",
            Utils.BuildingBlocksIcons, "Select Block");

        internal static readonly TextureContent AddIcon = TextureContent.CreateContent("ovr_icon_addblock.png",
            Utils.BuildingBlocksIcons, "Add Block to current scene");

        public static readonly TextureContent CollectionTagBackgroundTexture = TextureContent.CreateContent("ovr_bg_border_radius8.png",
            BuildingBlocksIcons);
        public static readonly TextureContent CollectionTagCloseIcon = TextureContent.CreateContent("bb_icon_close.png",
            BuildingBlocksIcons, "Close");

        private const string ExperimentalTagName = "Experimental";

        internal static readonly TextureContent ExperimentalIcon =
            TextureContent.CreateContent("ovr_icon_experimental.png", Utils.BuildingBlocksIcons,
                ExperimentalTagName);

        internal static Tag ExperimentalTag = new(ExperimentalTagName)
        {
            Behavior =
            {
                Color = ExperimentalColor,
                Icon = ExperimentalIcon,
                Order = 100,
                ToggleableVisibility = true,
                CanFilterBy = false,
            }
        };

        private const string PrototypingTagName = "Prototyping";

        internal static readonly TextureContent PrototypingIcon =
            TextureContent.CreateContent("ovr_icon_prototype.png", Utils.BuildingBlocksIcons,
                PrototypingTagName);

        internal static Tag PrototypingTag = new(PrototypingTagName)
        {
            Behavior =
            {
                Color = ExperimentalColor,
                Icon = PrototypingIcon,
                Order = 101,
                ToggleableVisibility = true,
                Show = true,
                CanFilterBy = false,
            }
        };

        private const string DebugTagName = "Debug";

        internal static readonly TextureContent DebugIcon =
            TextureContent.CreateContent("ovr_icon_debug.png", Utils.BuildingBlocksIcons, DebugTagName);

        internal static Tag DebugTag = new(DebugTagName)
        {
            Behavior =
            {
                Color = DebugColor,
                Icon = DebugIcon,
                Order = 90,
                ToggleableVisibility = true,
                CanFilterBy = false,
            }
        };

        private const string InternalTagName = "Internal";

        internal const string BBOverviewDocURL = "https://developer.oculus.com/documentation/unity/bb-overview/";

        internal static Tag InternalTag = new(InternalTagName)
        {
            Behavior =
            {
                Order = 200,
                Automated = true,
                Show = false,
                DefaultVisibility = false,
                CanFilterBy = false,
            }
        };

        private const string HiddenTagName = "Hidden";

        internal static Tag HiddenTag = new(HiddenTagName)
        {
            Behavior =
            {
                Order = 201,
                Show = false,
                DefaultVisibility = false,
                CanFilterBy = false,
            }
        };

        private const string DeprecatedTagName = "Deprecated";

        internal static Tag DeprecatedTag = new(DeprecatedTagName)
        {
            Behavior =
            {
                Order = 203,
                Color = ErrorColor,
                Icon = TextureContent.CreateContent("ovr_icon_deprecated.png", Utils.BuildingBlocksIcons,
                    HiddenTagName),
                Show = true,
                ToggleableVisibility = true,
                DefaultVisibility = false,
                CanFilterBy = false,
            }
        };

        private const string NewTagName = "New";

        internal static Tag NewTag = new(NewTagName)
        {
            Behavior =
            {
                Automated = true,
                Order = 202,
                Color = NewColor,
                Icon = TextureContent.CreateContent("ovr_icon_new.png", Utils.BuildingBlocksIcons,
                    NewTagName),
                Show = true,
                CanFilterBy = false,
                ShowOverlay = true,
            }
        };

        private const string NewVersionTagName = "New Version Available";

        internal static Tag NewVersionTag = new(NewVersionTagName)
        {
            Behavior =
            {
                Automated = true,
                Order = 204,
                Color = NewColor,
                Icon = TextureContent.CreateContent("ovr_icon_new.png", Utils.BuildingBlocksIcons,
                    NewVersionTagName),
                Show = true,
                CanFilterBy = false,
                ShowOverlay = true,
            }
        };

        private const string DocumentationUrl = "https://developer.oculus.com/documentation/unity/unity-buildingblocks-overview";


        internal const string PrintIndentationString = "\t";

        internal static readonly ToolDescriptor ToolDescriptor = new()
        {
            Name = BlocksPublicName,
            MenuDescription = "Explore XR Features",
            MqdhCategoryId = "509819301803627",
            Color = Styles.Colors.AccentColor,
            Icon = StatusIcon,
            InfoTextDelegate = ComputeInfoText,
            PillIcon = GetPillIcon,
            OnClickDelegate = OnStatusMenuClick,
            BuildOptionsMenuDelegate = BuildingBlocksWindow.BuildSettingsMenu,
            Order = 1,
            AddToStatusMenu = true,
            Description = BuildingBlocksWindow.Description.text,
            OnUserSettingsGUI = BuildingBlocksWindow.OnUserSettingsGUI,
            Documentation = new List<Documentation>()
            {
                new Documentation()
                {
                    Title = BlocksPublicName,
                    Url = DocumentationUrl
                }
            }
        };

        static Utils()
        {
        }


        private static int ComputeNumberOfNewBlocks() =>
            BlockBaseData.Registry.Values.Count(data => !data.Hidden && (data.Tags.Contains(NewTag) || data.Tags.Contains(NewVersionTag)));

        private static (string, Color?) ComputeInfoText()
        {
            var numberOfNewBlocks = ComputeNumberOfNewBlocks();
            if (numberOfNewBlocks > 0)
            {
                return (
                    $"{numberOfNewBlocks} new {OVREditorUtils.ChoosePlural(numberOfNewBlocks, "block", "blocks")} available",
                    NewColor);
            }

            return (null, null);
        }

        private static (TextureContent, Color?, bool) GetPillIcon()
        {
            if (ComputeNumberOfNewBlocks() > 0)
            {
                return (NewTag.Behavior.Icon, NewColor, true);
            }

            return (null, null, false);
        }

        private static void OnStatusMenuClick(Origins origin)
        {
            BuildingBlocksWindow.ShowWindow(origin, null);
        }

        public static BlockData GetBlockData(this BuildingBlock block) => GetBlockData(block.blockId);

        public static BlockData GetBlockData(string blockId) => BlockBaseData.Registry[blockId] as BlockData;

        public static BuildingBlock GetBlock(this BlockData data)
        {
            return Object.FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None)
                .FirstOrDefault(x => x.BlockId == data.Id);
        }

        public static InstallationRoutine GetInstallationRoutine(this BuildingBlock block) =>
            GetInstallationRoutine(block.InstallationRoutineCheckpoint.InstallationRoutineId);

        public static InstallationRoutine GetInstallationRoutine(string installationRoutineId) =>
            InstallationRoutine.Registry[installationRoutineId];

        public static BuildingBlock GetBlock(string blockId)
        {
            return GetBlockData(blockId)?.GetBlock();
        }

        public static List<BuildingBlock> GetBlocks(this BlockData data)
        {
            return Object.FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None)
                .Where(x => x.BlockId == data.Id)
                .ToList();
        }

        public static List<BuildingBlock> GetBlocks(string blockId)
        {
            return GetBlockData(blockId)?.GetBlocks() ?? new List<BuildingBlock>();
        }

        public static List<T> GetBlocksWithType<T>() where T : Component
        {
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None)
                .Where(controller => controller.GetComponent<BuildingBlock>() != null)
                .ToList();
        }

        public static List<T> GetBlocksWithBaseClassType<T>() where T : Component
        {
            var objects = GetBlocksWithType<T>();
            return objects
                .Select(obj => obj.GetComponent<T>())
                .Where(component => component != null && component.GetType() == typeof(T))
                .ToList();
        }

        private static bool IsRequiredBy(this BlockData data, BlockData other)
        {
            if (data == null || other == null)
            {
                return false;
            }

            return data == other || other.Dependencies.Any(data.IsRequiredBy);
        }

        public static List<BuildingBlock> GetBlocksInScene()
        {
            return Object.FindObjectsByType<BuildingBlock>(FindObjectsSortMode.InstanceID).ToList();
        }

        public static IEnumerable<BuildingBlock> GetInterfaceBlocksInScene(string blockId, string installationRoutineId) =>
            GetBlocksInScene()
                .Where(b =>
                    b.BlockId == blockId
                    && b.InstallationRoutineCheckpoint.InstallationRoutineId ==
                    installationRoutineId
                );
        public static List<BuildingBlock> GetUsingBlocksInScene(this BlockData requiredData)
        {
            return Object.FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None).Where(x =>
            {
                var data = x.GetBlockData();
                return requiredData != data && requiredData.IsRequiredBy(data);
            }).ToList();
        }

        public static List<BlockData> GetUsingBlockDatasInScene(this BlockData requiredData)
        {
            return requiredData.GetUsingBlocksInScene().Select(x => x.GetBlockData()).Distinct().ToList();
        }

        public static IEnumerable<BlockData> GetAllDependencies(this BlockData data) =>
            data.Dependencies
            .Where(dependency => dependency != null)
            .SelectMany(
                dependency => GetAllDependencies(dependency)
                .Concat(new[] { dependency })
            ).Distinct();

        public static void SelectBlockInScene(this BuildingBlock block)
        {
            Selection.activeGameObject = block.gameObject;
        }

        public static void SelectBlocksInScene(IEnumerable<GameObject> blockList)
        {
            Selection.objects = blockList.Cast<Object>().ToArray();
        }

        public static void SelectBlocksInScene(this BlockData blockData)
        {
            var blocksInScene = blockData.GetBlocks();

            if (blocksInScene.Count == 1)
            {
                SelectBlockInScene(blocksInScene[0]);
            }
            else if (blocksInScene.Count > 1)
            {
                SelectBlocksInScene(blocksInScene.Select(block => block.gameObject));
            }
        }

        public static void HighlightBlockInScene(this BuildingBlock block)
        {
            EditorGUIUtility.PingObject(block.gameObject);
        }

        public static int ComputeNumberOfBlocksInScene(this BlockData blockData)
        {
            return Object.FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None)
                .Count(x => x.BlockId == blockData.Id);
        }

        public static void BreakBlockConnection(this BuildingBlock buildingBlock)
        {
            if (buildingBlock == null)
            {
                return;
            }
            if (EditorUtility.DisplayDialog(
                    "Confirm Operation",
                    "Are you sure you want to break Block connection? \n\nThis action cannot be undone.",
                    "Yes",
                    "No"))
            {
                var dependents =
                    buildingBlock
                        .GetBlockData()
                        .GetUsingBlockDatasInScene()
                        .SelectMany(x => x.GetBlocks());

                foreach (var dep in dependents)
                {
                    BreakBlockConnection(dep);
                }

                var go = buildingBlock.gameObject;
                go.name = go.name.Replace(Utils.BlockPublicTag, string.Empty).TrimStart();

                Object.DestroyImmediate(buildingBlock);
            }
        }

        public static T FindComponentInScene<T>() where T : Component
        {
            var scene = SceneManager.GetActiveScene();
            var rootGameObjects = scene.GetRootGameObjects();
            return rootGameObjects.FirstOrDefault(go => go.GetComponentInChildren<T>())?.GetComponentInChildren<T>();
        }

        public static HashSet<string> CollectPackageDependencies(this BlockData blockData, HashSet<string> set)
        {
            foreach (var packageDependency in blockData.PackageDependencies)
            {
                set.Add(packageDependency);
            }

            foreach (var dep in blockData.Dependencies)
            {
                CollectPackageDependencies(dep, set);
            }

            return set;
        }

        internal static bool IsPackageInstalled(string packageId)
        {
            if (CustomPackageDependencyRegistry.IsPackageDepInCustomRegistry(packageId))
            {
                return CustomPackageDependencyRegistry.IsPackageInstalled(packageId);
            }

            return PackageList.IsPackageInstalledWithValidVersion(packageId);
        }

        internal static void UpdateBlockUsageFrequency(BlockData blockData)
        {
            var freqTable = LoadBlocksUsageFrequencyTable();
            if (freqTable == null)
            {
                freqTable = new();
            }

            if (freqTable.ContainsKey(blockData.Id))
            {
                freqTable[blockData.Id] += 1;
            }
            else
            {
                freqTable.Add(blockData.Id, 1);
            }

            SaveUsageFreqTable(freqTable);
        }

        private static Setting<string> _blocksUsageFrequency;
        private static Setting<string> BlocksUsageFrequency => _blocksUsageFrequency ??= new UserString()
        {
            Owner = ToolDescriptor,
            Uid = $"BuildingBlocks_UsageFreq_{Application.dataPath}",
            Default = string.Empty,
            SendTelemetry = false
        };

        internal static SerializableDictionary<string, int> LoadBlocksUsageFrequencyTable()
        {
            var value = BlocksUsageFrequency.Value;
            return string.IsNullOrEmpty(value)
                ? null
                : JsonUtility.FromJson<SerializableDictionary<string, int>>(value);
        }

        private static void SaveUsageFreqTable(SerializableDictionary<string, int> data)
        {
            var json = JsonUtility.ToJson(data);
            BlocksUsageFrequency.SetValue(json);
        }


        public static TResult Let<TSource, TResult>(this TSource source, Func<TSource, TResult> func) => func(source);

        internal static bool HasDuplicates<T>(this IEnumerable<T> dependencies) =>
            dependencies
                .GroupBy(x => x)
                .Any(g => g.Count() > 1);

        /// <summary>
        /// Compare current Unity Editor version with target Unity Editor version.
        /// </summary>
        /// <remarks>
        /// Version format: Major.Minor.Build. Examples: 2022.3.2, 2023.3, 2021.3.2
        /// </remarks>
        /// <param name="target">Target version</param>
        /// <returns>
        /// returns -1 if current version is older than target
        /// returns 1 if current version is newer than target
        /// returns 0 if versions are same
        /// </returns>
        public static int CompareCurrentUnityEditorVersions(string target)
        {
            if (target == null)
                throw new ArgumentNullException();

            var pattern = @"[\.-]|(?<=\d)(?=[a-zA-Z])";
            var currentVersionParts = Regex.Split(Application.unityVersion, pattern)
                .Where(s => !String.IsNullOrEmpty(s))
                .Select(s =>
                {
                    int.TryParse(s, out var o);
                    return o;
                }).ToArray();

            var targetVersionParts = Regex.Split(target, pattern)
                .Where(s => !String.IsNullOrEmpty(s))
                .Select(s =>
                {
                    int.TryParse(s, out var o);
                    return o;
                }).ToArray();

            if (targetVersionParts.Length == 0)
            {
                throw new ArgumentException("Empty target version string.");
            }

            Version currentVersion = new Version(currentVersionParts[0], currentVersionParts[1], currentVersionParts[2]);
            Version targetVersion = new Version(targetVersionParts[0],
                targetVersionParts.Length > 1 ? targetVersionParts[1] : 0,
                targetVersionParts.Length > 2 ? targetVersionParts[2] : 0);

            return currentVersion.CompareTo(targetVersion);
        }

        public static readonly Func<bool> IsApplicationPlaying = () => Application.isPlaying;
        internal static class Sort
        {
            public static IEnumerable<BlockBaseData> Alphabetical(IEnumerable<BlockBaseData> blocks) => blocks.OrderByDescending(b => b);

            public static IEnumerable<BlockBaseData> MostUsed(IEnumerable<BlockBaseData> blocks) => MostUsed(blocks, LoadBlocksUsageFrequencyTable());

            internal static IEnumerable<BlockBaseData> MostUsed(IEnumerable<BlockBaseData> blocks, SerializableDictionary<string, int> frequencyTable)
            {
                if (frequencyTable == null) return blocks;

                var unusedBlocks = blocks.Where(b => !frequencyTable.ContainsKey(b.Id));
                var frequentlyUsedBlocks = frequencyTable
                    .OrderByDescending(kvp => kvp.Value)
                    .SelectMany(kvp => blocks.Where(block => block.Id == kvp.Key))
                    .ToList();
                frequentlyUsedBlocks.AddRange(unusedBlocks);

                return frequentlyUsedBlocks;
            }

            public static IEnumerable<BlockBaseData> MostPopular(IEnumerable<BlockBaseData> blocks)
            {
                return blocks.Where(obj => !string.IsNullOrEmpty(obj.name))
                    .OrderBy(block => block.Order)
                    .ThenBy(block => block.BlockName.Value);
            }
        }

        internal static IEnumerable<OVRConfigurationTask> GetAssociatedRules(this BuildingBlock block)
            => block.GetBlockData()?.GetAssociatedRules(block) ?? Enumerable.Empty<OVRConfigurationTask>();

        internal static MonoScript FindMonoScriptByTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var possibleFileName = Type.GetType(typeName)?.Name; // file name most likely the same to class name
            var guid = AssetDatabase.FindAssets(possibleFileName).FirstOrDefault();
            if (guid == null) // heavier if file not found by class name - iterate through MonoScript for the type
            {
                guid = AssetDatabase.FindAssets("t:MonoScript")
                    .FirstOrDefault(script => script.GetType().AssemblyQualifiedName == typeName);
            }
            return !string.IsNullOrEmpty(guid)
                ? AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid))
                : null;
        }

        internal static string[] GetClassNamesFromAssemblyQualifiedNames(string[] assemblyQualifiedNames)
        {
            return assemblyQualifiedNames.Select(s => s.Split(",").FirstOrDefault()).ToArray();
        }
    }
}
