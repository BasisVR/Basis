using System;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
public class ExampleButtonInteractable : InteractableObject
{
    // public BasisObjectSyncNetworking syncNetworking;

    // events other scripts can subscribe to
    public Action ButtonDown;
    public Action ButtonUp;

    [Header("Button Settings")]
    public bool isEnabled;
    [Space(10)]
    public string PropertyName = "_Color";
    public Color Color = Color.white;
    public Color HoverColor = Color.white;
    public Color InteractColor = Color.white;
    public Color DisabledColor = Color.white;

    [Header("References")]
    public Collider ColliderRef;
    public MeshRenderer RendererRef;
    public float InteractRange = 1f;

    private BasisInputWrapper _inputSource;
    // Ignore provided list localy, but keep it updated for other scripts 
    private BasisInputWrapper _InputSource {
        get => _inputSource;
        set {
            if (value.Source != null)
            {
                Inputs = new(0);
                Inputs.SetInputByRole(value.Source, value.GetState());
            }
            else if (value.Source == null)
            {
                Inputs = new(0);
            }
            _inputSource = value;
            
        }
    }

    void Start()
    {
        _InputSource = default;
        if (ColliderRef == null)
        {
            TryGetComponent(out ColliderRef);
        }
        if (RendererRef == null)
        {
            TryGetComponent(out RendererRef);
        }

        SetColor(isEnabled ? Color : DisabledColor);
    }

    public override bool CanHover(BasisInput input)
    {
        return _InputSource.GetState() == InteractInputState.NotAdded && IsWithinRange(input.transform.position, InteractRange) && isEnabled;
    }
    public override bool CanInteract(BasisInput input)
    {
        // must be the same input hovering
        if (!_InputSource.IsInput(input)) return false;
        // dont interact again till after interacting stopped
        if (_InputSource.GetState() == InteractInputState.Interacting) return false;

        return IsWithinRange(input.transform.position, InteractRange) && isEnabled;
    }

    public override void OnHoverStart(BasisInput input)
    {
        if (!BasisInputWrapper.TryNewTracking(input, InteractInputState.Hovering, out BasisInputWrapper wrapper))
        {
            BasisDebug.LogWarning($"{nameof(ExampleButtonInteractable)}: Failed to setup input on hover");
            return;
        }
        _InputSource = wrapper;
        SetColor(HoverColor);
        // call base method (invokes event)
        base.OnHoverStart(input); 
    }

    public override void OnHoverEnd(BasisInput input, bool willInteract)
    {
        if (_InputSource.IsInput(input))
        {
            // leaving hover and wont interact this frame, 
            if (!willInteract)
            {
                bool added = BasisInputWrapper.TryNewTracking(null, InteractInputState.NotAdded, out BasisInputWrapper wrapper);
                // setting to null should not add the tracker
                Debug.Assert(!added);
                _InputSource = wrapper;
                SetColor(Color);
            }
            // Oninteract will update color

            // call base method (invokes event)
            base.OnHoverEnd(input, willInteract);
        }
    }

    public override void OnInteractStart(BasisInput input)
    {
        if (_InputSource.IsInput(input) && _InputSource.GetState() == InteractInputState.Hovering )
        {
            // Set ownership to the local player
            // syncNetworking.IsOwner = true;
            SetColor(InteractColor);

            var newSource = _InputSource;
            var didSetState = newSource.TrySetState(InteractInputState.Interacting);
            Debug.Assert(didSetState);
            _InputSource = newSource;

            ButtonDown?.Invoke();
            // call base method (invokes event)
            base.OnInteractStart(input); 
        }
    }

    public override void OnInteractEnd(BasisInput input)
    {
        if (_InputSource.IsInput(input))
        {
            SetColor(Color);
            bool added = BasisInputWrapper.TryNewTracking(null, InteractInputState.NotAdded, out BasisInputWrapper wrapper);
            // setting to null should not add the tracker
            Debug.Assert(!added);
            _InputSource = wrapper;

            ButtonUp?.Invoke();
            // call base method (invokes event)
            base.OnInteractEnd(input); 
        }
    }
    public override bool IsInteractingWith(BasisInput input)
    {
        return _InputSource.IsInput(input) &&
            _InputSource.GetState() == InteractInputState.Interacting;
    }

    public override bool IsHoveredBy(BasisInput input)
    {
        return _InputSource.IsInput(input) &&
            _InputSource.GetState() == InteractInputState.Hovering;
    }

    // set material property to a color
    private void SetColor(Color color)
    {
        if (RendererRef != null && RendererRef.material != null)
        {
            RendererRef.material.SetColor(Shader.PropertyToID(PropertyName), color);
        }
    }


    private bool _triggerCleanup;
    // per-frame update, after IK transform
    public override void InputUpdate()
    {
        if (!isEnabled)
        {
            if (_triggerCleanup)
            {
                _triggerCleanup = false;
                // clean up currently hovering/interacting
                if (_InputSource.GetState() != InteractInputState.NotAdded)
                {
                    if (IsHoveredBy(_InputSource.Source))
                    {
                        OnHoverEnd(_InputSource.Source, false);
                    }
                    if (IsInteractingWith(_InputSource.Source))
                    {
                        OnInteractEnd(_InputSource.Source);
                    }
                }
                // setting same color every frame isnt optimal but fine for example
                SetColor(DisabledColor);
            }
        }
        else
        {
            _triggerCleanup = true;
        }
    }
}
