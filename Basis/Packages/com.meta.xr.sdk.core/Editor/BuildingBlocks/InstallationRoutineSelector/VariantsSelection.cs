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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal class MissingInstallationRoutineException : InvalidOperationException
    {
        internal MissingInstallationRoutineException(string message) : base(message) { }
    }

    internal class VariantsSelection : IReadOnlyList<VariantHandle>
    {
        private List<VariantHandle> _variants = new();
        public BlockData BlockData { get; private set; }
        public bool Completed { get; set; }
        public bool Canceled { get; set; }

        private readonly List<string> _missingDependencies = new();
        public IReadOnlyList<string> MissingDependencies => _missingDependencies;
        public bool HasMissingDependencies => _missingDependencies.Count > 0;
        private readonly List<string> _dependencies = new();
        public IReadOnlyList<string> Dependencies => _dependencies;

        private readonly Dictionary<BlockData, IReadOnlyList<InstallationRoutine>> _possibleRoutines = new();
        public IReadOnlyDictionary<BlockData, IReadOnlyList<InstallationRoutine>> PossibleRoutines => _possibleRoutines;

        private readonly Dictionary<string, bool> _variantConditionStatus = new();
        public IReadOnlyDictionary<string, bool> VariantConditionStatus => _variantConditionStatus;

        private bool RequiresChoiceFor(IReadOnlyList<InstallationRoutine> routines)
        {
            if (Completed)
                return false;

            if (!this.Any())
                return false;

            if (this.Any(variant => variant.Attribute.Behavior == VariantAttribute.VariantBehavior.Parameter))
                return true;

            if (HasMissingDependencies)
                return true;

            return routines.Count > 1;
        }

        public void SetupForSelection(BlockData blockData)
        {
            // Do not setup if already setup (unless forced)
            if (BlockData != null || blockData is not InterfaceBlockData data) return;

            Reset();

            BlockData = data;
            InitializeVariants();
            UpdateVariants();
        }

        private void InitializeVariants()
        {
            _variants = GetVariantsRecursive(BlockData)
                .Select(variant => variant.ToSelection()).ToList();
            // initialization doesn't have selected variants to be matched with, hence clearing here
            _variantConditionStatus.Clear();
        }

        public void UpdateVariants()
        {
            // Get expected Variants, recursively
            var expectedVariants = GetVariantsRecursive(BlockData)
                .Select(variant => variant.ToSelection());

            // Extract those that were previously already present (and keep their previous value)
            var sharedVariants = _variants.Where(variant => expectedVariants.Any(variant.Matches));

            // Extract those that are not present yet
            var missingVariants = expectedVariants.Where(variant => !sharedVariants.Any(variant.Matches));

            // Concatenate the old shared and the new not present
            var allVariants = sharedVariants.Concat(missingVariants);

            // Replace variants
            _variants = allVariants.ToList();

            if (BlockData is not InterfaceBlockData interfaceBlockData) return;
            UpdatePossibleRoutines();
            UpdateDependencies();
            UpdateMissingDependencies();
            ApplyConditionToVariants();
        }

        private void UpdateDependencies()
        {
            _dependencies.Clear();
            if (BlockData is InterfaceBlockData interfaceBlockData)
            {
                var packageDepsSet = new HashSet<string>();
                InterfaceBlockData.ComputePackageDependencies(interfaceBlockData, this, packageDepsSet);
                _dependencies.AddRange(packageDepsSet);
            }
        }

        private void UpdateMissingDependencies()
        {
            _missingDependencies.Clear();

            if (BlockData is InterfaceBlockData)
            {
                if (Dependencies.Count == 0)
                {
                    UpdateDependencies();
                }
                _missingDependencies.AddRange(Dependencies.Where(packageId => !Utils.IsPackageInstalled(packageId)));
            }
        }

        private void UpdatePossibleRoutines()
        {
            _possibleRoutines.Clear();

            AppendPossibleRoutines(BlockData);
        }

        private void ApplyConditionToVariants()
        {
            foreach (VariantHandle variant in _variants)
            {
                if (_variantConditionStatus.ContainsKey(variant.MemberInfo.Name))
                {
                    _variantConditionStatus.TryGetValue(variant.MemberInfo.Name, out var result);
                    variant.OverrideCondition = () => result;
                }
            }
            _variantConditionStatus.Clear();
        }

        private static readonly IReadOnlyList<InstallationRoutine> EmptyList = new List<InstallationRoutine>();

        private void AppendPossibleRoutines(BlockData blockData)
        {
            if (_possibleRoutines.ContainsKey(blockData)) return;

            var interfaceBlockData = blockData as InterfaceBlockData;
            if (interfaceBlockData != null)
            {
                _possibleRoutines.Add(blockData, ComputePossibleInstallationRoutines(interfaceBlockData));
            }
            else
            {
                _possibleRoutines.Add(blockData, EmptyList);
            }

            // Gather dependencies BlockDatas
            var dependencies = (blockData.dependencies ?? Enumerable.Empty<string>()).Select(Utils.GetBlockData);

            if (interfaceBlockData != null)
            {
                // Concatenate Optional dependencies BlockData
                dependencies = dependencies.Concat(InterfaceBlockData.ComputeOptionalDependencies(interfaceBlockData, this));
            }

            foreach (var dependency in dependencies)
            {
                AppendPossibleRoutines(dependency);
            }
        }

        public void SetupForCheckpoint(BuildingBlock block)
        {
            var checkpoint = block.InstallationRoutineCheckpoint;
            var routine = Utils.GetInstallationRoutine(checkpoint.InstallationRoutineId);
            if (routine == null)
            {
                return;
            }

            var blockData = block.GetBlockData();
            Reset();

            BlockData = blockData;
            _variants = GetVariants(blockData, false).Select(variant => variant.ToSelection(false)).ToList();

            foreach (var variant in _variants)
            {
                // Value may come from installation checkpoint
                var variantCheckpoint = checkpoint.InstallationVariants
                    .FirstOrDefault(variantCheckpoint => variant.MemberInfo.Name == variantCheckpoint.MemberName);
                if (variantCheckpoint != null)
                {
                    variant.FromJson(variantCheckpoint.Value);
                }
                else
                {
                    // Or we may need to get it from the installation routine itself
                    var definitionVariant = routine.DefinitionVariants.FirstOrDefault(definitionVariant =>
                        definitionVariant.MemberInfo.Name == variant.MemberInfo.Name);
                    if (definitionVariant != null)
                    {
                        variant.RawValue = definitionVariant.RawValue;
                    }
                }
            }
        }

        public void Release(BlockData blockData, bool force = false)
        {
            // Do not release if not the original block (unless forced)
            if (!force && BlockData != blockData) return;

            Reset();
        }

        private void Reset()
        {
            BlockData = null;
            Completed = false;
            Canceled = false;
            _variants.Clear();
            _variantConditionStatus.Clear();
        }

        private IEnumerable<VariantHandle> GetVariantsRecursive(BlockData blockData)
        {
            var variantHandles = GetVariants(blockData);

            // Gather dependencies BlockDatas
            var dependencies = (blockData.dependencies ?? Enumerable.Empty<string>())
                .Select(Utils.GetBlockData);

            // Concatenate Optional dependencies BlockData
            if (blockData is InterfaceBlockData interfaceBlockData)
            {
                dependencies = dependencies.Concat(InterfaceBlockData.ComputeOptionalDependencies(interfaceBlockData, this));
            }

            // Fetch Variants
            var dependenciesHandles = dependencies.SelectMany(GetVariantsRecursive);

            return variantHandles
                .Union(dependenciesHandles)
                .GroupBy(variant => variant.MemberInfo.Name)
                .Select(variants => variants.First());
        }

        private IEnumerable<VariantHandle> GetVariants(BlockData blockData, bool testCondition = true)
        {
            if (blockData is InterfaceBlockData interfaceBlockData)
            {
                return GetVariants(interfaceBlockData.GetAvailableInstallationRoutines(), testCondition);
            }

            return Enumerable.Empty<VariantHandle>();
        }

        private IEnumerable<VariantHandle> GetVariants(IEnumerable<InstallationRoutine> routinesFilter, bool testCondition = true)
        {
            var allVariants = routinesFilter.SelectMany(routine =>
                routine.DefinitionVariants.Union(routine.ParameterVariants));
            foreach (var variant in allVariants.Where(variant => variant.Owner.Fits(this))) // variants relevant to current selection
            {
                var variantName = variant.MemberInfo.Name;
                _variantConditionStatus[variantName] = _variantConditionStatus.TryGetValue(variantName, out bool originalValue)
                    ? originalValue || variant.Condition() // If any condition for this same variant is true, should keep true (enabled the choice)
                    : variant.Condition();
            }
            return allVariants
                .Where(variant => variant.Attribute.Behavior == VariantAttribute.VariantBehavior.Definition
                                  || !testCondition || variant.Condition())
                .GroupBy(variant => variant.MemberInfo.Name)
                .Select(variants =>
                {
                    var firstVariant = variants.First();
                    if (firstVariant.Attribute.Default != null)
                    {
                        foreach (var variant in variants)
                        {
                            if (variant.RawValue.Equals(variant.Attribute.Default))
                            {
                                return variant; // try best to use the one matching default value for this variant
                            }
                        }
                        if (firstVariant.Attribute.Behavior == VariantAttribute.VariantBehavior.Parameter)
                        {
                            // If parameter, overriding to default parameter value
                            firstVariant.RawValue = firstVariant.Attribute.Default;
                            // Cannot do so for definition variant because there would be no matching installationRoutine available
                        }
                    }
                    return firstVariant;
                });
        }

        public InstallationRoutine ComputeIdealInstallationRoutine(
            IReadOnlyList<InstallationRoutine> routines)
        {
            if (Canceled) return null;

            var selectedRoutine = routines.Count() <= 1 ? routines.FirstOrDefault()
                : routines.FirstOrDefault(routine => routine.Fits(this));

            if (selectedRoutine != null)
            {
                selectedRoutine.ApplySelection(this);
            }
            return selectedRoutine;
        }

        // eliminate installation routine that's not compatible with current scene's existing routines
        public IReadOnlyList<InstallationRoutine> ComputePossibleInstallationRoutines(InterfaceBlockData blockData)
        {
            var routines = blockData.GetAvailableInstallationRoutines().ToArray();

            // From availableInstallationRoutines, we pick the definition variants we need to find
            var definitionVariants = routines.SelectMany(routine =>
                routine.DefinitionVariants).ToList();

            // Add definitionVariants Also present in the Selection
            definitionVariants.AddRange(this.Where(variant => !definitionVariants.Any(variant.Matches)));

            // We will find in the blocks in scene if any set one of the definitions
            var foundDefinitionVariants = Utils.GetBlocksInScene()
                .Where(block => block.GetBlockData() is InterfaceBlockData)
                .Select(block => block.GetInstallationRoutine())
                .SelectMany(installationRoutine => installationRoutine?.ValidDefinitionVariants ?? Enumerable.Empty<VariantHandle>())
                .Where(otherDefinitionVariant =>
                    definitionVariants.Any(definitionVariant => definitionVariant.Matches(otherDefinitionVariant)))
                .GroupBy(definitionVariant => definitionVariant.MemberInfo.Name)
                .Select(otherDefinitionVariants => otherDefinitionVariants.First())
                .ToArray();

            var possibleRoutines = routines;
            if (foundDefinitionVariants.Length > 0)
            {
                // As we've found definitions in the scene, we will filter by it
                possibleRoutines = routines.Where(routine => routine.Fits(foundDefinitionVariants)).ToArray();
            }

            // Backwards deduction: current inspected routines needs to be filtered by the possible routine from Selection
            if (possibleRoutines.Length != 0
                && PossibleRoutines != null
                && BlockData != null
                && BlockData.Id != blockData.Id
                && PossibleRoutines.TryGetValue(BlockData,
                    out var possibleRoutinesForSelectedBlock))
            {
                var selectedDefinitionVariants = possibleRoutinesForSelectedBlock.SelectMany(routine =>
                    routine.DefinitionVariants);

                possibleRoutines = possibleRoutines.Where(routine =>
                        // routine doesn't have any definition matched with selected, not filtering
                        routine.DefinitionVariants.All(definitionVariant => !selectedDefinitionVariants.Any(definitionVariant.Matches)) ||
                        // routine has matched definition and fits with selected variant, not filtering
                        routine.DefinitionVariants.Any(definitionVariant => selectedDefinitionVariants.Any(definitionVariant.Fits))
                    )
                    .ToArray();

            }

            return possibleRoutines;
        }

        public IEnumerator<VariantHandle> GetEnumerator() => _variants.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _variants.GetEnumerator();
        public int Count => _variants.Count;
        public VariantHandle this[int index] => _variants[index];


        public async Task<InstallationRoutine> SelectInstallationRoutine(InterfaceBlockData blockData)
        {
            var installationRoutines = ComputePossibleInstallationRoutines(blockData);
            // Early return if no routines available
            if (installationRoutines.Count == 0)
            {
                throw new MissingInstallationRoutineException($"There are no available installation routines");
            }

            // Generates the variants that need to be selected
            if (RequiresChoiceFor(installationRoutines))
            {
                // A choice is necessary
                {
                    var window = InstallationWindow.ShowWindowFor(this);
                    await WaitUntil(() => Completed);
                    window.Close();
                }
            }

            var selectedRoutine = ComputeIdealInstallationRoutine(installationRoutines);

            // Selection was canceled
            if (Canceled) return null;

            // Invalid or empty routine
            if (selectedRoutine == null || !selectedRoutine.FitsParameters(this))
            {
                throw new MissingInstallationRoutineException($"There are no available installation routines");
            }

            return selectedRoutine;
        }

        private static async Task WaitUntil(Func<bool> predicate, int sleep = 50)
        {
            while (!predicate())
            {
                await Task.Delay(sleep);
            }
        }
    }
}
