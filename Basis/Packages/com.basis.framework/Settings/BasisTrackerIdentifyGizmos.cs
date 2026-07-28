using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.Scripts.Debugging
{
    public static class BasisTrackerIdentifyGizmos
    {
        private sealed class Entry
        {
            public int GizmoId;
            public Color Color;
        }

        /// <summary>
        /// Identify colours, in pick order. Two constraints the old palette missed:
        ///
        /// Red and green sat at indices 0 and 1, so the first two trackers a user
        /// identified — in full body, almost always the two feet — came out in the
        /// one pair of hues red-green colourblindness cannot separate (~8% of men).
        /// Blue and orange lead here instead: that pair survives every common form
        /// of colour blindness, and the rest are spread so the early picks stay far
        /// apart in hue AND in brightness, which is the only cue left to someone who
        /// sees no hue at all.
        ///
        /// Every entry also has to read against the dark menu background (palette
        /// BackgroundColor1, ~0.1 luma) as small text, and survive being multiplied
        /// by <see cref="EmissionIntensity"/> into an emissive sphere in-world.
        /// </summary>
        private static readonly Color[] Palette =
        {
            new Color(0.30f, 0.60f, 1.00f), // blue
            new Color(1.00f, 0.55f, 0.10f), // orange
            new Color(0.00f, 0.80f, 0.65f), // teal
            new Color(0.95f, 0.45f, 0.75f), // pink
            new Color(0.95f, 0.87f, 0.25f), // yellow
            new Color(0.68f, 0.55f, 1.00f), // purple
            new Color(0.95f, 0.32f, 0.22f), // vermillion
            new Color(0.60f, 0.90f, 0.35f), // lime
        };

        // Device id -> palette index. Sticky for the process lifetime so a tracker keeps
        // its colour when identify is toggled off and on, when the settings tab is
        // reopened, and when the device drops and reconnects.
        private static readonly Dictionary<string, int> ColorIndexById = new Dictionary<string, int>();

        private const float BaseSize = 0.07f;
        private const float EmissionIntensity = 2.5f;

        private static readonly Dictionary<BasisInput, Entry> Active = new Dictionary<BasisInput, Entry>();
        private static readonly List<BasisInput> _stale = new List<BasisInput>();
        private static bool _registered;

        public static event Action OnIdentifyChanged;

        public static bool HasActive => Active.Count != 0;

        /// <summary>True while this tracker's identify sphere is lit.</summary>
        public static bool IsShowing(BasisInput input)
        {
            return input != null && Active.ContainsKey(input);
        }

        /// <summary>
        /// The colour this tracker identifies as, whether or not it is currently lit.
        /// Stable for a given device, so the UI can show the swatch up front rather
        /// than only revealing it once the sphere is on.
        /// </summary>
        public static Color ColorFor(BasisInput input)
        {
            return Palette[IndexFor(input != null ? input.UniqueDeviceIdentifier : null)];
        }

        private static int IndexFor(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (ColorIndexById.TryGetValue(id, out int existing)) return existing;

            // Probe from the hashed slot for one no other known device has taken, so no
            // two trackers ever light up the same colour while there are still colours
            // left. Past Palette.Length devices the hash decides and duplicates are
            // accepted — by then the list is far past the point colour alone identifies
            // anything.
            int preferred = (int)(StableHash(id) % (uint)Palette.Length);
            for (int probe = 0; probe < Palette.Length; probe++)
            {
                int candidate = (preferred + probe) % Palette.Length;
                if (!ColorIndexById.ContainsValue(candidate))
                {
                    ColorIndexById[id] = candidate;
                    return candidate;
                }
            }

            ColorIndexById[id] = preferred;
            return preferred;
        }

        // FNV-1a. string.GetHashCode is randomized per process, so it would hand the
        // same tracker a different colour on every launch.
        private static uint StableHash(string value)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 16777619u;
            }
            return hash;
        }

        public static void Toggle(BasisInput input)
        {
            if (input == null) return;
            EnsureMasterToggleHook();

            if (Active.TryGetValue(input, out Entry existing))
            {
                BasisGizmoManager.DestroyGizmo(existing.GizmoId);
                Active.Remove(input);
                OnIdentifyChanged?.Invoke();
                return;
            }

            Color color = ColorFor(input);
            Color glow = color * EmissionIntensity;
            glow.a = 1f;
            float size = BaseSize * HeightScale();
            string label = input.TryGetRole(out var role) ? role.ToString() : "Tracker";
            if (BasisGizmoManager.CreateSphereGizmo($"Identify_{label}", out int id, input.transform.position, size, glow))
            {
                Active[input] = new Entry { GizmoId = id, Color = color };
                OnIdentifyChanged?.Invoke();
            }
        }

        public static void Tick()
        {
            if (Active.Count == 0) return;

            Vector3 scale = Vector3.one * (BaseSize * HeightScale());

            foreach (KeyValuePair<BasisInput, Entry> kvp in Active)
            {
                BasisInput input = kvp.Key;
                if (input == null)
                {
                    _stale.Add(input);
                    continue;
                }
                BasisGizmoManager.UpdateSphereGizmo(kvp.Value.GizmoId, input.transform.position, scale);
            }

            if (_stale.Count == 0) return;

            for (int i = 0; i < _stale.Count; i++)
            {
                if (Active.TryGetValue(_stale[i], out Entry e))
                {
                    BasisGizmoManager.DestroyGizmo(e.GizmoId);
                    Active.Remove(_stale[i]);
                }
            }
            _stale.Clear();
            OnIdentifyChanged?.Invoke();
        }

        public static void ClearAll()
        {
            if (Active.Count == 0) return;
            foreach (KeyValuePair<BasisInput, Entry> kvp in Active)
            {
                BasisGizmoManager.DestroyGizmo(kvp.Value.GizmoId);
            }
            Active.Clear();
            OnIdentifyChanged?.Invoke();
        }

        private static float HeightScale()
        {
            float scale = BasisHeightDriver.ScaledToMatchValue;
            return scale > 0f ? scale : 1f;
        }

        private static void EnsureMasterToggleHook()
        {
            if (_registered) return;
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            _registered = true;
        }

        private static void OnMasterToggleChanged(bool state)
        {
            if (state || Active.Count == 0) return;
            Active.Clear();
            OnIdentifyChanged?.Invoke();
        }
    }
}
