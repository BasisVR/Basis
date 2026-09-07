using System;
using System.Collections.Generic;
using System.Globalization;
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

        public override string BoundSettingKey => SettingsBinding?.BindingKey;

        protected T ResetDefaultValue => SettingsBinding != null
            ? SettingsBinding.DefaultValue.GetDefault()
            : (_hasExplicitResetDefault ? _resetDefault : Value);

        /// <summary>
        /// Opens this control's options window: what can be done with it from a hover. Reset to
        /// default is the one every control offers, and it is the accept, so the gesture still
        /// reaches it in a single press. Confirming writes the default through the normal value path
        /// so bindings and callbacks both update; controls with more to offer add it in
        /// <see cref="AddPanelOptions"/>.
        /// </summary>
        public override void RequestReset()
        {
            if (!HasResetDefault) return;

            T target = ResetDefaultValue;
            string label = Descriptor && !string.IsNullOrEmpty(Descriptor.Title) ? Descriptor.Title : "this value";

            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (menu == null)
            {
                // No menu to host a window — reset without asking rather than doing nothing.
                ApplyReset(target);
                return;
            }

            // OpenDialogue refuses while another modal is already up, and would leave that one in
            // Dialogue. Without this the options below would be grafted onto that unrelated window.
            if (menu.Dialogue != null) return;

            menu.OpenDialogue(
                BasisLocalization.Get("ui.panelOptions.title"),
                string.Format(BasisLocalization.Get("ui.panelOptions.body"), label),
                BasisLocalization.Get("ui.reset"),
                BasisLocalization.Get("ui.cancel"),
                confirmed =>
                {
                    if (confirmed) ApplyReset(target);
                });

            if (menu.Dialogue != null) AddPanelOptions(menu.Dialogue);
        }

        public override void ApplyResetToDefault()
        {
            if (!HasResetDefault) return;
            ApplyReset(ResetDefaultValue);
        }

        /// <summary>
        /// Adds whatever this control offers beyond a reset to its open options window. The third
        /// dialogue button is the only slot going spare, so a control gets one thing;
        /// <see cref="PanelSlider"/> spends it on the thumbstick bind.
        /// </summary>
        protected virtual void AddPanelOptions(BasisMenuDialoguePanel dialogue)
        {
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

        /// <summary>
        /// Writes a value that came from somewhere other than this control — a thumbstick driving
        /// it (<see cref="BasisPanelJoystickBind"/>), say. Takes the same path as a reset: the
        /// control is moved without re-firing its own live listener, then the binding and callback
        /// are invoked, which is what a pointer does when it lets go.
        /// </summary>
        public void ApplyDrivenValue(T value) => ApplyReset(value);

        public override bool TryDescribeSettingChange(out string label, out string currentText, out string defaultText)
        {
            label = null;
            currentText = null;
            defaultText = null;
            if (SettingsBinding == null && !_hasExplicitResetDefault) return false;

            // A bound control's stored value is the truth even while the page showing it is stale;
            // a callback-driven one — every control on the camera panel — only ever has what it shows.
            T current = SettingsBinding != null ? SettingsBinding.RawValue : Value;
            T standard = ResetDefaultValue;
            if (EqualityComparer<T>.Default.Equals(current, standard)) return false;

            label = Descriptor && !string.IsNullOrEmpty(Descriptor.Title) ? Descriptor.Title : BoundSettingKey;
            if (string.IsNullOrEmpty(label)) return false;

            currentText = FormatSettingValue(current);
            defaultText = FormatSettingValue(standard);
            return true;
        }

        /// <summary>
        /// Writes one of this control's values the way the control itself would show it, for the
        /// reset dialogue's change list. Controls with richer displays (slider units, dropdown
        /// labels) override this.
        /// </summary>
        protected virtual string FormatSettingValue(T value)
        {
            switch (value)
            {
                case bool boolValue:
                    return BasisLocalization.Get(boolValue ? "ui.on" : "ui.off");
                case float floatValue:
                    return floatValue.ToString("0.###", CultureInfo.InvariantCulture);
                default:
                    return value != null ? value.ToString() : string.Empty;
            }
        }
    }
}
