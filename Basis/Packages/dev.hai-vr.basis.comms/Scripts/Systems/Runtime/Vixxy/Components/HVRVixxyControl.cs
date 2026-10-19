using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HVR.Basis.Comms;
using HVR.Basis.Comms.HVRUtility;
#if HVR_VIXXY_IS_IN_BASIS
using Basis.Scripts.BasisSdk;
#endif
using UnityEngine;
using Object = UnityEngine.Object;

// UGC Rule: GameObjects and Components referenced by this class should be treated defensively as being UGC at runtime:<br/>
// - There may be null values in the arrays, as they may be unreliable user input or removed as part of a build process (e.g. EditorOnly),<br/>
// - Non-null values in the array may reference objects that will be destroyed later, so treat objects and components as potentially destroyable,<br/>
// - Similarly to animation, it is fine for the user to define rules that cannot apply (i.e. setting a material property on a type that isn't a Renderer,
//   referencing a field on a type that cannot exist, etc.); do not treat those as errors,<br/>
// - Do not treat anything else defensively than the above points, which are expectations of this specific system.
namespace HVR.Vixxy
{
    /// This is a user-accessible toggle system.<br/>
    /// <br/>
    /// This component listens to a value in an address, which toggles and applies user-defined values in the properties of some objects accordingly.<br/>
    /// If there are material properties to be changed, those changes are staged into the HVROrchestrator component, which then applies a material property block
    /// to the renderer once after all changes have been received for that frame.<br/>
    [AutoStaticsCleanup]
    public partial class HVRVixxyControl : MonoBehaviour, IHVRVixxyActuator, IHVRInitializable
    {
        // Licensing notes:
        // Portions of the code below originally comes from portions of a proprietary software that I (Haï~) am the author of,
        // and is notably used in "Vixen" (2023-2024).
        // The code below is released under the same terms as the LICENSE file of dev.hai-vr.basis.comms, which is MIT,
        // including the specific portions of the code that originally came from "Vixen".

        // Runtime only
        private Component _avatarNullable;
        private HVRAvatarComms _avatarComms;
        private HVRVariableStore _variableStore;

        private Transform _context;
        private HVRActuatorRegistrationToken _registeredActuator;

        internal float _previousValue;
        internal float _objectiveValue;
        internal float _actuatedValue;

        [NonSerialized] internal string Address;
        [NonSerialized] internal int AddressId;
        [NonSerialized] internal bool HasMoreThanTwoChoices;
        [NonSerialized] internal int ActualNumberOfChoices;
        [NonSerialized] internal bool IsInitialized;
        [NonSerialized] internal bool WasAvatarReadyApplied;
        [NonSerialized] internal bool IsWearer;
        [NonSerialized] internal bool AlsoExecutesWhenDisabled;
        [NonSerialized] internal List<int> ChoiceIndexOrderedByValue;
        [NonSerialized] internal float MinimumValue;
        [NonSerialized] internal float MaximumValue;
        [NonSerialized] internal List<HVRVixxyFilterBase> Filters;

        [NonSerialized] internal bool Networked;
        [NonSerialized] internal HVRVixxyNetworkingType NetworkingType;

        [SerializeField] internal bool InterpolateFromChoiceApplies; // Only serializable for debug purposes
        [SerializeField] internal int InterpolateFromChoice; // Only serializable for debug purposes
        [SerializeField] internal float InterpolateFromChoiceAmount01; // Only serializable for debug purposes

