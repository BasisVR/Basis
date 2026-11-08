using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Contacts
{
    public enum BasisBodyHapticZone : byte
    {
        Head = 0,
        ChestLeft = 1,
        ChestRight = 2,
        StomachLeft = 3,
        StomachRight = 4,
        UpperBackLeft = 5,
        UpperBackRight = 6,
        LowerBackLeft = 7,
        LowerBackRight = 8,
        UpperArmLeft = 9,
        UpperArmRight = 10,
        ForearmLeft = 11,
        ForearmRight = 12,
        ThighLeft = 13,
        ThighRight = 14,
        ShinLeft = 15,
        ShinRight = 16,
    }

    public interface IBasisBodyHapticsOutput
    {
        void OnBodyHapticsChanged(float[] intensities);
    }

    [NoAutoStaticsCleanup]
    public static class BasisBodyHaptics
    {
        public const int ZoneCount = 17;
        public const float ChangeThreshold = 0.005f;
        public static bool Enabled;
        public static bool FromOthers = true;
        public static bool FromSelf;
        public static float IntensityCap = 1f;
        public static readonly float[] Intensities = new float[ZoneCount];
        private static readonly List<IBasisBodyHapticsOutput> outputs = new List<IBasisBodyHapticsOutput>();
        public static int OutputCount => outputs.Count;

        public static void RegisterOutput(IBasisBodyHapticsOutput output)
        {
            if (output != null && !outputs.Contains(output))
            {
                outputs.Add(output);
            }
        }

        public static void UnregisterOutput(IBasisBodyHapticsOutput output)
        {
            outputs.Remove(output);
        }

        public static void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                ClearAll();
            }
        }

        public static void ClearAll()
        {
            bool changed = false;
            for (int index = 0; index < ZoneCount; index++)
            {
                if (Intensities[index] != 0f)
                {
                    Intensities[index] = 0f;
                    changed = true;
                }
            }
            if (changed)
            {
                Notify();
            }
        }

        public static void Submit(float[] values)
        {
            bool changed = false;
            float cap = Mathf.Clamp01(IntensityCap);
            for (int index = 0; index < ZoneCount; index++)
            {
                float value = Mathf.Clamp01(values[index]) * cap;
                float previous = Intensities[index];
                if (Mathf.Abs(value - previous) > ChangeThreshold || (value == 0f) != (previous == 0f))
                {
                    Intensities[index] = value;
                    changed = true;
                }
            }
            if (changed)
            {
                Notify();
            }
        }

        private static void Notify()
        {
            for (int index = outputs.Count - 1; index >= 0; index--)
            {
                try
                {
                    outputs[index].OnBodyHapticsChanged(Intensities);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Body haptics output threw: {e}");
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Array.Clear(Intensities, 0, ZoneCount);
        }
    }
}
