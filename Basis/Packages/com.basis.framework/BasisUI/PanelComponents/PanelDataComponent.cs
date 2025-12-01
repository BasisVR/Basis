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
    }
}
