using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        static BasisServerReductionSystemEvents()
        {
            // Raise the OS timer to 1ms so WaitOne keeps ~4ms accuracy on Windows (default
            // ~15ms). Windows-only: the winmm P/Invoke is never resolved on Linux/macOS
            // because the call is skipped there (those already resolve to ~1ms). try/catch so
            // a missing winmm (minimal Windows containers) degrades instead of faulting the
            // static ctor and taking the whole reduction system down with it.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { timeBeginPeriod(1); }
                catch (Exception ex) { BNL.LogError($"[BSR] timeBeginPeriod unavailable, tick timing falls back to OS default: {ex.Message}"); }
            }

            var thread = new Thread(BackgroundTickLoop)
            {
                IsBackground = true,
                Name = "BSR-TickLoop",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
        }
    }
}