        public void Awake()
        {
            if (_avatarNullable == null)
            {
                _avatarNullable = HVRCommsUtil.GetAvatar(this);
            }
#if HVR_HAS_HVR_INTEGRATION
            if (_avatarNullable != null)
            {
                (_avatarNullable as HVR.UGC.HVRUGCAvatar).OnFullyInitialized += HVRAvatarComms.FlagHVRFullyInitialized;
            }
#endif
#if HVR_VIXXY_IS_IN_BASIS
            // null avatar can happen in testing scenes, where the control is outside an avatar.
            // We don't want to treat this as being an error.
            _avatarNullable = GetComponentInParent<BasisAvatar>(true);

            if (_avatarNullable == null)
            {
                // If there is NO avatar, then we run the Avatar Ready callback ourselves, because this means we're in a test scene.
                OnHVRAvatarReady(false);
            }
#endif
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
#if HVR_VIXXY_IS_IN_BASIS
            // We need to do this here too, because if the VixxyControl is by default OFF, we need this to initalize ourselves. (WasAvatarReadyApplied is separate from IsInitialized)
            _avatarNullable = HVRCommsUtil.GetAvatar(this);
            _avatarComms = HVRCommsUtil.GetComms(this);
#endif

            orchestrator = VixxySetup.EnsureInitialized(this, isWearer);
            _context = orchestrator.Context();

            Address = CalculateAddress();
            AddressId = HVRAddress.AddressToId(Address);

            // Bake the choices.
            FigureOutActualNumberOfChoices(out var hasMoreThanTwoChoices, out var actualNumberOfChoices);
            HasMoreThanTwoChoices = hasMoreThanTwoChoices;
            ActualNumberOfChoices = actualNumberOfChoices;
            MinimumValue = Min();
            MaximumValue = Max();

            InterpolateFromChoiceApplies = false;
            InterpolateFromChoice = 0;
            InterpolateFromChoiceAmount01 = 0f;

            AlsoExecutesWhenDisabled = !onlyExecuteWhenEnabled;

            Networked = networked && !HVRAddress.IsSystemAddressName(Address) && !orchestrator.IsMeasurementAddress(AddressId);
            NetworkingType = Networked ? advancedNetworking : HVRVixxyNetworkingType.Automatic;

            // Bake the subjects
            // UGC Rule: Sanitize arrays.
            activations ??= Array.Empty<HVRVixxyActivation>();
            subjects ??= Array.Empty<HVRVixxySubject>();
            filters ??= new List<HVRVixxyFilterBase>();
            HVR_VixxyUtil.SanitizeFieldOfTypeSerializeReference(filters);
            if (transition == HVRVixxyTransitionMode.None)
            {
                Filters = new List<HVRVixxyFilterBase>();
            }
            else if (transition == HVRVixxyTransitionMode.Advanced)
            {
                Filters = filters;
            }
            else // Simplified
            {
                var newFilters = new List<HVRVixxyFilterBase>();
                if (transitionDuration > 0f)
                {
                    newFilters.Add(new HVRMoveTowardsVixxyFilter
                    {
                        secondsPerUnit = (MaximumValue - MinimumValue) * transitionDuration
                    });
                    newFilters.Add(new HVRCurveVixxyFilter
                    {
                        curve = AnimationCurve.EaseInOut(MinimumValue, MinimumValue, MaximumValue, MaximumValue)
                    });
                }
                Filters = newFilters;
            }
            BakeControlSubjectsAndActivationsForRuntime();

            if (_avatarNullable != null)
            {
                ApplyAvatarReady(isWearer);
            }

            if (Networked)
            {
                orchestrator.RequireNetworked(AddressId, NetworkingType, defaultValue, MinimumValue, MaximumValue);
            }

            _variableStore = _avatarComms != null ? _avatarComms.VariableStore : AcquisitionService.SceneInstance.VariableStore;

            IsInitialized = true;
            if (isActiveAndEnabled || AlsoExecutesWhenDisabled)
            {
                _variableStore.SubmitOrDefineDefaultValue(AddressId, defaultValue);

                _registeredActuator = orchestrator.RegisterActuator(AddressId, this, OnImplicitAddressUpdated);
                _objectiveValue = defaultValue;
                if (Filters.Count == 0)
                {
                    _actuatedValue = defaultValue;
                }
                else
                {
                    PrimeFilters();
                }
            }
        }

        public string CalculateAddress()
        {
            return address.TryResolvePath(out var resolvedPath) ? resolvedPath : GenerateAddressFromPath();
        }

        public List<Object> ListAssets()
        {
            var results = choices
                .Select(control => control.icon as Object)
                .Concat(subjects
                    .SelectMany(subject => subject.properties)
                    // Unusual, but happens if a property was created in a newer version, and then downgraded. Since we're in edit mode, sanitization is not yet in effect.
                    .Where(property => property != null)
                    .SelectMany(property => property.ListAssets()))
                .Where(that => that != null)
                .Distinct()
                .ToList();

            return results;
        }

        private void FigureOutActualNumberOfChoices(out bool hasMoreThanTwoChoices, out int actualNumberOfChoices)
        {
            hasMoreThanTwoChoices = NumberOfChoices > 2;
            actualNumberOfChoices = hasMoreThanTwoChoices ? NumberOfChoices : 2;
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            orchestrator.SignalHVRReadyBothAvatarAndNetwork(isWearer);
        }

        private void OnDestroy()
        {
            if (!IsInitialized) return;

            if (_registeredActuator != null && AlsoExecutesWhenDisabled)
            {
                orchestrator.UnregisterActuator(_registeredActuator);
            }
        }

        private void ApplyAvatarReady(bool isWearer)
        {
            WasAvatarReadyApplied = true;
            IsWearer = isWearer;
        }

