using System;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Static event hub for HMD user-presence (proximity sensor / isPresent).
    /// Each VR SDK (OpenVR, OpenXR) reports presence from its own assembly via <see cref="ReportPresence"/>.
    /// Consumers subscribe to <see cref="OnPresenceChanged"/> — debounced to reject flicker.
    /// </summary>
    public static class BasisHMDPresence
    {
        /// <summary>
        /// Current committed presence state. Only changes after the debounce window.
        /// </summary>
        public static bool IsPresent { get; private set; } = false;

        /// <summary>
        /// Fired when <see cref="IsPresent"/> changes after debounce.
        /// </summary>
        public static event Action<bool> OnPresenceChanged;

        /// <summary>
        /// How long the reported value must differ from <see cref="IsPresent"/>
        /// before the change is committed and the event fires.
        /// </summary>
        public static float DebounceSeconds = 1.5f;

        private static bool _hasPending;
        private static bool _pendingValue;
        private static float _lastChangeTime;

        /// <summary>
        /// Whether any VR presence provider is compiled into this build.
        /// </summary>
        public static bool HasPresenceProvider
        {
            get
            {
#if BASIS_HAS_OPENVR || BASIS_HAS_OPENXR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Called every frame by the active VR SDK's <c>Simulate()</c> with the current
        /// proximity sensor / user-presence value. Debounces before committing a change.
        /// </summary>
        /// <param name="present">Whether the headset detects a user wearing it.</param>
        public static void ReportPresence(bool present)
        {
            if (present == IsPresent)
            {
                // Matches committed state — cancel any pending change (flicker rejection)
                _hasPending = false;
                return;
            }

            if (!_hasPending || _pendingValue != present)
            {
                // New differing value or direction changed — start/restart timer
                _pendingValue = present;
                _hasPending = true;
                _lastChangeTime = Time.time;
                return;
            }

            // Same pending value — check if stable long enough
            if (Time.time - _lastChangeTime >= DebounceSeconds)
            {
                IsPresent = present;
                _hasPending = false;
                try
                {
                    OnPresenceChanged?.Invoke(present);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"BasisHMDPresence: Listener threw — {e}");
                }
            }
        }

        /// <summary>
        /// Immediately sets presence without debounce. Use for hard mode transitions
        /// (e.g., VR SDK startup/shutdown) where the state is known with certainty.
        /// </summary>
        public static void ForcePresence(bool present)
        {
            if (present == IsPresent) return;
            IsPresent = present;
            _hasPending = false;
            try
            {
                OnPresenceChanged?.Invoke(present);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"BasisHMDPresence: Listener threw — {e}");
            }
        }

        /// <summary>
        /// Resets all state. Called during shutdown.
        /// </summary>
        public static void Reset()
        {
            IsPresent = false;
            _hasPending = false;
            OnPresenceChanged = null;
        }
    }
}
