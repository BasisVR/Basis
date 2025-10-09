using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public abstract class PanelBindableElement : PanelElement
    {

        public enum PanelBindingType
        {
            Int,
            Float,
            Bool,
        }

        public string BindingKey => _bindingKey;
        private string _bindingKey;

        public abstract PanelBindingType BindingType { get; }

        public int IntValue => _intValue;
        private int _intValue;
        public float FloatValue => _floatValue;
        private float _floatValue;
        public bool BoolValue => _boolValue;
        private bool _boolValue;

        public Action OnChanged;
        public Action<int> OnIntChanged;
        public Action<float> OnFloatChanged;
        public Action<bool> OnBoolChanged;

        public bool HasBinding => _hasBinding;
        private bool _hasBinding;


        public void SetIntValue(int value)
        {
            if (!CheckType(PanelBindingType.Int)) return;

            _intValue = value;
            OnChanged?.Invoke();
            OnIntChanged?.Invoke(value);

            OnValueChanged();
            if (_hasBinding) WriteBindingValue();
        }

        public void SetFloatValue(float value)
        {
            if (!CheckType(PanelBindingType.Float)) return;

            _floatValue = value;
            OnChanged?.Invoke();
            OnFloatChanged?.Invoke(value);

            OnValueChanged();
            if (_hasBinding) WriteBindingValue();
        }

        public void SetBoolValue(bool value)
        {
            if (!CheckType(PanelBindingType.Bool)) return;

            _boolValue = value;
            OnChanged?.Invoke();
            OnBoolChanged?.Invoke(value);

            OnValueChanged();
            if (_hasBinding) WriteBindingValue();
        }

        public virtual void SetIntValueWithoutNotify(int value)
        {
            if (!CheckType(PanelBindingType.Int)) return;

            _intValue = value;
        }

        public virtual void SetFloatValueWithoutNotify(float value)
        {
            if (!CheckType(PanelBindingType.Float)) return;

            _floatValue = value;
        }

        public virtual void SetBoolValueWithoutNotify(bool value)
        {
            if (!CheckType(PanelBindingType.Bool)) return;

            _boolValue = value;
        }

        public virtual void OnValueChanged()
        {
        }

        protected virtual void WriteBindingValue()
        {
            switch (BindingType)
            {
                case PanelBindingType.Int:
                    BasisSettingsSystem.SetIntAsync(BindingKey, _intValue);
                    break;
                case PanelBindingType.Float:
                    BasisSettingsSystem.SetFloatAsync(BindingKey, _floatValue);
                    break;
                case PanelBindingType.Bool:
                    BasisSettingsSystem.SetBoolAsync(BindingKey, _boolValue);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void BindValue(string bindingPath)
        {
            _hasBinding = true;
            _bindingKey = bindingPath;
            ReadBindingValue();
        }

        protected virtual void ReadBindingValue()
        {
            switch (BindingType)
            {
                case PanelBindingType.Int:
                    SetIntValueWithoutNotify(BasisSettingsSystem.LoadInt(BindingKey));
                    break;
                case PanelBindingType.Float:
                    SetFloatValueWithoutNotify(BasisSettingsSystem.LoadFloat(BindingKey));
                    break;
                case PanelBindingType.Bool:
                    SetBoolValueWithoutNotify(BasisSettingsSystem.LoadBool(BindingKey));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private bool CheckType(PanelBindingType type)
        {
            if (BindingType == type) return true;
            Debug.LogWarning($"Attempted to write an {type} value to a {BindingType} element.", this);
            return false;
        }
    }
}
