using System;
using UnityEngine.InputSystem;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Drives InputSystem.Update from the event driver. Desktop and OpenXR modes pump every frame:
    /// desktop lives on keyboard/mouse and OpenXR streams HMD/controller state through the Input
    /// System. OpenVR mode reads all VR input via the OpenVR API instead, leaving keyboard/mouse as
    /// the only Input System consumers, and those only matter while the user is actually typing or
    /// clicking — so the pump runs full rate while events are flowing and decays to a slow heartbeat
    /// after a quiet spell. Queued events are never lost; a throttled pump only delays them by at
    /// most <see cref="IdlePumpInterval"/>.
    /// </summary>
    public enum BasisInputPumpMode
    {
        /// <summary>
        /// Full rate while keyboard/mouse/controller events are flowing, slow heartbeat when idle.
        /// Only applies in OpenVR mode; Desktop and OpenXR always pump full rate.
        /// </summary>
        Adaptive,
        /// <summary>
        /// Pump every frame in every mode.
        /// </summary>
        AllInputs,
        /// <summary>
        /// Desktop devices are effectively off while in OpenVR mode; events only drain on a slow
        /// heartbeat so nothing accumulates. Desktop and OpenXR still pump full rate.
        /// </summary>
        VRDesktopInputOff,
    }

    public static class BasisInputSystemPump
    {
        public static BasisInputPumpMode Mode = BasisInputPumpMode.Adaptive;
        /// <summary>
        /// Seconds between pumps while idle in OpenVR mode.
        /// </summary>
        public static double IdlePumpInterval = 0.05;
        /// <summary>
        /// Seconds of full-rate pumping kept alive after the most recent input event.
        /// </summary>
        public static double ActivityHoldSeconds = 5;
        /// <summary>
        /// Seconds between drain pumps while in <see cref="BasisInputPumpMode.VRDesktopInputOff"/>.
        /// </summary>
        public static double DormantPumpInterval = 1;

        private static int LastTotalEventCount = -1;
        private static double FullRateUntil;
        private static double NextIdlePumpTime;

        public static void Pump(double realtimeSinceStartup)
        {
            if (Mode == BasisInputPumpMode.AllInputs
                || !string.Equals(BasisDeviceManagement.StaticCurrentMode, BasisConstants.OpenVRLoader, StringComparison.Ordinal))
            {
                InputSystem.Update();
                return;
            }

            if (Mode == BasisInputPumpMode.VRDesktopInputOff)
            {
                if (realtimeSinceStartup < NextIdlePumpTime)
                {
                    return;
                }
                InputSystem.Update();
                NextIdlePumpTime = realtimeSinceStartup + DormantPumpInterval;
                return;
            }

            if (realtimeSinceStartup >= FullRateUntil && realtimeSinceStartup < NextIdlePumpTime)
            {
                return;
            }

            InputSystem.Update();
            NextIdlePumpTime = realtimeSinceStartup + IdlePumpInterval;

            int totalEventCount = InputSystem.metrics.totalEventCount;
            if (totalEventCount != LastTotalEventCount)
            {
                LastTotalEventCount = totalEventCount;
                FullRateUntil = realtimeSinceStartup + ActivityHoldSeconds;
            }
        }
    }
}
