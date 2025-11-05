using UnityEngine;
using System.Collections;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;

namespace Basis.Scripts.BasisSdk.Interactions
{

public class BasisAutoReturn : BasisInteractableObject
{

    [Tooltip("Target world position to move to.")]
    public bool enable = true;
    Vector3 _positionAtStart;
    Quaternion _rotationAtStart;
    Vector3 _scaleAtStart;

    [Tooltip("Delay in seconds before moving.")]
    public float delay = 3f;

    [Tooltip("If > 0, the object will interpolate to the target over this duration; if 0, it will jump instantly.")]
    public float duration = 0f;

    [Tooltip("Easing preset to apply to the interpolation.")]
    public EasingType easing = EasingType.Linear;

    [Tooltip("Use a custom AnimationCurve instead of the preset easing.")]
    public bool useCustomCurve = false;

    [Tooltip("Custom easing curve evaluated over 0..1 (time).")]
    public AnimationCurve customCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

    public enum EasingType
    {
        Linear,
        SmoothStep,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutQuad,
        EaseInCubic,
        EaseOutCubic,
        EaseInOutCubic
    }

    public float InteractRange = 1f;

    void Start()
    {
        _positionAtStart = transform.position;
        _rotationAtStart = transform.rotation;
        _scaleAtStart = transform.localScale;
        StartCoroutine(MoveAfterDelayCoroutine());
    }

    /// //////////////////////////////////////////////////////////////////////////
    public override bool CanHover(BasisInput input)
    {
        return InteractableEnabled &&
               Inputs.IsInputAdded(input) &&
               input.TryGetRole(out BasisBoneTrackedRole role) &&
               Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
               found.GetState() == BasisInteractInputState.Ignored &&
               IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    public override bool IsHoveredBy(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
    }

    public override bool CanInteract(BasisInput input)
    {
        return InteractableEnabled &&
               Inputs.IsInputAdded(input) &&
               input.TryGetRole(out BasisBoneTrackedRole role) &&
               Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
               found.GetState() == BasisInteractInputState.Hovering &&
               IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    public override bool IsInteractingWith(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
    }

    public override void InputUpdate()
    {
        if(enable){
            StopCoroutine(MoveAfterDelayCoroutine());
        }
    }

    /// //////////////////////////////////////////////////////////////////////////


    IEnumerator MoveAfterDelayCoroutine()
        {
            yield return new WaitForSeconds(delay);

            if (duration <= 0f)
            {
                transform.localPosition = _positionAtStart;
                transform.localRotation = _rotationAtStart;
                transform.localScale = _scaleAtStart;
                yield break;
            }

            float elapsed = 0f;
            Vector3 startPos = transform.localPosition;
            Vector3 scalePos = transform.localScale;
            Quaternion rotPos = transform.localRotation;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // Apply easing (or custom curve)
                float easedT = useCustomCurve ? customCurve.Evaluate(t) : ApplyEasing(t, easing);

                Vector3 pos = Vector3.Lerp(startPos, _positionAtStart, easedT);
                transform.localPosition = pos;

                Vector3 scale = Vector3.Lerp(scalePos, _scaleAtStart, easedT);
                transform.localScale = scale;

                Quaternion rot = Quaternion.Lerp(rotPos, _rotationAtStart, easedT);
                transform.localRotation = rot;

                yield return null;
            }

            // Ensure final position exactly
           transform.localPosition = _positionAtStart;

        }


        static float ApplyEasing(float t, EasingType e)
        {
            switch (e)
            {
                case EasingType.SmoothStep:
                    return Mathf.SmoothStep(0f, 1f, t);
                case EasingType.EaseInQuad:
                    return t * t;
                case EasingType.EaseOutQuad:
                    return t * (2f - t);
                case EasingType.EaseInOutQuad:
                    return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
                case EasingType.EaseInCubic:
                    return t * t * t;
                case EasingType.EaseOutCubic:
                    return 1f - Mathf.Pow(1f - t, 3f);
                case EasingType.EaseInOutCubic:
                    return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
                case EasingType.Linear:
                default:
                    return t;
            }
        }
    }
}
