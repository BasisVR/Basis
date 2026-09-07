using System;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(PanelElementDescriptor))]
    public abstract class PanelComponent : AddressableUIInstanceBase, IPointerEnterHandler, IPointerExitHandler
    {

        public PanelElementDescriptor Descriptor
        {
            get
            {
                if (_descriptor) return _descriptor;
                if (this == null) return null;
                _descriptor = GetComponent<PanelElementDescriptor>();
                return _descriptor;
            }
        }

        private PanelElementDescriptor _descriptor;

        private string _disabledReason;
        private bool _pointerInside;

        /// <summary>
        /// The Selectable that owns this control's interactable state (dropdown, toggle, slider,
        /// button, input field). Null for elements that have no interactive control.
        /// </summary>
        protected virtual Selectable InteractableTarget => null;

        /// <summary>True while the pointer is over this control.</summary>
        public bool PointerInside => _pointerInside;

        /// <summary>True when this control has no Selectable, or its Selectable is interactable.</summary>
        public bool IsInteractable
        {
            get
            {
                Selectable target = InteractableTarget;
                return target == null || target.interactable;
            }
        }

        /// <summary>
        /// True when this control knows a value to reset to, so the reset gesture is offered
        /// while it is hovered. See <see cref="BasisPanelResetGesture"/>.
        /// </summary>
        public virtual bool HasResetDefault => false;

        public virtual bool HasPanelOptions => HasResetDefault;

        public virtual string BoundSettingKey => null;

        /// <summary>Asks to reset this control to its default. No-op unless the control supports it.</summary>
        public virtual void RequestReset()
        {
        }

        /// <summary>
        /// Writes this control's default straight in, with no window and no question. The gesture on
        /// a single control asks first; a section reset has already asked once for the whole section,
        /// so its rows are written rather than each opening a confirmation of its own.
        /// </summary>
        public virtual void ApplyResetToDefault()
        {
        }

        /// <summary>
        /// Describes how this control's bound setting currently differs from its default, for the
        /// panel Reset dialogue's list of what a page reset would change. False when the control
        /// has no settings binding or its value already is the default.
        /// </summary>
        public virtual bool TryDescribeSettingChange(out string label, out string currentText, out string defaultText)
        {
            label = null;
            currentText = null;
            defaultText = null;
            return false;
        }

        /// <summary>
        /// Enables or disables this control. When disabling, pass a short reason describing why —
        /// it is shown in the hover tooltip so a greyed-out control explains itself instead of
        /// looking broken. The reason is cleared automatically when the control is re-enabled.
        /// </summary>
        public virtual void SetInteractable(bool interactable, string disabledReason = null)
        {
            _disabledReason = interactable ? null : disabledReason;

            Selectable target = InteractableTarget;
            if (target != null) target.interactable = interactable;

            if (_pointerInside) BasisMainMenu.ShowTooltip(HoverTooltipText);
        }

        /// <summary>
        /// Optional lazy source for the hover tooltip, checked before the descriptor's own text.
        /// The bar only reads a tooltip when the pointer arrives, so a row whose text changes
        /// continuously (live distances, "joined Xs ago") can supply it here instead of pushing a
        /// freshly formatted string through <see cref="PanelElementDescriptor.SetTooltip"/> every
        /// tick — that per-tick string is work nobody ever sees.
        /// </summary>
        public Func<string> TooltipProvider;

        /// <summary>
        /// Text shown in the hover tooltip bar. While disabled with a reason, that reason is
        /// shown so a greyed-out control explains itself. Otherwise prefers the element's
        /// description, falling back to its title — controls like toggles/sliders/dropdowns
        /// only set a title. Override to surface something more specific.
        /// </summary>
        protected virtual string TooltipText
        {
            get
            {
                if (!IsInteractable && !string.IsNullOrEmpty(_disabledReason)) return _disabledReason;
                if (TooltipProvider != null)
                {
                    string live = TooltipProvider();
                    if (!string.IsNullOrEmpty(live)) return live;
                }
                if (!Descriptor) return null;
                if (!string.IsNullOrEmpty(Descriptor.Tooltip)) return Descriptor.Tooltip;
                return string.IsNullOrEmpty(Descriptor.Description) ? Descriptor.Title : Descriptor.Description;
            }
        }

        /// <summary>
        /// What the tooltip bar actually shows: the control's own text, plus the reset gesture
        /// while there is a default to go back to. Nothing else advertises that gesture, and a
        /// hover is the moment it is about to be useful. A joystick bind being armed — or already
        /// holding this control — takes that spot instead, since it is the more immediate offer.
        /// </summary>
        protected virtual string HoverTooltipText
        {
            get
            {
                string text = TooltipText;
                if (!IsInteractable) return text;

                string hint = BasisPanelJoystickBind.HintFor(this);
                if (string.IsNullOrEmpty(hint))
                {
                    if (!HasPanelOptions) return text;
                    hint = BasisPanelResetGesture.HintText;
                }
                if (string.IsNullOrEmpty(hint)) return text;
                return string.IsNullOrEmpty(text) ? hint : $"{text} — {hint}";
            }
        }

        /// <summary>
        /// Re-pushes this control's tooltip into the bar, for controls whose text changes while
        /// the pointer is still on them — a dropdown moving between its open options, say.
        /// </summary>
        protected void RefreshHoverTooltip()
        {
            if (!_pointerInside) return;
            BasisMainMenu.ShowTooltip(HoverTooltipText);
        }

        public virtual void OnPointerEnter(PointerEventData eventData)
        {
            _pointerInside = true;
            BasisMainMenu.ShowTooltip(HoverTooltipText);
            BasisPanelResetGesture.SetHovered(this, eventData);
        }

        public virtual void OnPointerExit(PointerEventData eventData)
        {
            _pointerInside = false;
            BasisMainMenu.HideTooltip();
            BasisPanelResetGesture.ClearHovered(this, eventData);
        }

        // A closing menu tears its elements down without a pointer exit, so drop the hover here
        // too rather than leaving the gesture poll pointed at a dead control.
        protected override void OnDisable()
        {
            base.OnDisable();
            if (_pointerInside) BasisMainMenu.HideTooltip();
            _pointerInside = false;
            BasisPanelResetGesture.ClearHovered(this);
        }

        [UsedImplicitly]
        public virtual void OnComponentUsed()
        {
        }
    }
}
