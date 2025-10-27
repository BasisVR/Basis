using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public abstract class PanelBindableElement<T> : PanelElement
    {

        public string BindingKey => _bindingKey;
        private string _bindingKey;

        public T RawValue;
        public BasisPlatformDefault<T> DefaultValue;

        public Action<T> OnChanged;

        public bool HasBinding => _hasBinding;
        private bool _hasBinding;

        public void SetValue(T value)
        {
            RawValue = value;
            OnChanged?.Invoke(value);
            OnValueChanged();
            if (_hasBinding) WriteBindingValue();
        }

        public virtual void SetValueWithoutNotify(T value)
        {
            RawValue = value;
        }

        public virtual void OnValueChanged()
        {
        }

        protected virtual void WriteBindingValue()
        {
            switch (RawValue)
            {
                case int intValue:
                    BasisSettingsSystem.SetInt(BindingKey, intValue);
                    break;
                case Enum enumValue:
                    BasisSettingsSystem.SetInt(BindingKey, Convert.ToInt32(enumValue));
                    break;
                case float floatValue:
                    BasisSettingsSystem.SetFloat(BindingKey, floatValue);
                    break;
                case bool boolValue:
                    BasisSettingsSystem.SetBool(BindingKey, boolValue);
                    break;
                default:
                    Debug.LogError(
                        $"[PanelBinding] Variable type {typeof(T)} not currently supported by BasisSettingsSystem value storage. No value will be stored.",
                        this);
                    break;
            }
        }

        public void BindValue(string bindingPath)
        {
            _hasBinding = true;
            _bindingKey = bindingPath;
            ReadBindingValue();
        }

        public void BindValue(string bindingPath, BasisPlatformDefault<T> defaultValue)
        {
            _hasBinding = true;
            _bindingKey = bindingPath;
            DefaultValue = defaultValue;
            ReadBindingValue();
        }

        protected virtual void ReadBindingValue()
        {
            switch (RawValue)
            {
                case int _:
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadInt(BindingKey));
                    break;
                case Enum _:
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadInt(BindingKey));
                    break;
                case float _:
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadFloat(BindingKey));
                    break;
                case bool _:
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadBool(BindingKey));
                    break;
                default:
                    Debug.LogError(
                        $"[PanelBinding] Variable type {typeof(T)} not currently supported by BasisSettingsSystem value storage. No value will be loaded.",
                        this);
                    break;
            }

            OnBoundValueLoaded();
        }

        protected virtual void OnBoundValueLoaded()
        {
        }

    }
}