        private string GenerateAddressFromPath()
        {
            var componentIndex = Array.IndexOf(transform.GetComponents<HVRVixxyControl>(), this);
            var path = HVR_VixxyUtil.ResolveRelativePath(orchestrator.Context().transform, transform);
            var newAddress = $"{path}@{HVR_VixxyUtil.SimpleSha1(path)}+{componentIndex}";
            return newAddress;
        }

        /// Called by the Editor when a serialized property changes due to live edits. This is not to be invoked by anything else.
        internal void DebugOnly_ReBakeControl()
        {
            var calculateAddress = CalculateAddress();
            var addressChanged = Address != calculateAddress;
            if (addressChanged)
            {
                Address = calculateAddress;
                AddressId = HVRAddress.AddressToId(Address);

                if (IsInitialized && _registeredActuator != null)
                {
                    orchestrator.UnregisterActuator(_registeredActuator);
                    _registeredActuator = orchestrator.RegisterActuator(AddressId, this, OnImplicitAddressUpdated);
                }
            }

            BakeControlSubjectsAndActivationsForRuntime();
        }

        private void BakeControlSubjectsAndActivationsForRuntime()
        {
            {
                var choiceList = choices.ToList();
                ChoiceIndexOrderedByValue = choiceList
                    .OrderBy(choice => choice.value)
                    .Select(choice => choiceList.IndexOf(choice))
                    .ToList();
            }

            // In this phase, we do all the checks, so that when actuation is requested (this might be as expensive
            // as running every frame), we don't need to do type checks or other work.
            // This means that we need to catch all invalid cases.
            foreach (var activation in activations)
            {
                activation.choices ??= new bool[ActualNumberOfChoices];
                if (activation.choices.Length != ActualNumberOfChoices)
                {
                    Array.Resize(ref activation.choices, ActualNumberOfChoices);
                }
                if (activation.component == null)
                {
                    activation.IsApplicable = false;
                    activation.BakeResult = HVRVixxyActivationBakeResult.ComponentOrGameObjectIsMissing;
                    continue;
                }
                if (!HVR_VixxyPermitted.IsPermitted(activation.component.GetType().FullName))
                {
                    activation.IsApplicable = false;
                    activation.BakeResult = HVRVixxyActivationBakeResult.TypeIsNotPermitted;
                    continue;
                }
                if (activation.component.GetType() == typeof(Transform) && _avatarNullable != null && _avatarNullable.gameObject == activation.component.gameObject)
                {
                    activation.IsApplicable = false;
                    activation.BakeResult = HVRVixxyActivationBakeResult.CannotToggleRootGameObject;
                    continue;
                }

                activation.IsApplicable = true;
                activation.BakeResult = HVRVixxyActivationBakeResult.Success;
            }

            foreach (var subject in subjects)
            {
                // UGC Rule: Sanitize input arrays.
                subject.targets ??= Array.Empty<GameObject>();
                subject.childrenOf ??= Array.Empty<GameObject>();
                subject.exceptions ??= Array.Empty<GameObject>();
                subject.properties ??= new List<HVRVixxyPropertyBase>();
                HVR_VixxyUtil.SanitizeFieldOfTypeSerializeReference(subject.properties);

                BakeSubjectAffectedObjects(subject, _context);

                if (subject.BakedObjects.Count == 0)
                {
                    subject.IsApplicable = false;
                    subject.BakeResult = HVRVixxySubjectsBakeResult.NoBakedObjects;

                    foreach (var property in subject.properties)
                    {
                        property.IsApplicable = false;
                        property.BakeResult = HVRVixxyPropertyBakeResult.SubjectHasNoBakedObjects;
                    }

                    continue;
                }

                var isAnyPropertyApplicable = false;
                var isAnyPropertyDependentOnMaterialPropertyBlock = false;

                for (var index = 0; index < subject.properties.Count; index++)
                {
                    var property = subject.properties[index];
                    if (property is HVRVixxyPropertyColor32 legacyColor32) // Fix an issue where HDR colors were incorrectly specified as Color32
                    {
                        HVRLogging.Debug($"In HVRVixxyControl {name}, converting property Color32 to ColorHDR");
                        subject.properties[index] = new HVRVixxyPropertyColorHDR
                        {
                            choices = legacyColor32.choices.Select(color32 => (Color)color32).ToArray(),
                            interpolation = legacyColor32.interpolation,
                        };
                    }

                    var bakeResult = BakeProperty(property, subject);
                    var isApplicable = bakeResult == HVRVixxyPropertyBakeResult.Success;
                    property.IsApplicable = isApplicable;
                    property.BakeResult = bakeResult;

                    isAnyPropertyApplicable |= isApplicable;
                    if (isApplicable && property.KindMarker == HVRKindMarker.AffectsMaterialPropertyBlock)
                    {
                        isAnyPropertyDependentOnMaterialPropertyBlock = true;
                    }
                }

                if (isAnyPropertyDependentOnMaterialPropertyBlock)
                {
                    foreach (var bakedObject in subject.BakedObjects)
                    {
                        orchestrator.RequireMaterialPropertyBlock(bakedObject);
                    }
                }

                subject.IsApplicable = isAnyPropertyApplicable;
                subject.BakeResult = isAnyPropertyApplicable ? HVRVixxySubjectsBakeResult.Success : HVRVixxySubjectsBakeResult.NoPropertyIsApplicable;
            }
        }

