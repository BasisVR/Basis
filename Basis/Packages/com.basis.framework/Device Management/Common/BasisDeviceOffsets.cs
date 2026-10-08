using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Settings;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    [AutoStaticsCleanup]
    public static partial class BasisDeviceOffsets
    {
        public const string SettingsPrefix = "deviceoffset::";
        private static readonly Dictionary<string, Offset> cache = new Dictionary<string, Offset>();
        private static bool waitingForSettings;

        public static event Action<string, object> OnOffsetChanged;

        private struct Offset
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public static bool IsEligibleRole(BasisBoneTrackedRole role)
        {
            return role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand || BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role);
        }

        public static bool TryGetKey(BasisInput input, out string key)
        {
            key = null;
            if (input == null || input.IsLinked || !input.SupportsDeviceOffset || string.IsNullOrEmpty(input.CommonDeviceIdentifier))
            {
                return false;
            }
            if (!input.TryGetRole(out BasisBoneTrackedRole role) || !IsEligibleRole(role))
            {
                return false;
            }
            key = input.CommonDeviceIdentifier + "|" + role;
            return true;
        }

        public static bool TryGet(string key, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }
            if (cache.TryGetValue(key, out Offset offset))
            {
                position = offset.Position;
                rotation = offset.Rotation;
                return true;
            }
            if (!BasisSettingsSystem.SettingsLoaded)
            {
                WaitForSettings();
                return false;
            }
            string settingsKey = SettingsPrefix + key;
            if (!BasisSettingsSystem.HasSaveData(settingsKey) || !BasisDeviceOffsetMath.TryParse(BasisSettingsSystem.LoadString(settingsKey, string.Empty), out position, out rotation))
            {
                return false;
            }
            cache[key] = new Offset { Position = position, Rotation = rotation };
            return true;
        }

        public static void Set(string key, Vector3 position, Quaternion rotation, bool persist, object source)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            position = BasisDeviceOffsetMath.ClampPosition(position);
            rotation = BasisDeviceOffsetMath.Normalize(rotation);
            cache[key] = new Offset { Position = position, Rotation = rotation };
            PushToDevices(key, position, rotation);
            if (persist)
            {
                Persist(key);
            }
            OnOffsetChanged?.Invoke(key, source);
        }

        public static void Persist(string key)
        {
            if (string.IsNullOrEmpty(key) || !BasisSettingsSystem.SettingsLoaded || !cache.TryGetValue(key, out Offset offset))
            {
                return;
            }
            string settingsKey = SettingsPrefix + key;
            if (BasisDeviceOffsetMath.IsIdentity(offset.Position, offset.Rotation))
            {
                BasisSettingsSystem.DeleteSaveDataQuiet(settingsKey);
                return;
            }
            BasisSettingsSystem.SaveStringQuiet(settingsKey, BasisDeviceOffsetMath.Format(offset.Position, offset.Rotation));
        }

        public static void Clear(string key, object source)
        {
            Set(key, Vector3.zero, Quaternion.identity, true, source);
        }

        public static void ClearAll(object source)
        {
            cache.Clear();
            if (BasisSettingsSystem.SettingsLoaded)
            {
                BasisSettingsSystem.DeleteSaveDataWithPrefixQuiet(SettingsPrefix);
            }
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management != null)
            {
                var devices = management.AllInputDevices;
                int count = devices.Count;
                for (int index = 0; index < count; index++)
                {
                    BasisInput input = devices[index];
                    if (input != null)
                    {
                        input.SetDeviceOffset(Vector3.zero, Quaternion.identity);
                    }
                }
            }
            OnOffsetChanged?.Invoke(null, source);
        }

        public static void RefreshAllDevices()
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management != null)
            {
                var devices = management.AllInputDevices;
                int count = devices.Count;
                for (int index = 0; index < count; index++)
                {
                    BasisInput input = devices[index];
                    if (input != null)
                    {
                        input.RefreshDeviceOffset();
                    }
                }
            }
            OnOffsetChanged?.Invoke(null, null);
        }

        private static void PushToDevices(string key, Vector3 position, Quaternion rotation)
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return;
            }
            var devices = management.AllInputDevices;
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput input = devices[index];
                if (input != null && input.DeviceOffsetKey == key)
                {
                    input.SetDeviceOffset(position, rotation);
                }
            }
        }

        private static void WaitForSettings()
        {
            if (waitingForSettings)
            {
                return;
            }
            waitingForSettings = true;
            BasisSettingsSystem.OnSettingsFinishedChanges -= OnSettingsLoaded;
            BasisSettingsSystem.OnSettingsFinishedChanges += OnSettingsLoaded;
        }

        private static void OnSettingsLoaded()
        {
            if (!BasisSettingsSystem.SettingsLoaded)
            {
                return;
            }
            BasisSettingsSystem.OnSettingsFinishedChanges -= OnSettingsLoaded;
            waitingForSettings = false;
            RefreshAllDevices();
        }
    }
}
