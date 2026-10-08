using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms.HVRUtility;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AutoStaticsCleanup]
    [AddComponentMenu("HVR.Basis/Comms/Blendshape Actuation")]
    public partial class BlendshapeActuation : MonoBehaviour, IHVRInitializable
    {
        // This is a class originally created in September 2024, which as of 2026 sets the value of blendshapes based on addresses.
        // Originally, this class also took care of networking the addresses, but it is no longer the case since the addition of HVRVariableNetworking in April 2026 which now takes that responsibility.
        // There are still leftover traces of the old networking (e.g. range calculation, indexing) in this class, so this class could still be greatly simplified.

        private const int MaxAddresses = 256;
        private const float BlendshapeAtFullStrength = 100f;

        [SerializeField] private SkinnedMeshRenderer[] renderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private BlendshapeActuationDefinitionFile[] definitionFiles = Array.Empty<BlendshapeActuationDefinitionFile>();
        [SerializeField] private BlendshapeActuationDefinition[] definitions = Array.Empty<BlendshapeActuationDefinition>();
        [SerializeField] private AddressOverride[] addressOverrides = Array.Empty<AddressOverride>();

        [HideInInspector] [SerializeField] private BasisAvatar avatar;

        private HVRAvatarComms comms;

        private Dictionary<int, int> _addessIdToBaseIndex = new();
        private readonly Dictionary<int, float> _latestAbsoluteByAddress = new();
        private ComputedActuator[] _computedActuators;
        private ComputedActuator[][] _addressBaseIndexToActuators;
        private Dictionary<int, (float, float)> _addressToStreamedLowerUpper;
        private AddressOverride[] _defaultOverrides = Array.Empty<AddressOverride>();
        private FaceTrackingActivityRelay _activityRelay;
        private bool _isWearer;
        private bool _trackingActive;
        public bool IsTrackingActive => _trackingActive;

        public string[] debugAddresses;

        public void AutoDefine(BlendshapeActuationDefinitionFile[] providedDefinitionFiles, List<SkinnedMeshRenderer> providedSmrs)
        {
            definitionFiles = providedDefinitionFiles;
            renderers = providedSmrs.ToArray();
        }

        private void Awake()
        {
            if (avatar == null)
            {
                avatar = HVRCommsUtil.GetAvatar(this);
            }

            renderers = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(renderers);
            definitionFiles = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(definitionFiles);
            definitions = HVRCommsUtil.SlowSanitizeEndUserProvidedStructArray(definitions);
        }

        private void OnAddressUpdated(int address, float inRange)
        {
            ApplyAddressValue(address, inRange);
        }

        private static void Actuate(ComputedActuator actuator, float inRange)
        {
            var intermediate01 = Mathf.InverseLerp(actuator.InStart, actuator.InEnd, inRange);
            if (actuator.UseCurve)
            {
                intermediate01 = actuator.Curve.Evaluate(intermediate01);
            }
            var outputWild = Mathf.Lerp(actuator.OutStart, actuator.OutEnd, intermediate01);
            var output01 = Mathf.Clamp01(outputWild);
            var output0100 = output01 * BlendshapeAtFullStrength;

            foreach (var target in actuator.Targets)
            {
                var renderer = target.Renderer;
                if (null == renderer)
                {
                    continue;
                }
                var lastWeights = target.LastWeights;
                foreach (var blendshapeIndex in target.BlendshapeIndices)
                {
                    if (lastWeights[blendshapeIndex] != output0100)
                    {
                        renderer.SetBlendShapeWeight(blendshapeIndex, output0100);
                        lastWeights[blendshapeIndex] = output0100;
                    }
                }
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;
            comms = HVRCommsUtil.GetComms(avatar);
            _activityRelay = FaceTrackingActivityRelay.GetOrCreate(avatar, out var relayCreated);
            if (relayCreated)
            {
                _activityRelay.OnHVRAvatarReady(isWearer);
            }
            if (_activityRelay != null)
            {
                _activityRelay.OnTrackingActivityChanged -= OnTrackingActivityUpdated;
                _activityRelay.OnTrackingActivityChanged += OnTrackingActivityUpdated;
            }
            _trackingActive = _activityRelay != null && _activityRelay.IsTrackingActive;

            var totalDefinitionCount = definitions.Length;
            foreach (var file in definitionFiles)
            {
                totalDefinitionCount += file.definitions.Length;
            }
            var allDefinitions = new BlendshapeActuationDefinition[totalDefinitionCount];
            Array.Copy(definitions, allDefinitions, definitions.Length);
            var allDefinitionsWriteIndex = definitions.Length;
            foreach (var file in definitionFiles)
            {
                var fileDefinitions = file.definitions;
                Array.Copy(fileDefinitions, 0, allDefinitions, allDefinitionsWriteIndex, fileDefinitions.Length);
                allDefinitionsWriteIndex += fileDefinitions.Length;
            }

            var smrToBlendshapeIndices = ResolveSmrToBlendshapeIndices(renderers);

            // All streamed avatar feature values are between 0 and 1.
            // If we want to stream values outside of this range (i.e. [-1; 1]), we need to collect all
            // possible InStart and InEnd values in order to lerp in that range.
            // Reminder that InStart may be greater than InEnd.
            // We want the lower bound, not the minimum of InStart.
            var addressIds = new int[allDefinitions.Length];
            _addressToStreamedLowerUpper = new Dictionary<int, (float, float)>(allDefinitions.Length);
            for (var i = 0; i < allDefinitions.Length; i++)
            {
                var definition = allDefinitions[i];
                var addressId = HVRAddress.AddressToId(definition.address);
                addressIds[i] = addressId;
                var definitionLower = Mathf.Min(definition.inStart, definition.inEnd);
                var definitionUpper = Mathf.Max(definition.inStart, definition.inEnd);
                if (_addressToStreamedLowerUpper.TryGetValue(addressId, out var existing))
                {
                    _addressToStreamedLowerUpper[addressId] = (Mathf.Min(existing.Item1, definitionLower), Mathf.Max(existing.Item2, definitionUpper));
                }
                else
                {
                    _addressToStreamedLowerUpper[addressId] = (definitionLower, definitionUpper);
                }
            }

            var computedActuators = new List<ComputedActuator>(allDefinitions.Length);
            _addessIdToBaseIndex = new Dictionary<int, int>(allDefinitions.Length);
            var debugAddressList = new List<string>();
            var scratchTargets = new List<ComputedActuatorTarget>();
            var scratchIndices = new List<int>();
            for (var i = 0; i < allDefinitions.Length; i++)
            {
                var definition = allDefinitions[i];
                var actuatorTargets = ComputeTargets(smrToBlendshapeIndices, definition.blendshapes, definition.onlyFirstMatch, scratchTargets, scratchIndices);
                if (actuatorTargets.Length == 0)
                {
                    continue;
                }

                var address = addressIds[i];
                if (!_addessIdToBaseIndex.TryGetValue(address, out var addressIndex))
                {
                    addressIndex = _addessIdToBaseIndex.Count;
                    _addessIdToBaseIndex.Add(address, addressIndex);
                    debugAddressList.Add(definition.address);
                }

                var (lower, upper) = _addressToStreamedLowerUpper[address];
                computedActuators.Add(new ComputedActuator
                {
                    AddressIndex = addressIndex,
                    InStart = definition.inStart,
                    InEnd = definition.inEnd,
                    OutStart = definition.outStart,
                    OutEnd = definition.outEnd,
                    UseCurve = definition.useCurve,
                    Curve = definition.curve,
                    Targets = actuatorTargets,
                    RequestedFeature = new RequestedFeature
                    {
                        identifier = definition.address,
                        address = address,
                        lower = lower,
                        upper = upper
                    }
                });
            }
            _computedActuators = computedActuators.ToArray();
            debugAddresses = debugAddressList.ToArray();

            if (_addessIdToBaseIndex.Count > MaxAddresses)
            {
                Debug.LogError($"Exceeded max {MaxAddresses} addresses allowed in an actuator.");
                enabled = false;
                return;
            }

            var actuatorsPerAddressIndex = new int[_addessIdToBaseIndex.Count];
            foreach (var computedActuator in _computedActuators)
            {
                actuatorsPerAddressIndex[computedActuator.AddressIndex]++;
            }
            _addressBaseIndexToActuators = new ComputedActuator[_addessIdToBaseIndex.Count][];
            for (var index = 0; index < _addressBaseIndexToActuators.Length; index++)
            {
                _addressBaseIndexToActuators[index] = new ComputedActuator[actuatorsPerAddressIndex[index]];
            }
            var actuatorWriteIndexPerAddressIndex = new int[_addessIdToBaseIndex.Count];
            foreach (var computedActuator in _computedActuators)
            {
                var addressIndex = computedActuator.AddressIndex;
                _addressBaseIndexToActuators[addressIndex][actuatorWriteIndexPerAddressIndex[addressIndex]++] = computedActuator;
            }

            var lastWeightsByRenderer = new Dictionary<SkinnedMeshRenderer, float[]>();
            var writableTargets = new List<ComputedActuatorTarget>();
            foreach (var computedActuator in _computedActuators)
            {
                writableTargets.Clear();
                foreach (var target in computedActuator.Targets)
                {
                    if (target.Renderer == null)
                    {
                        continue;
                    }
                    if (!lastWeightsByRenderer.TryGetValue(target.Renderer, out var lastWeights))
                    {
                        var mesh = target.Renderer.sharedMesh;
                        lastWeights = new float[mesh != null ? mesh.blendShapeCount : 0];
                        for (var i = 0; i < lastWeights.Length; i++)
                        {
                            lastWeights[i] = float.NaN;
                        }
                        lastWeightsByRenderer.Add(target.Renderer, lastWeights);
                    }

                    var withinMesh = true;
                    foreach (var blendshapeIndex in target.BlendshapeIndices)
                    {
                        if (blendshapeIndex >= lastWeights.Length)
                        {
                            withinMesh = false;
                            break;
                        }
                    }
                    if (!withinMesh)
                    {
                        continue;
                    }

                    target.LastWeights = lastWeights;
                    writableTargets.Add(target);
                }

                if (writableTargets.Count != computedActuator.Targets.Length)
                {
                    computedActuator.Targets = writableTargets.Count == 0 ? Array.Empty<ComputedActuatorTarget>() : writableTargets.ToArray();
                }
            }

            List<AddressOverride> defaultOverrides = null;
            foreach (var file in definitionFiles)
            {
                foreach (var addressOverride in file.addressOverrides)
                {
                    if (addressOverride.overrideDefaultValue)
                    {
                        (defaultOverrides ??= new List<AddressOverride>()).Add(addressOverride);
                    }
                }
            }
            foreach (var addressOverride in addressOverrides)
            {
                if (addressOverride.overrideDefaultValue)
                {
                    (defaultOverrides ??= new List<AddressOverride>()).Add(addressOverride);
                }
            }
            _defaultOverrides = defaultOverrides == null ? Array.Empty<AddressOverride>() : defaultOverrides.ToArray();

            var addressesToListenTo = new int[_addessIdToBaseIndex.Count];
            _addessIdToBaseIndex.Keys.CopyTo(addressesToListenTo, 0);
            comms.VariableStore.RegisterAddresses(addressesToListenTo, OnAddressUpdated);
        }

        private static readonly ConditionalWeakTable<Mesh, Dictionary<string, int>> MeshBlendshapeIndices = new();

        public static Dictionary<SkinnedMeshRenderer, Dictionary<string, int>> ResolveSmrToBlendshapeIndices(SkinnedMeshRenderer[] smrs)
        {
            var smrToBlendshapeIndices = new Dictionary<SkinnedMeshRenderer, Dictionary<string, int>>(smrs.Length);
            foreach (var smr in smrs)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                if (!MeshBlendshapeIndices.TryGetValue(mesh, out var nameToIndex))
                {
                    var blendshapeCount = mesh.blendShapeCount;
                    nameToIndex = new Dictionary<string, int>(blendshapeCount);
                    for (var i = 0; i < blendshapeCount; i++)
                    {
                        var blendshapeName = mesh.GetBlendShapeName(i);
                        nameToIndex.TryAdd(blendshapeName, i);
                    }
                    MeshBlendshapeIndices.Add(mesh, nameToIndex);
                }
                smrToBlendshapeIndices.Add(smr, nameToIndex);
            }

            return smrToBlendshapeIndices;
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isLocallyOwned)
        {
         //   HVRLogging.ProtocolDebug("OnReadyBothAvatarAndNetwork called on BlendshapeActuation.");
            _isWearer = isLocallyOwned;
            // FIXME: We should be using the computed actuators instead of the address base, assuming that
            // the list of blendshapes is the same local and remote (no local-only or remote-only blendshapes).

            var addressIdToDefault = new Dictionary<int, float>();
            foreach (var defaultOverride in _defaultOverrides)
            {
                addressIdToDefault[HVRAddress.AddressToId(defaultOverride.address)] = defaultOverride.defaultValue;
            }

            foreach (var actuator in _computedActuators)
            {
                comms.RequireVariable(new HVRVariable
                {
                    addressId = actuator.RequestedFeature.address,
                    initialValue = addressIdToDefault.GetValueOrDefault(actuator.RequestedFeature.address, 0f),
                    variableTypeCode = HVRVariableTypeCode.Float,
                    needsInterpolation = true,
                    min = Mathf.Min(actuator.InStart, actuator.InEnd),
                    max = Mathf.Max(actuator.InStart, actuator.InEnd),
                });
            }
        }

        private void OnDisable()
        {
            if (_computedActuators != null)
            {
                ResetAllBlendshapesToZero();
            }
        }

        private void OnDestroy()
        {
            if (avatar != null)
            {
                avatar.OnAvatarReady -= OnHVRAvatarReady;
            }

            if (_activityRelay != null)
            {
                _activityRelay.OnTrackingActivityChanged -= OnTrackingActivityUpdated;
            }

            if (_computedActuators != null)
            {
                var addressIdToListenTo = new HashSet<int>();
                foreach (var computedActuator in _computedActuators)
                {
                    addressIdToListenTo.Add(computedActuator.RequestedFeature.address);
                }
                comms.VariableStore.UnregisterAddresses(addressIdToListenTo.ToArray(), OnAddressUpdated);
            }
        }

        private void OnTrackingActivityUpdated(bool isTrackingActive)
        {
            if (_trackingActive == isTrackingActive)
            {
                return;
            }

            _trackingActive = isTrackingActive;
            if (_trackingActive)
            {
                if (_isWearer)
                {
                    ApplyDefaultOverrides(); // 2026: This might not be necessary as the function called below will re-submit new values for the addresses, which will be carried by OnAddressUpdated. Still to be checked.
                    ReplayLatestTrackedValuesToNetwork();
                }
                return;
            }

            ResetAllBlendshapesToZero(); // 2026: This might not be necessary as the function called below will re-submit new values for the addresses, which will be carried by OnAddressUpdated. Still to be checked.
            _latestAbsoluteByAddress.Clear();
            if (_isWearer)
            {
                SubmitNeutralValuesToNetwork();
            }
        }

        private void ApplyAddressValue(int address, float inRange)
        {
            if (!_trackingActive || !_addessIdToBaseIndex.TryGetValue(address, out var baseIndex))
            {
                return;
            }

            var actuatorsForThisAddress = _addressBaseIndexToActuators[baseIndex];
            if (actuatorsForThisAddress == null)
            {
                return;
            }

            if (_isWearer)
            {
                _latestAbsoluteByAddress[address] = inRange;
            }
            foreach (var actuator in actuatorsForThisAddress)
            {
                Actuate(actuator, inRange);
            }
        }

        private void ApplyDefaultOverrides()
        {
            foreach (var addressOverride in _defaultOverrides)
            {
                ApplyAddressValue(HVRAddress.AddressToId(addressOverride.address), addressOverride.defaultValue);
            }
        }

        private void ReplayLatestTrackedValuesToNetwork()
        {
            if (!_isWearer)
            {
                return;
            }

            // We need to make a copy because comms.VariableStore.Submit will cause the data to be modified
            var copy = _latestAbsoluteByAddress.ToList();
            foreach (var pair in copy)
            {
                comms.VariableStore.SubmitOrDefineDefaultValue(pair.Key, pair.Value);
            }
        }

        private void SubmitDefaultOverridesToNetwork()
        {
            if (!_isWearer)
            {
                return;
            }

            foreach (var addressOverride in _defaultOverrides)
            {
                var addressId = HVRAddress.AddressToId(addressOverride.address);
                comms.VariableStore.SubmitOrDefineDefaultValue(addressId, addressOverride.defaultValue);
            }
        }

        private void SubmitNeutralValuesToNetwork()
        {
            if (!_isWearer)
            {
                return;
            }

            foreach (var addressId in _addessIdToBaseIndex.Keys)
            {
                comms.VariableStore.SubmitOrDefineDefaultValue(addressId, 0f);
            }
        }

        private void ResetAllBlendshapesToZero()
        {
            if (_computedActuators == null)
            {
                return;
            }

            foreach (var computedActuator in _computedActuators)
            {
                foreach (var target in computedActuator.Targets)
                {
                    if (null != target.Renderer && null != target.Renderer.sharedMesh)
                    {
                        var blendshapeCount = target.Renderer.sharedMesh.blendShapeCount;
                        var lastWeights = target.LastWeights;
                        foreach (var blendshapeIndex in target.BlendshapeIndices)
                        {
                            if (blendshapeIndex < blendshapeCount)
                            {
                                target.Renderer.SetBlendShapeWeight(blendshapeIndex, 0);
                                if (lastWeights != null && blendshapeIndex < lastWeights.Length)
                                {
                                    lastWeights[blendshapeIndex] = 0f;
                                }
                            }
                        }
                    }
                }
            }
        }

        public static ComputedActuatorTarget[] ComputeTargets(Dictionary<SkinnedMeshRenderer, Dictionary<string, int>> smrToBlendshapeIndices, string[] definitionBlendshapes, bool onlyFirstMatch)
        {
            return ComputeTargets(smrToBlendshapeIndices, definitionBlendshapes, onlyFirstMatch, new List<ComputedActuatorTarget>(), new List<int>());
        }

        public static ComputedActuatorTarget[] ComputeTargets(Dictionary<SkinnedMeshRenderer, Dictionary<string, int>> smrToBlendshapeIndices, string[] definitionBlendshapes, bool onlyFirstMatch, List<ComputedActuatorTarget> scratchTargets, List<int> scratchIndices)
        {
            scratchTargets.Clear();
            foreach (var pair in smrToBlendshapeIndices)
            {
                var nameToIndex = pair.Value;
                if (onlyFirstMatch)
                {
                    foreach (var toFind in definitionBlendshapes)
                    {
                        if (nameToIndex.TryGetValue(toFind, out var index))
                        {
                            scratchTargets.Add(new ComputedActuatorTarget
                            {
                                Renderer = pair.Key,
                                BlendshapeIndices = new[] { index }
                            });
                            break;
                        }
                    }
                }
                else
                {
                    scratchIndices.Clear();
                    foreach (var toFind in definitionBlendshapes)
                    {
                        if (nameToIndex.TryGetValue(toFind, out var index))
                        {
                            scratchIndices.Add(index);
                        }
                    }
                    if (scratchIndices.Count > 0)
                    {
                        scratchTargets.Add(new ComputedActuatorTarget
                        {
                            Renderer = pair.Key,
                            BlendshapeIndices = scratchIndices.ToArray()
                        });
                    }
                }
            }

            return scratchTargets.Count == 0 ? Array.Empty<ComputedActuatorTarget>() : scratchTargets.ToArray();
        }

        private class ComputedActuator
        {
            public int AddressIndex;
            public float InStart;
            public float InEnd;
            public float OutStart;
            public float OutEnd;
            public bool UseCurve;
            public AnimationCurve Curve;
            public ComputedActuatorTarget[] Targets;
            public RequestedFeature RequestedFeature;
        }

        public class ComputedActuatorTarget
        {
            public SkinnedMeshRenderer Renderer;
            public int[] BlendshapeIndices;
            internal float[] LastWeights;
        }

        private class RequestedFeature
        {
            public string identifier;
            public int address;
            public float lower;
            public float upper;
        }
    }
}
