using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Blendshape Actuation")]
    public class BlendshapeActuation : MonoBehaviour, ICommsNetworkable
    {
        private const int MaxAddresses = 256;
        private const float BlendshapeAtFullStrength = 100f;

        [SerializeField] private SkinnedMeshRenderer[] renderers = Array.Empty<SkinnedMeshRenderer>();
        [SerializeField] private BlendshapeActuationDefinitionFile[] definitionFiles = Array.Empty<BlendshapeActuationDefinitionFile>();
        [SerializeField] private BlendshapeActuationDefinition[] definitions = Array.Empty<BlendshapeActuationDefinition>();
        
        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private FeatureNetworking featureNetworking;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;
        
        private Dictionary<string, int> _addressBase = new Dictionary<string, int>();
        private ComputedActuator[] _computedActuators;
        private ComputedActuator[][] _addressBaseIndexToActuators;
        
#region NetworkingFields
        private int _guidIndex;
        // Can be null due to:
        // - Application with no network, or
        // - Network late initialization.
        // Nullability is needed for local tests without initialization scene.
        // - Becomes non-null after HVRAvatarComms.OnAvatarNetworkReady is successfully invoked
        private FeatureInterpolator _featureInterpolator;
        private bool _avatarReady;
        private bool _networkReady;
        private bool _dualInitialized;

        #endregion

        private void Awake()
        {
            if (avatar == null) avatar = CommsUtil.GetAvatar(this);
            if (featureNetworking == null) featureNetworking = CommsUtil.FeatureNetworkingFromAvatar(avatar);
            if (acquisition == null) acquisition = AcquisitionService.SceneInstance;
            
            renderers = CommsUtil.SlowSanitizeEndUserProvidedObjectArray(renderers);
            definitionFiles = CommsUtil.SlowSanitizeEndUserProvidedObjectArray(definitionFiles);
            definitions = CommsUtil.SlowSanitizeEndUserProvidedStructArray(definitions);

            avatar.OnAvatarReady -= OnAvatarReady;
            avatar.OnAvatarReady += OnAvatarReady;
        }

        private void OnAddressUpdated(string address, float inRange)
        {
            if (!_addressBase.TryGetValue(address, out var index)) return;
            
            // TODO: Might need to queue and delay this change so that it executes on the Update loop.

            var actuatorsForThisAddress = _addressBaseIndexToActuators[index];
            if (actuatorsForThisAddress == null) return; // There may be no actuator for an address when it does not exist in the renderers.
            
            var lower = 0f;
            var upper = 0f;
            foreach (var actuator in actuatorsForThisAddress)
            {
                Actuate(actuator, inRange);
                lower = actuator.StreamedLower;
                upper = actuator.StreamedUpper;
            }
            
            if (_featureInterpolator != null)
            {
                var streamed01 = Mathf.InverseLerp(lower, upper, inRange);
                _featureInterpolator.Store(index, streamed01);
            }
        }

        private void OnInterpolatedDataChanged(float[] current)
        {
            foreach (var actuator in _computedActuators)
            {
                var streamed01 = current[actuator.AddressIndex];
                var inRange = Mathf.Lerp(actuator.StreamedLower, actuator.StreamedUpper, streamed01);
                
                Actuate(actuator, inRange);
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

        private void OnAvatarReady(bool isWearer)
        {
            var allDefinitions = definitions
                .Concat(definitionFiles.SelectMany(file => file.definitions))
                .ToArray();
            _addressBase = MakeIndexDictionary(allDefinitions.Select(definition => definition.address).Distinct().ToArray());
            
            if (_addressBase.Count > MaxAddresses)
            {
                Debug.LogError($"Exceeded max {MaxAddresses} addresses allowed in an actuator.");
                enabled = false;
                return;
            }

            var smrToBlendshapeNames = new Dictionary<SkinnedMeshRenderer, List<string>>();
            foreach (var smr in renderers)
            {
                var mesh = smr.sharedMesh;
                smrToBlendshapeNames.Add(smr, Enumerable.Range(0, mesh.blendShapeCount)
                    .Select(i => mesh.GetBlendShapeName(i))
                    .ToList());
            }

            // All streamed avatar feature values are between 0 and 1.
            // If we want to stream values outside of this range (i.e. [-1; 1]), we need to collect all
            // possible InStart and InEnd values in order to lerp in that range.
            var addressToStreamedLowerUpper = allDefinitions
                .GroupBy(definition => definition.address)
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

                    var (lower, upper) = addressToStreamedLowerUpper[definition.address];
                    return new ComputedActuator
                    {
                        AddressIndex = _addressBase[definition.address],
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
                            lower = lower,
                            upper = upper
                        }
                    };
                })
                .Where(actuator => actuator != null)
                .ToArray();

            _addressBaseIndexToActuators = new ComputedActuator[_addressBase.Count][];
            foreach (var computedActuator in _computedActuators.GroupBy(actuator => actuator.AddressIndex, actuator => actuator))
            {
                _addressBaseIndexToActuators[computedActuator.Key] = computedActuator.ToArray();
            }
            
            if (isWearer)
            {
                acquisition.RegisterAddresses(_addressBase.Keys.ToArray(), OnAddressUpdated);
            }

            _avatarReady = true;
            TryOnAvatarIsNetworkable();
        }

        public void OnGuidAssigned(int guidIndex, Guid guid)
        {
            _guidIndex = guidIndex;
            
            _networkReady = true;
            TryOnAvatarIsNetworkable();
        }

        private void TryOnAvatarIsNetworkable()
        {
            // HACK: Work around an issue where remote and local avatars trigger
            // OnAvatarReady and OnAvatarNetworkReady in a different order.

            if (_avatarReady && _networkReady && !_dualInitialized)
            {
                _dualInitialized = true;
                OnAvatarFullyNetworkable();
            }
        }

        private void OnAvatarFullyNetworkable()
        {
            // FIXME: We should be using the computed actuators instead of the address base, assuming that
            // the list of blendshapes is the same local and remote (no local-only or remote-only blendshapes).
            _featureInterpolator = featureNetworking.NewInterpolator(_guidIndex, _addressBase.Count, OnInterpolatedDataChanged);
            
            // FIXME: Add default values in the blendshape actuation file
            if (_addressBase.TryGetValue("FT/v2/EyeLidLeft", out var indexLeft)) _featureInterpolator.Store(indexLeft, 0.8f);
            if (_addressBase.TryGetValue("FT/v2/EyeLidRight", out var indexRight)) _featureInterpolator.Store(indexRight, 0.8f);
        }

        private Dictionary<string, int> MakeIndexDictionary(string[] addressBase)
        {
            var dictionary = new Dictionary<string, int>();
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
            avatar.OnAvatarReady -= OnAvatarReady;
            
            acquisition.UnregisterAddresses(_addressBase.Keys.ToArray(), OnAddressUpdated);

            if (_featureInterpolator != null)
            {
                _featureInterpolator.Unregister();
                _featureInterpolator = null;
            }
        }

        private void ResetAllBlendshapesToZero()
        {
            foreach (ComputedActuator computedActuator in _computedActuators)
            {
                if (computedActuator != null)
                {
                    foreach (ComputedActuatorTarget target in computedActuator.Targets)
                    {
                        if (target != null)
                        {
                            foreach (int blendshapeIndex in target.BlendshapeIndices)
                            {
                                if (target.Renderer != null)
                                {
                                    target.Renderer.SetBlendShapeWeight(blendshapeIndex, 0);
                                }
                            }
                        }
                    }
                }
            }
        }

        private ComputedActuatorTarget[] ComputeTargets(Dictionary<SkinnedMeshRenderer, List<string>> smrToBlendshapeNames, string[] definitionBlendshapes, bool onlyFirstMatch)
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

        private class ComputedActuatorTarget
        {
            public SkinnedMeshRenderer Renderer;
            public int[] BlendshapeIndices;
        }
    }
}
