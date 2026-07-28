using System;
using UnityEngine;

namespace Basis.BasisUI
{
    public abstract class PanelDataComponent<T> : PanelComponent
    {
        public BasisSettingsBinding<T> SettingsBinding { get; private set; }
        public virtual void AssignBinding(BasisSettingsBinding<T> binding)
        {
            SettingsBinding = binding;
            SetValueWithoutNotify(SettingsBinding.RawValue);
        }

        public T Value { get; protected set; }
        public Action<T> OnValueChanged { get; set; }


        public virtual void SetValue(T value)
        {
            Value = value;
            SettingsBinding?.SetValue(value);
            OnValueChanged?.Invoke(value);
            ApplyValue();
        }

        public virtual void SetValueWithoutNotify(T value)
        {
            Value = value;
            ApplyValue();
        }

        protected virtual void ApplyValue()
        {

        }

        // ---- Reset to default ----------------------------------------------------------------
        // Controls that opt in take a right-click (desktop) or thumbstick click (VR) while hovered
        // as a request to return to their default. Bound controls take the default from their
        // settings binding; callback-driven ones take it from an explicit ResetDefault.

        /// <summary>Opt in to the hover reset gesture. Off by default so only controls that want it get it.</summary>
        protected virtual bool SupportsResetGesture => false;

        /// <summary>Explicit default for controls with no settings binding.</summary>
        private T _resetDefault;
        private bool _hasExplicitResetDefault;

        /// <summary>Sets the value a reset returns to, for controls driven by callbacks rather than a binding.</summary>
        public void SetResetDefault(T value)
        {
            _resetDefault = value;
            _hasExplicitResetDefault = true;
        }

        public override bool HasResetDefault =>
            SupportsResetGesture && (SettingsBinding != null || _hasExplicitResetDefault);

        protected T ResetDefaultValue => SettingsBinding != null
            ? SettingsBinding.DefaultValue.GetDefault()
            : (_hasExplicitResetDefault ? _resetDefault : Value);

        /// <summary>
        /// Asks, via a confirmation dialogue, whether to reset this control to its default.
        /// Confirming writes the default through the normal value path so bindings and callbacks
        /// both update.
        /// </summary>
        public override void RequestReset()
        {
            if (!HasResetDefault) return;

            T target = ResetDefaultValue;
            string label = Descriptor && !string.IsNullOrEmpty(Descriptor.Title) ? Descriptor.Title : "this value";

            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (menu == null)
            {
                // No menu to host a dialogue — reset without asking rather than doing nothing.
                ApplyReset(target);
                return;
            }

            menu.OpenDialogue(
                "Reset",
                $"Reset {label} to its default?",
                BasisLocalization.Get("ui.reset"),
                BasisLocalization.Get("ui.cancel"),
                confirmed =>
                {
                    if (confirmed) ApplyReset(target);
                });
        }

        /// <summary>
        /// Moves the control to the value and applies it. SetValue alone leaves the visual where it
        /// was — it never writes the underlying Slider/Dropdown — so the default would apply without
        /// the control visibly moving. SetValueWithoutNotify moves it without re-firing the live
        /// listener; the binding and callback are then invoked explicitly.
        /// </summary>
        protected virtual void ApplyReset(T target)
        {
            SetValueWithoutNotify(target);
            SettingsBinding?.SetValue(target);
            OnValueChanged?.Invoke(target);
        }
    }
}
