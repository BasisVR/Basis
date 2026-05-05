using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HVR.Basis.Comms;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Vixxy
{
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy")]
    [AddComponentMenu("HVR.Basis/HVR Vixxy Control")]
    public partial class HVRVixxyControl
    {
        /// The orchestrator defines the context that the subjects of this control will affect (e.g. Recursive Search).
        /// Vixxy is not an avatar-specific component, so it needs that limited context.
        [SerializeField] internal HVRVixxyOrchestrator orchestrator;

        /// An address is not necessary, but if one is provided, then we will be using that provided address.
        /// If not, we will generate one at runtime.
        [SerializeField] internal HVRAddressSelector address;

        public int NumberOfChoices => choices.Length;
        public bool HasThreeOrMoreChoices => NumberOfChoices >= 3;
        public bool IsRegularToggle => !HasThreeOrMoreChoices
                                       && Mathf.Approximately(choices[HVRVixxyPropertyBase.ActiveIndex].value, 1f)
                                       && Mathf.Approximately(choices[HVRVixxyPropertyBase.InactiveIndex].value, 0f);

        [SerializeField] public HVRVixxyChoiceControl[] choices = {
            new() { title = "", icon = null, value = 0f },
            new() { title = "", icon = null, value = 1f }
        };
        [SerializeField] public float defaultValue = 0f;

        [SerializeField] internal HVRVixxyActivation[] activations = Array.Empty<HVRVixxyActivation>();
        [SerializeField] internal HVRVixxySubject[] subjects = Array.Empty<HVRVixxySubject>();

        // The number of seconds it takes to go from 0.0 to 1.0. If there are more than two choices: The number of seconds it takes to go from one state to another.
        // [SerializeField] internal float interpolationDurationSeconds = 0f;
        // [SerializeField] internal AnimationCurve interpolationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [SerializeField] internal bool networked = true;
        [SerializeField] internal HVRVixxyNetworkingType advancedNetworking = HVRVixxyNetworkingType.Automatic;

        /// If true, we only run the logic of this control if it's enabled. By default, this is false, so that users can put a toggle control
        /// directly inside the component hierarchy that is being toggled OFF.
        [SerializeField] internal bool onlyExecuteWhenEnabled = false;
    }

    [Serializable]
    public enum HVRVixxyControlType
    {
        Menu,
        Variable,
    }

    [Serializable]
    public enum HVRVixxyControlPresentation
    {
        Default,
        Slider,
    }

    [Serializable]
    public enum HVRVixxyTitleSelection
    {
        UseObjectName,
        UseCustomTitle,
        UseCustomTitleAndChoices,
        UseChoicesOnly,
    }

    [Serializable]
    public class HVRVixxyActivation
    {
        public Component component; // To toggle a GameObject, provide the Transform instead. It makes things easier as GameObject is not a component.
        public ActivationThreshold threshold;
        public bool[] choices;

        [NonSerialized] internal bool IsApplicable;
        [NonSerialized] internal HVRVixxyActivationBakeResult BakeResult = HVRVixxyActivationBakeResult.WasNotEvaluated;
    }

    [Serializable]
    public enum ActivationThreshold
    {
        /// When there's a transition, it is ON during that transition.<br/>
        /// In technical terms, it is considered to be ON when the absolute difference to the target is strictly smaller than 1.
        /// This is the best choice for stuff like material dissolves, where the object appears before it is even complete, and therefore the default.
        Blended,
        /// Is considered to be ON when the current value is equal to the target value.
        Strict,
    }

    [Serializable]
    public class HVRVixxySubject
    {
        public HVRVixxySelection selection;

        // TODO: It may be relevant to create a MonoBehaviour that represents groups of objects that can be referenced multiple times throughout.
        public GameObject[] targets;
        public GameObject[] childrenOf;
        public GameObject[] exceptions;

        // Note: The list of properties may sometimes contain properties that are not shown in the UI,
        // because the first target does not contain the component type referenced by that property.
        //
        // In that case, when the Processor runs, these properties are NOT applied, even if the actual
        // objects being changed do contain the component type.
        // We don't want to apply "ghost" properties that are not visible to the user in the UI.
        //
        // In the case of Vixxy (and not Vixen), we should just prune these properties at runtime.
        [SerializeReference] public List<HVRVixxyPropertyBase> properties;

        // Runtime only
        [NonSerialized] internal List<GameObject> BakedObjects;
        [NonSerialized] internal bool IsApplicable;
        [NonSerialized] internal HVRVixxySubjectsBakeResult BakeResult = HVRVixxySubjectsBakeResult.WasNotEvaluated;
    }

    [Serializable]
    public enum HVRVixxySelection
    {
        Normal,
        RecursiveSearch,
        Everything
    }

    [Serializable]
    public enum HVRVixxyRememberScope
    {
        /// When the avatar loads, the value is always the default.
        DoNotRemember,
        /// We remember the value for this address, only in this specific avatar. The name of the avatar is used to determine if it's the same avatar.
        RememberInThisAvatar,
        /// We remember the value for this address, only across controls which share the same rememberTag value.
        RememberInThisTag,
        /// We remember the value for this address across all avatars.
        RememberAcrossAvatars
    }

    [Serializable]
    public enum HVRVixxyNetworkingType
    {
        Automatic,
        UpdatedExtremelyFrequently
    }

    [Serializable]
    public class HVRVixxyProperty<T> : HVRVixxyPropertyBase
    {
        public T[] choices = new T[2];

        public override bool ValidateBasedOnNumberOfChoices(int actualNumberOfChoices) => choices.Length >= actualNumberOfChoices;

        public override void PruneArrays(int actualNumberOfChoices)
        {
            var newChoices = new T[actualNumberOfChoices];
            for (var i = 0; i < actualNumberOfChoices; i++)
            {
                if (i < choices.Length)
                {
                    newChoices[i] = choices[i];
                }
                else
                {
                    var k = choices.Length - 1;
                    if (k >= 0)
                    {
                        newChoices[i] = choices[k];
                    }
                }
            }

            choices = newChoices;
        }

        public override void RemoveChoiceAtIndex(int choiceIndex)
        {
            choices = choices.Where((_, i) => i != choiceIndex).ToArray();
        }

        public override List<Object> ListAssets()
        {
            if (typeof(Object).IsAssignableFrom(typeof(T)) && typeof(T) != typeof(GameObject) && typeof(T) != typeof(Component))
            {
                return choices
                    .Select(choice => choice as Object)
                    .Where(choice => choice != null)
                    .Distinct()
                    .ToList();
            }
            return new List<Object>();
        }

        internal T ApplyThresholdFunction(float active01, int inactiveIndex, int activeIndex, float threshold)
        {
            // This checks if it's above the threshold, OR if it's equal to 1.
            // We specifically don't do "above or equal to" because we don't want 0 to be above the threshold.
            var isActive = active01 > threshold || Mathf.Approximately(active01, 1f);
            return isActive ? choices[activeIndex] : choices[inactiveIndex];
        }
    }

    [Serializable]
    public class HVRVixxyPropertyBase : IHVRVixxyProperty
    {
        [NonSerialized] public const int InactiveIndex = 0;
        [NonSerialized] public const int ActiveIndex = 1;

        public string fullClassName;
        public HVRVixxyPropertyVariant variant;
        public string propertyName;

        // Runtime only
        [NonSerialized] internal bool IsApplicable;
        [NonSerialized] internal HVRVixxyPropertyBakeResult BakeResult = HVRVixxyPropertyBakeResult.WasNotEvaluated;
        [NonSerialized] internal Type FoundType;
        [NonSerialized] internal List<Component> FoundComponents;
        [NonSerialized] internal HVRKindMarker KindMarker;
        [NonSerialized] internal int ShaderMaterialProperty; // only relevant if HVRKindMarker is AffectsMaterialPropertyBlock
        [NonSerialized] internal FieldInfo FieldIfMarkedAsFieldAccess; // null if HVRKindMarker is not FieldAccess
        [NonSerialized] internal PropertyInfo TPropertyIfMarkedAsTPropertyAccess; // null if HVRKindMarker is not PropertyAccess
        [NonSerialized] internal Dictionary<SkinnedMeshRenderer, int> SmrToBlendshapeIndex; // null if HVRKindMarker is not BlendShape

        public virtual bool ValidateBasedOnNumberOfChoices(int actualNumberOfChoices) => true;
        public virtual void PruneArrays(int actualNumberOfChoices) {}
        public virtual void RemoveChoiceAtIndex(int choiceIndex) {}
        public virtual object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue) { throw new NotImplementedException(); }
        public virtual void ApplyMaterialProperty(MaterialPropertyBlock materialPropertyBlock, object resolvedValue)
        {
            switch (resolvedValue)
            {
                case float lerpFloatValue: materialPropertyBlock.SetFloat(ShaderMaterialProperty, lerpFloatValue); break;
                case Color lerpColorValue: materialPropertyBlock.SetColor(ShaderMaterialProperty, lerpColorValue); break;
                case Vector4 lerpVector4Value: materialPropertyBlock.SetVector(ShaderMaterialProperty, lerpVector4Value); break;
                case Vector3 lerpVector3Value: materialPropertyBlock.SetVector(ShaderMaterialProperty, lerpVector3Value); break;

                // Unused, but we define them anyway:
                case Vector2 lerpVector2Value: materialPropertyBlock.SetVector(ShaderMaterialProperty, lerpVector2Value); break;
                case int lerpIntValue: materialPropertyBlock.SetInt(ShaderMaterialProperty, lerpIntValue); break;
                case bool lerpBoolValue: materialPropertyBlock.SetFloat(ShaderMaterialProperty, lerpBoolValue ? 1f : 0f); break;
                case Texture textureValue: materialPropertyBlock.SetTexture(ShaderMaterialProperty, textureValue); break;
                case float[] lerpFloatArrayValue: materialPropertyBlock.SetFloatArray(ShaderMaterialProperty, lerpFloatArrayValue); break;
            }
        }
        public virtual List<Object> ListAssets() { return new List<Object>(); }
    }

    [Serializable]
    public enum HVRVixxyPropertyVariant
    {
        Standard,
        MaterialProperty,
        BlendShape
    }

    interface IHVRVixxyProperty
    {
    }

    [Serializable]
    public class HVRVixxyPropertyFloat : HVRVixxyProperty<float>
    {
        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return Mathf.Lerp(choices[inactiveIndex], choices[activeIndex], active01);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyInt : HVRVixxyProperty<int>
    {
        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return Mathf.RoundToInt(Mathf.Lerp(choices[inactiveIndex], choices[activeIndex], active01));
        }
    }

    [Serializable]
    public class HVRVixxyPropertyVector4 : HVRVixxyProperty<Vector4>
    {
        public HVRVixxyPropertyVector4Interpolation interpolation;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            switch (interpolation)
            {
                case HVRVixxyPropertyVector4Interpolation.Regular:
                {
                    return Vector4.Lerp(choices[inactiveIndex], choices[activeIndex], active01);
                }
                case HVRVixxyPropertyVector4Interpolation.VectorXYZSpherical:
                {
                    var vector3 = Vector3.Slerp(choices[inactiveIndex], choices[activeIndex], active01);
                    var w = Mathf.Lerp(choices[inactiveIndex].w, choices[activeIndex].w, active01);
                    return new Vector4(vector3.x, vector3.y, vector3.z, w);
                }
                default: throw new ArgumentOutOfRangeException();
            }
        }
    }

    [Serializable]
    public class HVRVixxyPropertyVector3 : HVRVixxyProperty<Vector3>
    {
        public HVRVixxyPropertyVector3Interpolation interpolation;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return interpolation switch
            {
                HVRVixxyPropertyVector3Interpolation.Regular => Vector3.Lerp(choices[inactiveIndex], choices[activeIndex], active01),
                HVRVixxyPropertyVector3Interpolation.Spherical => Vector3.Slerp(choices[inactiveIndex], choices[activeIndex], active01),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }

    [Serializable]
    public class HVRVixxyPropertyMaterial : HVRVixxyProperty<Material>
    {
        public float threshold;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return ApplyThresholdFunction(active01, inactiveIndex, activeIndex, threshold);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyMesh : HVRVixxyProperty<Mesh>
    {
        public float threshold;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return ApplyThresholdFunction(active01, inactiveIndex, activeIndex, threshold);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyTexture : HVRVixxyProperty<Texture>
    {
        public float threshold;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return ApplyThresholdFunction(active01, inactiveIndex, activeIndex, threshold);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyColor : HVRVixxyProperty<Color>
    {
        public HVRVixxyPropertyColorInterpolation interpolation;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return interpolation == HVRVixxyPropertyColorInterpolation.Oklab
                ? HVR_VixxyUtil.OklabLerp(choices[inactiveIndex], choices[activeIndex], active01)
                : Color.Lerp(choices[inactiveIndex], choices[activeIndex], active01);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyColor32 : HVRVixxyProperty<Color32>
    {
        public HVRVixxyPropertyColor32Interpolation interpolation;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            var fromHDR = choices[inactiveIndex];
            var toHDR = choices[activeIndex];

            if (interpolation == HVRVixxyPropertyColor32Interpolation.OklabWhenPossible)
            {
                if (HVR_VixxyUtil.IsBelowHDR(fromHDR) && HVR_VixxyUtil.IsBelowHDR(toHDR))
                {
                    return HVR_VixxyUtil.OklabLerp(fromHDR, toHDR, active01);
                }
                else
                {
                    // Interpolation using OkLab may not be possible with HDR
                    // https://blog.selfshadow.com/publications/s2025-shading-course/pdi/s2025_pbs_pdi_slides.pdf
                    // page 141
                    //
                    // (Basis Discord) https://discord.com/channels/1239242259392757822/1288184592930570293/1498540178157867018
                }
            }

            return Color32.Lerp(fromHDR, toHDR, active01);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyBool : HVRVixxyProperty<bool>
    {
        public float threshold;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return ApplyThresholdFunction(active01, inactiveIndex, activeIndex, threshold);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyQuaternion : HVRVixxyProperty<Quaternion>
    {
        public HVRVixxyPropertyQuaternionInterpolation interpolation;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            return Quaternion.Slerp(choices[inactiveIndex], choices[activeIndex], active01);
        }
    }

    [Serializable]
    public class HVRVixxyPropertyString : HVRVixxyProperty<string>
    {
        public float threshold;

        public override object CalculateLerpValue(float active01, int inactiveIndex, int activeIndex, float absoluteValue)
        {
            var result = ApplyThresholdFunction(active01, inactiveIndex, activeIndex, threshold);

            return result != null ? string.Format(CultureInfo.InvariantCulture, result, absoluteValue, absoluteValue * 100f, active01) : null;
        }
    }

    [Serializable]
    public enum HVRVixxyPropertyColorInterpolation
    {
        Oklab,
        Unity
    }

    [Serializable]
    public enum HVRVixxyPropertyColor32Interpolation
    {
        /// Use Oklab when the two colors are both below HDR.
        OklabWhenPossible,
        Unity
    }

    [Serializable]
    public enum HVRVixxyPropertyQuaternionInterpolation
    {
        Spherical,
    }

    [Serializable]
    public enum HVRVixxyPropertyVector3Interpolation
    {
        Regular,
        Spherical,
    }

    [Serializable]
    public enum HVRVixxyPropertyVector4Interpolation
    {
        Regular,
        /// Slerp for XYZ, Lerp for W
        VectorXYZSpherical,
    }

    [Serializable]
    public class HVRVixxyChoiceControl
    {
        public string title;
        public Texture2D icon;
        public float value;
    }
}