        private static void BakeSubjectAffectedObjects(HVRVixxySubject subject, Transform context)
        {
            var bakedObjects = new List<GameObject>();

            switch (subject.selection)
            {
                case HVRVixxySelection.Normal:
                {
                    bakedObjects.AddRange(subject.targets);
                    break;
                }
                case HVRVixxySelection.RecursiveSearch:
                {
                    // TODO: Prevent out-of-context searches

                    var exceptions = subject.exceptions.Where(o => o != null).ToHashSet();
                    foreach (var childrenRoot in subject.childrenOf)
                    {
                        if (childrenRoot != null) // UGC rule.
                        {
                            var allTransforms = childrenRoot.GetComponentsInChildren<Transform>(true);
                            foreach (var t in allTransforms)
                            {
                                if (!exceptions.Contains(t.gameObject))
                                {
                                    bakedObjects.Add(t.gameObject);
                                }
                            }
                        }
                    }
                    break;
                }
                case HVRVixxySelection.Everything:
                {
                    var exceptions = subject.exceptions.Where(o => o != null).ToHashSet();
                    var allTransforms = context.GetComponentsInChildren<Transform>(true);
                    foreach (var t in allTransforms)
                    {
                        if (!exceptions.Contains(t.gameObject))
                        {
                            bakedObjects.Add(t.gameObject);
                        }
                    }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            HVR_VixxyUtil.RemoveDestroyedFromList(bakedObjects); // Following the UGC rule; see class header.

            subject.BakedObjects = bakedObjects;
        }

        private HVRVixxyPropertyBakeResult BakeProperty(HVRVixxyPropertyBase property, HVRVixxySubject subject)
        {
            if (!HVR_VixxyPermitted.IsTypeOfPropertyValuePermitted(property)) return HVRVixxyPropertyBakeResult.TypeOfPropertyValueIsNotPermitted;
            if (!HVR_VixxyPermitted.IsPermitted(property.fullClassName)) return HVRVixxyPropertyBakeResult.TypeIsNotPermitted;
            if (!HVR_ComponentDictionary.TryGetComponentType(property.fullClassName, out var foundType)) return HVRVixxyPropertyBakeResult.TypeNotFound;

            if (!property.ValidateBasedOnNumberOfChoices(ActualNumberOfChoices))
            {
                return HVRVixxyPropertyBakeResult.InconsistentNumberOfChoices;
            }

            var affectsMaterialPropertyBlock = property.variant == HVRVixxyPropertyVariant.MaterialProperty;
            var affectsBlendShape = property.variant == HVRVixxyPropertyVariant.BlendShape;

            // UGC: Detect misconfiguration oddities
            if (affectsMaterialPropertyBlock && !typeof(Renderer).IsAssignableFrom(foundType))
            {
                return HVRVixxyPropertyBakeResult.MaterialPropertyBlockCanOnlyBeUsedOnRenderers;
            }
            if (affectsBlendShape && foundType != typeof(SkinnedMeshRenderer))
            {
                return HVRVixxyPropertyBakeResult.BlendShapeCanOnlyBeUsedOnSkinnedMeshRenderers;
            }

            var useSkinnedMeshRendererLeniency = affectsMaterialPropertyBlock && (foundType == typeof(SkinnedMeshRenderer) || foundType == typeof(MeshRenderer));
            var skinnedMeshRendererLeniencyOtherType = useSkinnedMeshRendererLeniency
                ? (foundType == typeof(SkinnedMeshRenderer) ? typeof(MeshRenderer) : typeof(SkinnedMeshRenderer))
                : null;

            var foundComponents = new List<Component>();
            foreach (var bakedObject in subject.BakedObjects)
            {
                var foundComponent = bakedObject.TryGetComponent(foundType, out var component);
                if (!foundComponent && useSkinnedMeshRendererLeniency)
                {
                    foundComponent = bakedObject.TryGetComponent(skinnedMeshRendererLeniencyOtherType, out component);
                }
                if (foundComponent) // This is *NOT* UGC Rule. Some of the targets just may not have that component, especially the non-first objects, and recursive searches.
                {
                    foundComponents.Add(component);
                }
            }

            if (foundComponents.Count <= 0) return HVRVixxyPropertyBakeResult.NoObjectsHasThatComponent;

            if (affectsMaterialPropertyBlock)
            {
                property.ShaderMaterialProperty = Shader.PropertyToID(property.propertyName);
                property.KindMarker = HVRKindMarker.AffectsMaterialPropertyBlock;
            }
            else if (affectsBlendShape)
            {
                var nonApplicableComponents = new List<Component>();
                var smrToIndex = new Dictionary<SkinnedMeshRenderer, int>();
                foreach (var component in foundComponents)
                {
                    var smr = (SkinnedMeshRenderer)component;
                    var sharedMesh = smr.sharedMesh;
                    if (sharedMesh != null)
                    {
                        var blendShapeIndex = sharedMesh.GetBlendShapeIndex(property.propertyName);
                        if (blendShapeIndex != -1)
                        {
                            smrToIndex[smr] = blendShapeIndex;
                        }
                        else
                        {
                            nonApplicableComponents.Add(component);
                        }
                    }
                    else
                    {
                        nonApplicableComponents.Add(component);
                    }
                }

                foreach (var nonApplicableComponent in nonApplicableComponents)
                {
                    foundComponents.Remove(nonApplicableComponent);
                }

                if (foundComponents.Count == 0) return HVRVixxyPropertyBakeResult.NoSkinnedMeshRendererHasThisBlendShape;

                property.KindMarker = HVRKindMarker.BlendShape;
                property.SmrToBlendshapeIndex = smrToIndex;
            }
            else
            {
                var fieldInfoNullable = GetFieldInfoOrNull(foundType, property.propertyName);
                if (fieldInfoNullable != null)
                {
                    if (!HVR_VixxyPermitted.AllowArbitraryFieldAccess
                        && !HVR_VixxyPermitted.IsStandardAccessPermitted(property.fullClassName, property.propertyName))
                    {
                        return HVRVixxyPropertyBakeResult.FieldAccessIsNotPermitted;
                    }

                    property.FieldIfMarkedAsFieldAccess = fieldInfoNullable;
                    property.KindMarker = HVRKindMarker.FieldAccess;
                }
                else
                {
                    if (!HVR_VixxyPermitted.AllowArbitraryPropertyAccess
                        && !HVR_VixxyPermitted.IsStandardAccessPermitted(property.fullClassName, property.propertyName))
                    {
                        return HVRVixxyPropertyBakeResult.PropertyAccessIsNotPermitted;
                    }

                    var propertyInfoNullable = GetPropertyInfoOrNull(foundType, property.propertyName);
                    if (propertyInfoNullable == null) return HVRVixxyPropertyBakeResult.NoFieldNorPropertyMatches;

                    if (typeof(Array).IsAssignableFrom(propertyInfoNullable.PropertyType))
                    {
                        // TODO: Add special handling for modifying the slots of arrays?
                    }

                    property.TPropertyIfMarkedAsTPropertyAccess = propertyInfoNullable;
                    property.KindMarker = HVRKindMarker.PropertyAccess;
                }
            }

            property.FoundType = foundType;
            property.FoundComponents = foundComponents;

            return HVRVixxyPropertyBakeResult.Success;
        }

        private void OnEnable()
        {
            // If this was true, we have already enabled this in OnHVRAvatarReady.
            if (AlsoExecutesWhenDisabled) return;

            _previousValue = HVR_VixxyUtil.BogusInitializationNumber;
            if (IsInitialized && _registeredActuator == null)
            {
                _registeredActuator = orchestrator.RegisterActuator(AddressId, this, OnImplicitAddressUpdated);
            }
        }

        private void OnDisable()
        {
            if (AlsoExecutesWhenDisabled) return;

            if (IsInitialized)
            {
                orchestrator.UnregisterActuator(_registeredActuator);
            }
            _registeredActuator = null;
        }

        private void OnImplicitAddressUpdated(float value)
        {
            // It's good to do this, because:
            // - this avoids unnecessary actuations, we don't want to modify objects in the scene when not necessary,
            // - if the packets get quantized within range, it gets clamped to the address' min and max of all controls, so we should also clamp here.
            // However, orchestrator.PassAddressUpdated operates per-address. It might actuate this if another actuator occupies a different range.
            if (value < MinimumValue) value = MinimumValue;
            else if (value > MaximumValue) value = MaximumValue;

            // This function can be called multiple times with the same value (e.g. values submitted by an external program).
            // Only proceed if there's a substantial change.
            if (Mathf.Approximately(value, _previousValue)) return;
            _previousValue = value;
            _objectiveValue = value;
            if (Filters.Count == 0)
            {
                _actuatedValue = _objectiveValue;
            }

            orchestrator.PassAddressUpdated(AddressId);
        }

        public bool HasFilters()
        {
            return Filters.Count > 0;
        }

        private void PrimeFilters()
        {
            var filteredValue = _objectiveValue;
            foreach (var filter in Filters)
            {
                filteredValue = filter.isTimeFilter ? filter.PrimeFilter(_objectiveValue) : filter.Filter(filteredValue);
            }
            _actuatedValue = filteredValue;
        }

        public HVRVixxyActuatorApplyFilterResult ApplyFilters()
        {
            if (Filters.Count == 0) throw new InvalidOperationException("ApplyFilters must never be called when there are no filters, this indicates a programming error.");

            var filterNeedsCheckNextTick = false;

            var filteredValue = _objectiveValue;
            foreach (var filter in Filters)
            {
                if (filter.isTimeFilter)
                {
                    var filtered = filter.TimeFilter(filteredValue, Time.deltaTime);
                    filteredValue = filtered.result;
                    if (filtered.needsCheckNextTick)
                    {
                        filterNeedsCheckNextTick = true;
                    }
                }
                else
                {
                    filteredValue = filter.Filter(filteredValue);
                }
            }

            var actuatorNeedsUpdate = !Mathf.Approximately(_actuatedValue, filteredValue);
            _actuatedValue = filteredValue;

            return new HVRVixxyActuatorApplyFilterResult
            {
                actuatorNeedsUpdate = actuatorNeedsUpdate,
                filterNeedsCheckNextTick = filterNeedsCheckNextTick
            };
        }

        public void Actuate()
        {
            if (!HasMoreThanTwoChoices)
            {
                // FIXME: We really need to figure out how actuators sample values from their dependents.
                var linear01 = Mathf.InverseLerp(choices[HVRVixxyPropertyBase.InactiveIndex].value, choices[HVRVixxyPropertyBase.ActiveIndex].value, _actuatedValue);
                var active01 = linear01;
                ActuateActivations(active01);
                ActuateSubjects(active01, HVRVixxyPropertyBase.InactiveIndex, HVRVixxyPropertyBase.ActiveIndex);
            }
            else
            {
                if (!InterpolateFromChoiceApplies)
                {
                    var lerpFromChoiceIndex = -1;
                    var lerpToChoiceIndex = -1;
                    if (_actuatedValue > choices[ChoiceIndexOrderedByValue[0]].value)
                    {
                        for (var i = 0; i < ChoiceIndexOrderedByValue.Count - 1; i++)
                        {
                            var toChoiceIndex = ChoiceIndexOrderedByValue[i + 1];
                            var toChoice = choices[toChoiceIndex];
                            if (_actuatedValue < toChoice.value)
                            {
                                // We're between two choices
                                lerpFromChoiceIndex = ChoiceIndexOrderedByValue[i];
                                lerpToChoiceIndex = toChoiceIndex;
                                break;
                            }
                        }

                        if (lerpToChoiceIndex == -1)
                        {
                            // We're above the maximum value
                            lerpFromChoiceIndex = ChoiceIndexOrderedByValue[^1];
                            lerpToChoiceIndex = ChoiceIndexOrderedByValue[^1];
                        }
                    }
                    else
                    {
                        // We're below the minimum value
                        lerpFromChoiceIndex = ChoiceIndexOrderedByValue[0];
                        lerpToChoiceIndex = lerpFromChoiceIndex;
                    }

                    if (lerpFromChoiceIndex != lerpToChoiceIndex)
                    {
                        var amount01 = Mathf.InverseLerp(choices[lerpFromChoiceIndex].value, choices[lerpToChoiceIndex].value, _actuatedValue);
                        ActuateActivationsBasedOnChoices(amount01, lerpFromChoiceIndex, lerpToChoiceIndex);
                        ActuateSubjects(amount01, lerpFromChoiceIndex, lerpToChoiceIndex);
                    }
                    else
                    {
                        SetActivation(lerpFromChoiceIndex);
                        SetSubjects(lerpFromChoiceIndex);
                    }
                }
                else
                {
                     int storedIntValue = Mathf.RoundToInt(_actuatedValue);
                     int outValue;
                     if (storedIntValue < 0) outValue = 0;
                     else if (storedIntValue >= ActualNumberOfChoices) outValue = ActualNumberOfChoices - 1;
                     else outValue = storedIntValue;

                    ActuateActivationsBasedOnChoices(InterpolateFromChoiceAmount01, InterpolateFromChoice, outValue);
                    ActuateSubjects(InterpolateFromChoiceAmount01, InterpolateFromChoice, outValue);
                }
            }
        }

        private void ActuateActivations(float active01)
        {
            // TODO: Bake activations in Awake, so that we may remove components that were destroyed without affecting the serialized state of the control.
            foreach (var activation in activations)
            {
                if (!activation.IsApplicable) continue;

                // Defensive check in case of external destruction.
                if (null != activation.component)
                {
                    var target = activation.choices[HVRVixxyPropertyBase.ActiveIndex] ? 1f : 0f;
                    switch (activation.threshold)
                    {
                        case ActivationThreshold.Blended:
                            HVR_VixxyUtil.SetToggleState(activation.component, Mathf.Abs(target - active01) < 1f);
                            break;
                        case ActivationThreshold.Strict:
                            HVR_VixxyUtil.SetToggleState(activation.component, Mathf.Approximately(target, active01));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        private void ActuateActivationsBasedOnChoices(float active01, int inactive, int active)
        {
            // TODO: Bake activations in Awake, so that we may remove components that were destroyed without affecting the serialized state of the control.
            foreach (var activation in activations)
            {
                // Defensive check in case of external destruction.
                if (null != activation.component)
                {
                    var inactivity = activation.choices[inactive];
                    var activity = activation.choices[active];
                    if (inactivity == activity)
                    {
                        HVR_VixxyUtil.SetToggleState(activation.component, inactivity);
                    }
                    else
                    {
                        var target = activity ? 1f : 0f;
                        switch (activation.threshold)
                        {
                            case ActivationThreshold.Blended:
                                HVR_VixxyUtil.SetToggleState(activation.component, Mathf.Abs(target - active01) < 1f);
                                break;
                            case ActivationThreshold.Strict:
                                HVR_VixxyUtil.SetToggleState(activation.component, Mathf.Approximately(target, active01));
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                }
            }
        }

        private void SetActivation(int choice)
        {
            // TODO: Bake activations in Awake, so that we may remove components that were destroyed without affecting the serialized state of the control.
            foreach (var activation in activations)
            {
                // Defensive check in case of external destruction.
                if (null != activation.component)
                {
                    HVR_VixxyUtil.SetToggleState(activation.component, activation.choices[choice]);
                }
            }
        }

        private void ActuateSubjects(float active01, int inactiveIndex, int activeIndex)
        {
            foreach (var subject in subjects)
            {
                // TODO: Rather than do that check every time, only keep applicable subjects into an internal field.
                if (!subject.IsApplicable) continue;

                foreach (var property in subject.properties)
                {
                    // TODO: Rather than do that check every time, bake the applicable properties into an internal field.
                    if (!property.IsApplicable) continue;

                    var lerpValue = property.CalculateLerpValue(active01, inactiveIndex, activeIndex, _actuatedValue);
                    Apply(property, lerpValue, out var propertyNeedsCleanup);

                    if (propertyNeedsCleanup)
                    {
                        HVR_VixxyUtil.RemoveDestroyedFromList(property.FoundComponents);
                        if (property.FoundComponents.Count == 0)
                        {
                            property.IsApplicable = false;
                            // TODO: Also invalidate the subject if no property of that subject is applicable
                        }
                    }
                }
            }
        }

        private void SetSubjects(int choice)
        {
            foreach (var subject in subjects)
            {
                // TODO: Rather than do that check every time, only keep applicable subjects into an internal field.
                if (!subject.IsApplicable) continue;

                foreach (var property in subject.properties)
                {
                    // TODO: Rather than do that check every time, bake the applicable properties into an internal field.
                    if (!property.IsApplicable) continue;

                    object value = property.GetValueForChoice(choice);
                    Apply(property, value, out var propertyNeedsCleanup);

                    if (propertyNeedsCleanup)
                    {
                        HVR_VixxyUtil.RemoveDestroyedFromList(property.FoundComponents);
                        if (property.FoundComponents.Count == 0)
                        {
                            property.IsApplicable = false;
                            // TODO: Also invalidate the subject if no property of that subject is applicable
                        }
                    }
                }
            }
        }

        private void Apply(HVRVixxyPropertyBase property, object resolvedValue, out bool propertyNeedsCleanup)
        {
            propertyNeedsCleanup = false;
            foreach (var component in property.FoundComponents)
            {
                // Defensive check in case of external destruction.
                if (null != component)
                {
                    switch (property.KindMarker)
                    {
                        case HVRKindMarker.AffectsMaterialPropertyBlock:
                        {
                            var bakedObject = component.gameObject;
                            var materialPropertyBlock = orchestrator.GetMaterialPropertyBlockForBakedObject(bakedObject);
                            property.ApplyMaterialProperty(materialPropertyBlock, resolvedValue);
                            orchestrator.StagePropertyBlock(bakedObject);
                            break;
                        }
                        case HVRKindMarker.BlendShape:
                        {
                            if (resolvedValue is float lerpFloatValue)
                            {
                                var smr = (SkinnedMeshRenderer)component;
                                var blendShapeIndex = property.SmrToBlendshapeIndex[smr];
                                smr.SetBlendShapeWeight(blendShapeIndex, lerpFloatValue);
                            }
                            break;
                        }
                        case HVRKindMarker.FieldAccess:
                        {
                            var fieldInfo = property.FieldIfMarkedAsFieldAccess;
                            fieldInfo.SetValue(component, resolvedValue);
                            break;
                        }
                        case HVRKindMarker.PropertyAccess:
                        {
                            var propertyInfo = property.TPropertyIfMarkedAsTPropertyAccess;
                            propertyInfo.SetValue(component, resolvedValue);
                            break;
                        }
                        case HVRKindMarker.Undefined:
                        default:
                            throw new ArgumentException("We tried to access an Undefined property, but Undefined properties are not supposed" +
                                                        " to be valid if the property IsApplicable. This may be a programming error, did we" +
                                                        " properly check that the property IsApplicable?");
                    }
                }
                else
                {
                    propertyNeedsCleanup = true;
                }
            }
        }

        /// Asks this component to optimize itself by pruning unused arrays. Calling this will get rid of material references on choices that are
        /// hidden when the number of choices in a control has been reduced.
        public void OptimizePruneArrays()
        {
            FigureOutActualNumberOfChoices(out _, out var actualNumberOfChoices);

            foreach (var activation in activations)
            {
                if (activation.choices.Length != actualNumberOfChoices)
                {
                    Array.Resize(ref activation.choices, actualNumberOfChoices);
                }
            }

            foreach (var subject in subjects)
            {
                foreach (var propertyBase in subject.properties)
                {
                    propertyBase.PruneArrays(actualNumberOfChoices);
                }
            }
        }

        private static readonly Dictionary<(Type, string), FieldInfo> FieldInfoCache = new();
        private static readonly Dictionary<(Type, string), PropertyInfo> PropertyInfoCache = new();

        public static FieldInfo GetFieldInfoOrNull(Type foundType, string propertyName)
        {
            var key = (foundType, propertyName);
            if (FieldInfoCache.TryGetValue(key, out var cached)) return cached;

            FieldInfo result = null;
            var fields = foundType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var fieldInfo in fields)
            {
                if (fieldInfo.Name == propertyName)
                {
                    result = fieldInfo;
                    break;
                }
            }

            FieldInfoCache[key] = result;
            return result;
        }

        public static PropertyInfo GetPropertyInfoOrNull(Type foundType, string propertyName)
        {
            var key = (foundType, propertyName);
            if (PropertyInfoCache.TryGetValue(key, out var cached)) return cached;

            PropertyInfo result = null;
            var typeProperties = foundType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var propertyInfo in typeProperties)
            {
                if (propertyInfo.Name == propertyName)
                {
                    result = propertyInfo;
                    break;
                }
            }

            PropertyInfoCache[key] = result;
            return result;
        }

        public float Min() => IsInitialized ? MinimumValue : choices.Select(control => control.value).Min();
        public float Max() => IsInitialized ? MaximumValue : choices.Select(control => control.value).Max();
    }

    internal enum HVRKindMarker
    {
        Undefined,
        AffectsMaterialPropertyBlock,
        BlendShape,
        FieldAccess,
        PropertyAccess
    }

    /// When baking properties, the user may have misconfigured the property, or the property configuration may not apply to a
    /// given targeted app. This enumeration describes the various errors that can happen, and it also serves as a comment in the
    /// code explaining why the property is not applicable.
    internal enum HVRVixxyPropertyBakeResult
    {
        WasNotEvaluated,
        Success,
        SubjectHasNoBakedObjects,
        TypeOfPropertyValueIsNotPermitted,
        TypeIsNotPermitted,
        TypeNotFound,
        InconsistentNumberOfChoices,
        NoObjectsHasThatComponent,
        MaterialPropertyBlockCanOnlyBeUsedOnRenderers,
        BlendShapeCanOnlyBeUsedOnSkinnedMeshRenderers,
        NoSkinnedMeshRendererHasThisBlendShape,
        NoFieldNorPropertyMatches,
        FieldAccessIsNotPermitted,
        PropertyAccessIsNotPermitted,
    }

    internal enum HVRVixxySubjectsBakeResult
    {
        WasNotEvaluated,
        Success,
        NoBakedObjects,
        NoPropertyIsApplicable
    }

    internal enum HVRVixxyActivationBakeResult
    {
        WasNotEvaluated,
        Success,
        ComponentOrGameObjectIsMissing,
        TypeIsNotPermitted,
        CannotToggleRootGameObject,
    }
}
