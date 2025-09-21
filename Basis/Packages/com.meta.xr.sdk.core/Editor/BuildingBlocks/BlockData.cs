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
using System.Reflection;
using System.Threading.Tasks;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Guides.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.SceneManagement;
using Meta.XR.Editor.UPST.Notifications;


namespace Meta.XR.BuildingBlocks.Editor
{
    public class BlockData : BlockBaseData
    {
        [SerializeField] internal GameObject prefab;
        public GameObject Prefab => prefab;
        protected virtual bool UsesPrefab => true;
        internal bool GetUsesPrefab => UsesPrefab;

        internal string GuidedSetupName
        {
            get => guidedSetupName;
            set => guidedSetupName = value;
        }

        // Indicates whether the current block can be added over selected GameObject(s).
        // Override this value in custom BlockData class to show "Add to Selected" button
        // in Block's detail page.
        internal virtual bool CanBeAddedOverGameObject => false;

        [SerializeField] internal int selectedGuidedSetupIndex;
        [SerializeField] internal List<string> externalBlockDependencies;
        [SerializeField] internal List<string> dependencies;
        [SerializeField] internal string guidedSetupName;

        internal virtual IReadOnlyCollection<InstallationStepInfo> InstallationSteps
        {
            get
            {
                if (!UsesPrefab) return Array.Empty<InstallationStepInfo>();

                return new List<InstallationStepInfo>
                {
                    new(Prefab, "Instantiates a {0} prefab."),
                    new(null, $"Renames the instantiated prefab to <b>{Utils.BlockPublicTag} {BlockName}</b>.")
                };
            }
        }


        public IEnumerable<BlockData> Dependencies =>
            (dependencies ?? Enumerable.Empty<string>())
            .Concat(externalBlockDependencies ?? Enumerable.Empty<string>())
            .Select(Utils.GetBlockData);

        [SerializeField] internal List<string> packageDependencies;
        public virtual IEnumerable<string> PackageDependencies => packageDependencies ?? Enumerable.Empty<string>();

        [Tooltip("Indicates whether only one instance of this block can be installed per scene.")]
        [SerializeField]
        internal bool isSingleton;

        [Tooltip("(Optional) Briefly write how this block should be used.")]
        [TextArea(5, 40)]
        [SerializeField]
        internal string usageInstructions;

        [Tooltip("(Optional) Name of the feature documentation")]
        [SerializeField]
        internal string featureDocumentationName;

        [Tooltip("(Optional) Link to the feature documentation")]
        [SerializeField]
        internal string featureDocumentationUrl;

        public string UsageInstructions => usageInstructions;
        public string FeatureDocumentationName => featureDocumentationName;
        public string FeatureDocumentationUrl => featureDocumentationUrl;
        public bool IsSingleton => isSingleton;


        internal override async Task AddToProject(GameObject selectedGameObject = null, Action onInstall = null)
        {
            NotificationsScheduler.PauseForSeconds(30);

            UsageSettings.UsesBuildingBlocks.SetValue(true);

            using (new OVREditorUtils.UndoScope($"Install {Utils.BlockPublicTag} {BlockName}"))
            {
                await InstallWithDependenciesAndCommit(selectedGameObject);
                onInstall?.Invoke();

                // Update local block usage data
                Utils.UpdateBlockUsageFrequency(this);
            }
        }

        internal override async Task AddToObjects(List<GameObject> selectedGameObjects)
        {
            using (new OVREditorUtils.UndoScope($"Install {Utils.BlockPublicTag} {BlockName}"))
            {
                await InstallWithDependenciesAndCommit(selectedGameObjects);
            }
        }

        [ContextMenu("Install")]
        private async void ContextMenuInstall()
        {
            await AddToProject(null, null);
        }

        private async Task InstallWithDependenciesAndCommit(List<GameObject> selectedGameObjects)
        {
            Exception installException = null;
            try
            {
                List<GameObject> installedObjects;
                // use different signatures for multiple selected objects vs. single/null selected object
                if (selectedGameObjects.Count <= 1)
                {
                    installedObjects = await InstallWithDependencies(selectedGameObjects.FirstOrDefault());
                }
                else
                {
                    installedObjects = await InstallWithDependencies(selectedGameObjects);
                }

                SaveScene();
                var createdBlocks =
                    installedObjects.SelectMany(gameObject => gameObject.GetComponents<BuildingBlock>());
                await FixSetupRules(createdBlocks);

                // Wait for 1 frame.
                await Task.Yield();
                Utils.SelectBlocksInScene(installedObjects);
            }
            catch (Exception e)
            {
                if (e is not InstallationCancelledException)
                {
                    Debug.LogError($"Error installing Building Block {BlockName}: {e.Message} \n {e.StackTrace}");
                }

                installException = e;
                throw;
            }
            finally
            {
                OVRTelemetry.Start(OVRTelemetryConstants.BB.MarkerId.InstallBlockData)
                    .SetResult(installException switch
                    {
                        null => OVRPlugin.Qpl.ResultType.Success,
                        InstallationCancelledException => OVRPlugin.Qpl.ResultType.Cancel,
                        _ => OVRPlugin.Qpl.ResultType.Fail
                    })
                    .AddAnnotation(OVRTelemetryConstants.BB.AnnotationType.BlockId, Id)
                    .AddAnnotationIfNotNullOrEmpty(OVRTelemetryConstants.BB.AnnotationType.Error,
                        installException?.Message + "\n" + installException?.StackTrace)
                    .Send();
            }
        }

