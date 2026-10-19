using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Highlight;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    public class BasisExternalLinkInteractable : BasisInteractableObject
    {
        [Tooltip("The web page to open, starting with https://. Players always see the address and choose whether to open it.")]
        public string Url = "https://";
        public bool UseHighlight = true;
        [Tooltip("Optional, leave empty to highlight the renderer on this object.")]
        public Renderer HighlightRenderer;
        [Tooltip("Runs after the player chose to open the page.")]
        public UnityEvent OnOpened = new UnityEvent();
        [Tooltip("Runs when the page was not opened: the player said no, link requests are off, or the address is not allowed. Use it to show the address another way.")]
        public UnityEvent OnNotOpened = new UnityEvent();

        private BasisInputWrapper source;

        public override void Awake()
        {
            base.Awake();
            if (HighlightRenderer == null) TryGetComponent(out HighlightRenderer);
        }

        public void RequestOpen()
        {
            BasisExternalLinks.Request(Url, gameObject, Resolve);
        }

        public override bool CanHover(BasisInput input)
        {
            return source.GetState() == BasisInteractInputState.NotAdded && InteractableEnabled && IsWithinRange(input.transform.position, InteractRange);
        }

        public override bool CanInteract(BasisInput input)
        {
            return source.IsInput(input) && source.GetState() == BasisInteractInputState.Hovering && InteractableEnabled && IsWithinRange(input.transform.position, InteractRange);
        }

        public override bool IsHoveredBy(BasisInput input)
        {
            return source.IsInput(input) && source.GetState() == BasisInteractInputState.Hovering;
        }

        public override bool IsInteractingWith(BasisInput input)
        {
            return source.IsInput(input) && source.GetState() == BasisInteractInputState.Interacting;
        }

        public override void OnHoverStart(BasisInput input)
        {
            if (!BasisInputWrapper.TryNewTracking(input, BasisInteractInputState.Hovering, out BasisInputWrapper wrapper)) return;
            Track(wrapper);
            SetHighlight(true);
            base.OnHoverStart(input);
        }

        public override void OnHoverEnd(BasisInput input, bool willInteract)
        {
            if (!source.IsInput(input)) return;
            if (!willInteract)
            {
                Track(default);
                SetHighlight(false);
            }
            base.OnHoverEnd(input, willInteract);
        }

        public override void OnInteractStart(BasisInput input)
        {
            if (!source.IsInput(input) || source.GetState() != BasisInteractInputState.Hovering) return;
            BasisInputWrapper interacting = source;
            interacting.TrySetState(BasisInteractInputState.Interacting);
            Track(interacting);
            base.OnInteractStart(input);
            RequestOpen();
        }

        public override void OnInteractEnd(BasisInput input)
        {
            if (!source.IsInput(input)) return;
            Track(default);
            SetHighlight(false);
            base.OnInteractEnd(input);
        }

        public override void OnDestroy()
        {
            SetHighlight(false);
            base.OnDestroy();
        }

        private void OnDisable()
        {
            SetHighlight(false);
        }

        private void Resolve(BasisExternalLinks.Result result)
        {
            if (this == null) return;
            if (result == BasisExternalLinks.Result.Opened) OnOpened?.Invoke();
            else OnNotOpened?.Invoke();
        }

        private void Track(BasisInputWrapper wrapper)
        {
            source = wrapper;
            Inputs = new(0);
            if (wrapper.Source != null) Inputs.SetInputByRole(wrapper.Source, wrapper.GetState());
        }

        private void SetHighlight(bool on)
        {
            if (UseHighlight && HighlightRenderer != null) BasisHighlightManager.SetHighlight(HighlightRenderer, on);
        }
    }
}
