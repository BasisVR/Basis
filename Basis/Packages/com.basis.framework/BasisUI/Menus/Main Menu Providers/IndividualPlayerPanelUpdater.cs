using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.BasisUI
{
    public class IndividualPlayerPanelUpdater : MonoBehaviour
    {
        public BasisRemotePlayer RemotePlayer;
        public PanelElementDescriptor DebugField;

        private void Update()
        {
            if (DebugField == null) return;

            if (RemotePlayer == null)
            {
                DebugField.SetDescription("RemotePlayer is null.");
                return;
            }

            var nm = BasisNetworkManagement.Instance;
            if (nm == null || nm.LocalAccessTransmitter == null)
            {
                DebugField.SetDescription("No LocalAccessTransmitter.");
                return;
            }

            var transmitter = nm.LocalAccessTransmitter;
            var results = transmitter.TransmissionResults;

            if (results == null)
            {
                DebugField.SetDescription("TransmissionResults is null.");
                return;
            }

            // Keep this lightweight unless you really need the full array scan each frame.
            DebugField.SetDescription(
                $"Interval: {results.intervalSeconds:F3}s\n" +
                $"DefaultInterval: {results.DefaultInterval:F3}s\n" +
                $"UnclampedInterval: {results.UnClampedInterval:F3}s"
            );

            // If you want the full per-player range logic, paste your old block here.
            // Tip: throttle it (e.g., update every 0.2s) so you don’t churn GC.
        }
    }
}