        private async Task InstallWithDependenciesAndCommit(GameObject selectedGameObject = null)
        {
            await InstallWithDependenciesAndCommit(new List<GameObject> { selectedGameObject });
        }

        internal static async Task FixSetupRules(IEnumerable<BuildingBlock> blocks)
        {
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);

            await OVRProjectSetup.UpdateTasksAsync(buildTargetGroup, logMessages: OVRProjectSetup.LogMessages.Disabled);

            var additionalRules = blocks
                .SelectMany<BuildingBlock, OVRConfigurationTask>(block => block.GetAssociatedRules()).ToList();

            bool IsRequired(OVRConfigurationTask task) =>
                !task.IsDone(buildTargetGroup)
                && !task.IsIgnored(buildTargetGroup)
                && (task.Level.GetValue(buildTargetGroup) == OVRProjectSetup.TaskLevel.Required
                    || additionalRules.Contains(task));

            await OVRProjectSetup.FixTasksAsync(buildTargetGroup, tasks => tasks
                .Where(IsRequired)
                .ToList());

            AssetDatabase.SaveAssets();

            await OVRProjectSetup.UpdateTasksAsync(buildTargetGroup, logMessages: OVRProjectSetup.LogMessages.Disabled);
        }

        internal override bool IsInstallable =>
            base.IsInstallable
            && !HasMissingDependencies
            && !IsSingletonAndAlreadyPresent
            && !HasMissingPackageDependencies;

        internal bool HasMissingPackageDependencies => GetMissingPackageDependencies.Any();

        internal IEnumerable<string> GetMissingPackageDependencies =>
            this.CollectPackageDependencies(new HashSet<string>())
                .Where(packageId => !Utils.IsPackageInstalled(packageId));

        internal bool HasMissingDependencies => GetMissingDependencies.Any();

        private IEnumerable<string> GetMissingDependencies =>
            (dependencies ?? Enumerable.Empty<string>())
            .Concat(
                PackageDependencies.All(Utils.IsPackageInstalled)
                    ? externalBlockDependencies ?? Enumerable.Empty<string>()
                    : Enumerable.Empty<string>())
            .Where(dependencyId => Utils.GetBlockData(dependencyId) == null);

        internal bool IsSingletonAndAlreadyPresent => IsSingleton && IsBlockPresentInScene();

        internal virtual async Task<List<GameObject>> InstallWithDependencies(List<GameObject> selectedGameObjects)
        {
            var createdObjects = new List<GameObject>();
            foreach (var obj in selectedGameObjects.DefaultIfEmpty())
            {
                createdObjects.AddRange(await InstallWithDependencies(obj));
            }

            return createdObjects;
        }

        internal virtual async Task<List<GameObject>> InstallWithDependencies(GameObject selectedGameObject = null)
        {
            if (IsSingletonAndAlreadyPresent)
            {
                throw new InstallationCancelledException(
                    $"Block {BlockName} is a singleton and already present in the scene so it cannot be installed.");
            }

            if (HasMissingDependencies)
            {
                throw new InstallationCancelledException(
                    $"A dependency of block {BlockName} is not present in the project: {string.Join(", ", GetMissingDependencies)}");
            }

            using (new OVREditorUtils.UndoScope($"Install {Utils.BlockPublicTag} {BlockName}"))
            {
                await InstallDependenciesAsync(Dependencies, selectedGameObject);
                return await InstallAsync(selectedGameObject);
            }
        }

        protected virtual Type ComponentType => typeof(BuildingBlock);

        internal async Task<List<GameObject>> InstallAsync(GameObject selectedGameObject = null)
        {
            return await InstallBlockAsync(selectedGameObject);
        }

