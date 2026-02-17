using System;
using UnityEngine;

namespace Basis
{

    public class BasisSettingsBinding<T>
    {

        public string BindingKey { get; }

        public T RawValue;
        public BasisPlatformDefault<T> DefaultValue;
        public Action<T> OnChanged;

        public static implicit operator bool(BasisSettingsBinding<T> obj) => obj != null;


        public BasisSettingsBinding(string bindingPath)
        {
            BindingKey = bindingPath;
            DefaultValue = new BasisPlatformDefault<T>();
            LoadBindingValue();
        }

        public BasisSettingsBinding(string bindingPath, BasisPlatformDefault<T> defaultValue)
        {
            BindingKey = bindingPath;
            DefaultValue = defaultValue;
            LoadBindingValue();
        }


        public void SetValue(T value)
        {
            RawValue = value;
            OnChanged?.Invoke(value);
            WriteBindingValue();
        }

        public void SetValueWithoutNotify(T value)
        {
            RawValue = value;
        }

        private void WriteBindingValue()
        {
            switch (RawValue)
            {
                case int intValue:
                    BasisSettingsSystem.SaveInt(BindingKey, intValue);
                    break;
                case Enum enumValue:
                    BasisSettingsSystem.SaveInt(BindingKey, Convert.ToInt32(enumValue));
                    break;
                case float floatValue:
                    BasisSettingsSystem.SaveFloat(BindingKey, floatValue);
                    break;
                case bool boolValue:
                    BasisSettingsSystem.SaveBool(BindingKey, boolValue);
                    break;
                case string stringValue:
                    BasisSettingsSystem.SaveString(BindingKey, stringValue);
                    break;
                default:
                    Debug.LogError(
                        $"[PanelBinding] Variable type {typeof(T)} not currently supported by BasisSettingsSystem value storage. No value will be stored.");
                    break;
            }
        }

        public void LoadBindingValue()
        {
            switch (typeof(T))
            {
                case var t when t == typeof(int):
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadInt(BindingKey, (int)(object)DefaultValue.GetDefault()));
                    break;
                case var t when t == typeof(Enum):
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadInt(BindingKey, (int)(object)DefaultValue.GetDefault()));
                    break;
                case var t when t == typeof(float):
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadFloat(BindingKey, (float)(object)DefaultValue.GetDefault()));
                    break;
                case var t when t == typeof(bool):
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadBool(BindingKey, (bool)(object)DefaultValue.GetDefault()));
                    break;
                case var t when t == typeof(string):
                    SetValueWithoutNotify((T)(object)BasisSettingsSystem.LoadString(BindingKey, (string)(object)DefaultValue.GetDefault()));
                    break;
                default:
                    Debug.LogError(
                        $"[PanelBinding] Variable type {typeof(T)} not currently supported by BasisSettingsSystem value storage ({RawValue.GetType()}). No value will be loaded.");
                    break;
            }
        }
    }
}
