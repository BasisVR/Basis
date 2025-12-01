using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms.HVRUtility;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Blendshape Actuation")]
    public class BlendshapeActuation : MonoBehaviour, IHVRInitializable
    {
        private const int MaxAddresses = 256;
        private const float BlendshapeAtFullStrength = 100f;

        [SerializeField] private SkinnedMeshRenderer[] renderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private BlendshapeActuationDefinitionFile[] definitionFiles = Array.Empty<BlendshapeActuationDefinitionFile>();
        [SerializeField] private BlendshapeActuationDefinition[] definitions = Array.Empty<BlendshapeActuationDefinition>();
        [SerializeField] private AddressOverride[] addressOverrides = Array.Empty<AddressOverride>();

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;

        private Dictionary<int, int> _addessIdToBaseIndex = new();
        private ComputedActuator[] _computedActuators;
        private ComputedActuator[][] _addressBaseIndexToActuators;
        private Dictionary<int, (float, float)> _addressToStreamedLowerUpper;

        #region NetworkingFields
        // Can be null due to:
        // - Application with no network, or
        // - Network late initialization.
        // Nullability is needed for local tests without initialization scene.
        // - Becomes non-null after HVRAvatarComms.OnAvatarNetworkReady is successfully invoked
        [NonSerialized] internal MutualizedFeatureInterpolator featureInterpolator;

        public string[] debugAddresses;

        #endregion

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

            if (acquisition == null)
            {
                acquisition = AcquisitionService.SceneInstance;
            }

            renderers = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(renderers);
            definitionFiles = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(definitionFiles);
            definitions = HVRCommsUtil.SlowSanitizeEndUserProvidedStructArray(definitions);
        }

        private void OnAddressUpdated(int address, float inRange)
        {
            if (!_addessIdToBaseIndex.TryGetValue(address, out var baseIndex)) return;

            // TODO: Might need to queue and delay this change so that it executes on the Update loop.

            var actuatorsForThisAddress = _addressBaseIndexToActuators[baseIndex];
            if (actuatorsForThisAddress == null) return; // There may be no actuator for an address when it does not exist in the renderers.

            foreach (var actuator in actuatorsForThisAddress)
            {
                Actuate(actuator, inRange);
            }

            if (featureInterpolator != null)
            {
                featureInterpolator.SubmitAbsolute(baseIndex, inRange);
            }
        }

        private void OnInterpolatedDataChanged(float[] current)
        {
            foreach (var actuator in _computedActuators)
            {
                var absolute = current[actuator.AddressIndex];
                Actuate(actuator, absolute);
            }
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
                foreach (var blendshapeIndex in target.BlendshapeIndices)
                {
                    target.Renderer.SetBlendShapeWeight(blendshapeIndex, output0100);
                }
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            var allDefinitions = definitions
                .Concat(definitionFiles.SelectMany(file => file.definitions))
                .ToArray();

            var smrToBlendshapeNames = ResolveSmrToBlendshapeNames(renderers);

            // All streamed avatar feature values are between 0 and 1.
            // If we want to stream values outside of this range (i.e. [-1; 1]), we need to collect all
            // possible InStart and InEnd values in order to lerp in that range.
            _addressToStreamedLowerUpper = allDefinitions
                .GroupBy(definition => HVRAddress.AddressToId(definition.address))
                .ToDictionary(grouping => grouping.Key, grouping =>
                {
                    var inValuesForThisAddress = grouping
                        // Reminder that InStart may be greater than InEnd.
                        // We want the lower bound, not the minimum of InStart.
                        .SelectMany(definition => new [] { definition.inStart, definition.inEnd })
                        .ToArray();
                    return (inValuesForThisAddress.Min(), inValuesForThisAddress.Max());
                });

            _computedActuators = allDefinitions.Select(definition =>
                {
                    var actuatorTargets = ComputeTargets(smrToBlendshapeNames, definition.blendshapes, definition.onlyFirstMatch);
                    if (actuatorTargets.Length == 0) return null;

                    var (lower, upper) = _addressToStreamedLowerUpper[HVRAddress.AddressToId(definition.address)];
                    return new ComputedActuator
                    {
                        // The AddressIndex field is filled later.
                        InStart = definition.inStart,
                        InEnd = definition.inEnd,
                        OutStart = definition.outStart,
                        OutEnd = definition.outEnd,
                        StreamedLower = lower,
                        StreamedUpper = upper,
                        UseCurve = definition.useCurve,
                        Curve = definition.curve,
                        Targets = actuatorTargets,
                        RequestedFeature = new RequestedFeature
                        {
                            identifier = definition.address,
                            address = HVRAddress.AddressToId(definition.address),
                            lower = lower,
                            upper = upper
                        }
                    };
                })
                .Where(actuator => actuator != null)
                .ToArray();

            var allAddressesThatAreEffectivelyActuated = _computedActuators
                .Select(actuator => actuator.RequestedFeature.address)
                .Distinct()
                .ToArray();
            var allAddessesThatAreEffectivelyActuatedAsString = _computedActuators
                .Select(actuator => actuator.RequestedFeature.identifier)
                .Distinct()
                .ToArray();
            debugAddresses = allAddessesThatAreEffectivelyActuatedAsString;

            _addessIdToBaseIndex = MakeIndexDictionary(allAddressesThatAreEffectivelyActuated);
            if (_addessIdToBaseIndex.Count > MaxAddresses)
            {
                Debug.LogError($"Exceeded max {MaxAddresses} addresses allowed in an actuator.");
                enabled = false;
                return;
            }

            foreach (var computedActuator in _computedActuators)
            {
                computedActuator.AddressIndex = _addessIdToBaseIndex[computedActuator.RequestedFeature.address];
            }

            _addressBaseIndexToActuators = new ComputedActuator[_addessIdToBaseIndex.Count][];
            foreach (var computedActuator in _computedActuators.GroupBy(actuator => actuator.AddressIndex, actuator => actuator))
            {
                _addressBaseIndexToActuators[computedActuator.Key] = computedActuator.ToArray();
            }

            if (isWearer)
            {
                acquisition.RegisterAddresses(_addessIdToBaseIndex.Keys.ToArray(), OnAddressUpdated);
            }
        }

        public static Dictionary<SkinnedMeshRenderer, List<string>> ResolveSmrToBlendshapeNames(SkinnedMeshRenderer[] smrs)
        {
            var smrToBlendshapeNames = new Dictionary<SkinnedMeshRenderer, List<string>>();
            foreach (var smr in smrs)
            {
                var mesh = smr.sharedMesh;
                smrToBlendshapeNames.Add(smr, Enumerable.Range(0, mesh.blendShapeCount)
                    .Select(i => mesh.GetBlendShapeName(i))
                    .ToList());
            }

            return smrToBlendshapeNames;
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isLocallyOwned)
        {
            HVRLogging.ProtocolDebug("OnReadyBothAvatarAndNetwork called on BlendshapeActuation.");
            // FIXME: We should be using the computed actuators instead of the address base, assuming that
            // the list of blendshapes is the same local and remote (no local-only or remote-only blendshapes).
            featureInterpolator = CommsNetworking.UsingMutualizedInterpolator(avatar, MakeMutualized(), OnInterpolatedDataChanged);

            var overrides = definitionFiles
                .SelectMany(file => file.addressOverrides)
                .Concat(addressOverrides)
                .Where(it => it.overrideDefaultValue)
                .ToArray();
            foreach (var addressOverride in overrides)
            {
                if (_addessIdToBaseIndex.TryGetValue(HVRAddress.AddressToId(addressOverride.address), out var key))
                {
                    featureInterpolator.SubmitAbsolute(key, addressOverride.defaultValue);
                }
            }
        }

        private List<MutualizedInterpolationRange> MakeMutualized()
        {
            return _addessIdToBaseIndex.Keys
                .Select(address =>
                {
                    // The key order are different between addressBase and addressToStreamedLowerUpper
                    var (lower, upper) = _addressToStreamedLowerUpper[address];
                    return new MutualizedInterpolationRange
                    {
                        address = address,
                        lower = lower,
                        upper = upper,
                    };
                })
                .ToList();
        }

        private Dictionary<int, int> MakeIndexDictionary(int[] addressBase)
        {
            var dictionary = new Dictionary<int, int>();
            for (var index = 0; index < addressBase.Length; index++)
            {
                var se = addressBase[index];
                dictionary[se] = index;
            }

            return dictionary;
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
            avatar.OnAvatarReady -= OnHVRAvatarReady;

            acquisition.UnregisterAddresses(_addessIdToBaseIndex.Keys.ToArray(), OnAddressUpdated);
        }

        private void ResetAllBlendshapesToZero()
        {
            foreach (var computedActuator in _computedActuators)
            {
                foreach (var target in computedActuator.Targets)
                {
                    if (null != target.Renderer && null != target.Renderer.sharedMesh)
                    {
                        var blendshapeCount = target.Renderer.sharedMesh.blendShapeCount;
                        foreach (var blendshapeIndex in target.BlendshapeIndices)
                        {
                            if (blendshapeIndex < blendshapeCount)
                            {
                                target.Renderer.SetBlendShapeWeight(blendshapeIndex, 0);
                            }
                        }
                    }
                }
            }
        }

        public static ComputedActuatorTarget[] ComputeTargets(Dictionary<SkinnedMeshRenderer, List<string>> smrToBlendshapeNames, string[] definitionBlendshapes, bool onlyFirstMatch)
        {
            var actuatorTargets = new List<ComputedActuatorTarget>();
            foreach (var pair in smrToBlendshapeNames)
            {
                var indices = definitionBlendshapes
                    .Select(toFind => pair.Value.IndexOf(toFind))
                    .Where(i => i >= 0)
                    .ToArray();

                if (indices.Length > 0)
                {
                    if (onlyFirstMatch)
                    {
                        actuatorTargets.Add(new ComputedActuatorTarget
                        {
                            Renderer = pair.Key,
                            BlendshapeIndices = new[] { indices[0] }
                        });
                    }
                    else
                    {
                        actuatorTargets.Add(new ComputedActuatorTarget
                        {
                            Renderer = pair.Key,
                            BlendshapeIndices = indices
                        });
                    }
                }
            }

            return actuatorTargets.ToArray();
        }

        private class ComputedActuator
        {
            public int AddressIndex;
            public float StreamedLower;
            public float StreamedUpper;
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
        }
    }
}