        protected async Task<List<GameObject>> InstallBlockAsync(GameObject selectedGameObject)
        {
            var spawnedObjects = await InstallRoutineAsync(selectedGameObject);

            foreach (var spawnedObject in spawnedObjects)
            {
                if (spawnedObject.GetComponent(ComponentType) != null)
                {
                    continue;
                }

                var block = Undo.AddComponent(spawnedObject, ComponentType) as BuildingBlock;
                SetupBlockComponent(block);
                while (UnityEditorInternal.ComponentUtility.MoveComponentUp(block))
                {
                }

                Undo.RegisterCompleteObjectUndo(block, $"Setup {nameof(ComponentType)}");

                // Show guided setup window if available
                ShowGuidedSetup();

                OVRTelemetry.Start(OVRTelemetryConstants.BB.MarkerId.AddBlock)
                    .AddBlockInfo(block)
                    .AddSceneInfo(spawnedObject.scene)
                    .Send();
            }

            return spawnedObjects;
        }

        protected virtual void SetupBlockComponent(BuildingBlock block)
        {
            block.blockId = Id;
            block.version = Version;
        }

        protected virtual List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
            if (!UsesPrefab)
            {
                return new List<GameObject>();
            }

            var instance = Instantiate(Prefab, Vector3.zero, Quaternion.identity);
            instance.SetActive(true);
            instance.name = $"{Utils.BlockPublicTag} {BlockName}";
            Undo.RegisterCreatedObjectUndo(instance, "Create " + instance.name);

            // Show guided setup window if any.
            ShowGuidedSetup();

            return new List<GameObject> { instance };
        }

        private void ShowGuidedSetup()
        {
            string guidedSetupName = GuidedSetupName;
            var interfaceBlock = this as InterfaceBlockData;
            if (interfaceBlock != null && !string.IsNullOrEmpty(interfaceBlock.SelectedRoutine.guidedSetupName))
            {
                guidedSetupName = interfaceBlock.SelectedRoutine.guidedSetupName;
            }

            if (string.IsNullOrEmpty(guidedSetupName)) return;

            var guideType = Type.GetType(guidedSetupName);
            if (guideType == null || guideType.BaseType != typeof(GuidedSetup))
                return;

            var instance = Activator.CreateInstance(guideType);
            (instance as GuidedSetup)?.ShowWindow(Origins.Component);
        }

        protected virtual Task<List<GameObject>> InstallRoutineAsync(GameObject selectedGameObject)
        {
            return Task.FromResult(InstallRoutine(selectedGameObject));
        }

        private static async Task InstallDependenciesAsync(IEnumerable<BlockData> dependencies,
            GameObject selectedGameObject = null)
        {
            foreach (var dependency in dependencies)
            {
                if (IsBlockPresentInScene(dependency.Id))
                {
                    continue;
                }

                await dependency.InstallWithDependencies(selectedGameObject);
            }
        }

        private static bool IsBlockPresentInScene(string blockId)
        {
            return FindObjectsByType<BuildingBlock>(FindObjectsSortMode.None).Any(x => x.BlockId == blockId);
        }

        protected virtual bool IsBlockPresentInScene()
        {
            return IsBlockPresentInScene(Id);
        }

        internal bool IsUpdateAvailableForBlock(BuildingBlock block) => Version > block.Version;

        internal async Task UpdateBlockToLatestVersion(BuildingBlock block)
        {
            if (!IsUpdateAvailableForBlock(block))
            {
                throw new InstallationCancelledException(
                    $"Block {BlockName} is already in the latest version.");
            }

            if (IsSingleton)
            {
                foreach (var instance in this.GetBlocks())
                {
                    DestroyImmediate(instance.gameObject);
                }
            }
            else
            {
                DestroyImmediate(block.gameObject);
            }

            await InstallWithDependenciesAndCommit();

            OVRTelemetry.Start(OVRTelemetryConstants.BB.MarkerId.UpdateBlock)
                .AddAnnotation(OVRTelemetryConstants.BB.AnnotationType.BlockId, Id)
                .AddAnnotation(OVRTelemetryConstants.BB.AnnotationType.Version, Version.ToString())
                .Send();
        }

        private static void SaveScene()
        {
            if (!IsCurrentSceneSaved())
            {
                return;
            }

            var activeScene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);
        }

        private static bool IsCurrentSceneSaved()
        {
            var scenePath = SceneManager.GetActiveScene().path;
            return !string.IsNullOrEmpty(scenePath);
        }


        internal override bool OverridesInstallRoutine
        {
            get
            {
                var derivedMethodInfo = GetType().GetMethod(nameof(InstallRoutine),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
                return derivedMethodInfo != null &&
                       derivedMethodInfo != derivedMethodInfo.GetBaseDefinition();
            }
        }

        internal virtual IEnumerable<OVRConfigurationTask> GetAssociatedRules(BuildingBlock block)
            => Enumerable.Empty<OVRConfigurationTask>();
    }
}
