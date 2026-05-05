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

        private float _previousValue;
        internal float _value;

        [NonSerialized] internal string Address;
        [NonSerialized] internal int AddressId;
        [NonSerialized] internal bool HasMoreThanTwoChoices;
        [NonSerialized] internal int ActualNumberOfChoices;
        [NonSerialized] internal bool IsInitialized;
        [NonSerialized] internal bool WasAvatarReadyApplied;
        [NonSerialized] internal bool IsWearer;
        [NonSerialized] internal bool AlsoExecutesWhenDisabled;

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

            FigureOutActualNumberOfChoices(out var hasMoreThanTwoChoices, out var actualNumberOfChoices);
            HasMoreThanTwoChoices = hasMoreThanTwoChoices;
            ActualNumberOfChoices = actualNumberOfChoices;

            InterpolateFromChoiceApplies = false;
            InterpolateFromChoice = 0;
            InterpolateFromChoiceAmount01 = 0f;

            AlsoExecutesWhenDisabled = !onlyExecuteWhenEnabled;

            Networked = networked;
            NetworkingType = networked ? advancedNetworking : HVRVixxyNetworkingType.Automatic;

            // UGC Rule: Sanitize arrays.
            activations ??= Array.Empty<HVRVixxyActivation>();
            subjects ??= Array.Empty<HVRVixxySubject>();

            BakeControlSubjectsAndActivationsForRuntime();

            if (_avatarNullable != null)
            {
                ApplyAvatarReady(isWearer);
            }

            if (Networked)
            {
                orchestrator.RequireNetworked(AddressId, NetworkingType, defaultValue, Min(), Max());
            }

            _variableStore = _avatarComms != null ? _avatarComms.VariableStore : AcquisitionService.SceneInstance.VariableStore;

            IsInitialized = true;
            if (isActiveAndEnabled || AlsoExecutesWhenDisabled)
            {
                _variableStore.SubmitOrDefineDefaultValue(AddressId, defaultValue);

                _registeredActuator = orchestrator.RegisterActuator(AddressId, this, OnImplicitAddressUpdated);
                _value = defaultValue;
                BasisDebug.Log($"Initialized {GetType().Name} {Address}, value is set to {_value}");
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
                    .SelectMany(property => property.ListAssets())
                )
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
            BakeControlSubjectsAndActivationsForRuntime();
        }

        private void BakeControlSubjectsAndActivationsForRuntime()
        {
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

                foreach (var property in subject.properties)
                {
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
                var component = bakedObject.GetComponent(foundType);
                if (component == null && useSkinnedMeshRendererLeniency)
                {
                    bakedObject.GetComponent(skinnedMeshRendererLeniencyOtherType);
                }
                if (component != null) // This is *NOT* UGC Rule. Some of the targets just may not have that component, especially the non-first objects, and recursive searches.
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

            _previousValue = float.MinValue + 1.23456789f;
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
            // FIXME: This is a bypass so that we don't update an address that hasn't changed.
            // Ideally, the orchestrator should instead provide us a guarantee of calling us only when the value changes.
            if (Mathf.Approximately(value, _previousValue)) return;
            _previousValue = value;

            // FIXME: Storing that value is probably not a good idea to do at this specific stage of the processing.
            //           For comparison, we can't do this for aggregators (which can have multiple input values), it's not their responsibility.
            _value = value;

            orchestrator.PassAddressUpdated(AddressId);
        }

        public void Actuate()
        {
            if (!HasMoreThanTwoChoices)
            {
                // FIXME: We really need to figure out how actuators sample values from their dependents.
                var linear01 = Mathf.InverseLerp(choices[HVRVixxyPropertyBase.InactiveIndex].value, choices[HVRVixxyPropertyBase.ActiveIndex].value, _value);
                var active01 = linear01;
                ActuateActivations(active01);
                ActuateSubjects(active01, HVRVixxyPropertyBase.InactiveIndex, HVRVixxyPropertyBase.ActiveIndex);
            }
            else
            {
                int storedIntValue = Mathf.RoundToInt(_value);
                int outValue;
                if (storedIntValue < 0) outValue = 0;
                else if (storedIntValue >= ActualNumberOfChoices) outValue = ActualNumberOfChoices - 1;
                else outValue = storedIntValue;

                if (!InterpolateFromChoiceApplies)
                {
                    SetActivation(outValue);
                    SetSubjects(outValue);
                }
                else
                {
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

                    var lerpValue = property.CalculateLerpValue(active01, inactiveIndex, activeIndex, _value);
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

                    object lerpValue = property switch
                    {
                        HVRVixxyProperty<float> valueFloat => valueFloat.choices[choice],
                        HVRVixxyProperty<Color> valueColor => valueColor.choices[choice],
                        HVRVixxyProperty<Vector4> valueVector4 => valueVector4.choices[choice],
                        HVRVixxyProperty<Vector3> valueVector3 => valueVector3.choices[choice],
                        _ => null
                    };
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
                            var materialPropertyBlock = orchestrator.GetMaterialPropertyBlockForBakedObject(component.gameObject);
                            property.ApplyMaterialProperty(materialPropertyBlock, resolvedValue);
                            orchestrator.StagePropertyBlock(component.gameObject);
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

        private static FieldInfo GetFieldInfoOrNull(Type foundType, string propertyName)
        {
            var fields = foundType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var fieldInfo in fields)
            {
                if (fieldInfo.Name == propertyName)
                {
                    return fieldInfo;
                }
            }

            return null;
        }

        private static PropertyInfo GetPropertyInfoOrNull(Type foundType, string propertyName)
        {
            var typeProperties = foundType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var propertyInfo in typeProperties)
            {
                if (propertyInfo.Name == propertyName)
                {
                    return propertyInfo;
                }
            }

            return null;
        }

        public float Min() => choices.Select(control => control.value).Min();
        public float Max() => choices.Select(control => control.value).Max();
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
