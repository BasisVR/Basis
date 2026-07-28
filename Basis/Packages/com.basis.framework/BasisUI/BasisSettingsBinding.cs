using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Basis.Scripts.Settings;

namespace Basis
{
    public interface IBasisSettingsBinding
    {
        void ReloadAndNotify();
    }

    /// <summary>
    /// Re-loads a settings class's bindings once the settings file has been read. Binding
    /// constructors self-load, but static init triggered from any RuntimeInitializeOnLoadMethod
    /// runs before BasisSettingsSystem.Initialize (a Start hook) — those bindings only ever see
    /// defaults: saved values restore into the store but never into RawValue. Package settings
    /// classes (which the framework's BasisSettingsDefaults.LoadAll can't know about) register
    /// here from their static constructor.
    /// </summary>
    public static class BasisSettingsBindingPostLoad
    {
        public static void Register(Type settingsClass)
        {
            if (BasisSettingsSystem.SettingsLoaded)
            {
                return;
            }

            void Reload()
            {
                BasisSettingsSystem.OnSettingsFinishedChanges -= Reload;
                foreach (FieldInfo field in settingsClass.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.GetValue(null) is IBasisSettingsBinding binding)
                    {
                        binding.ReloadAndNotify();
                    }
                }
            }

            // Every invocation of this event happens with the store loaded (LoadAllSettings sets
            // _settingsLoaded before invoking; saves defer events until loaded), so the first
            // firing is a safe reload point.
            BasisSettingsSystem.OnSettingsFinishedChanges += Reload;
        }
    }

    public class BasisSettingsBinding<T> : IBasisSettingsBinding
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

        public void ResetToDefault()
        {
            SetValue(DefaultValue.GetDefault());
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

        public void ReloadAndNotify()
        {
            T previous = RawValue;
            LoadBindingValue();
            if (!EqualityComparer<T>.Default.Equals(previous, RawValue))
            {
                OnChanged?.Invoke(RawValue);
            }
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
