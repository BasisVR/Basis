using Basis.BasisUI;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
namespace Basis.Capture
{
    public class BasisCapturePanelUpdater : MonoBehaviour
    {
        private PanelElementDescriptor status;
        private PanelButton record;
        private PanelButton lockButton;
        private List<GameObject> recorderOnly;
        private RectTransform container;
        private string lastStatus, lastButton, lastLock;
        private bool lastRecorderVisible = true, lastLockVisible = true;
        private float next;
        private readonly StringBuilder sb = new StringBuilder();
        public void Initialize(PanelElementDescriptor status, PanelButton record, PanelButton lockButton, List<GameObject> recorderOnly, RectTransform container)
        {
            this.lockButton = lockButton;
            this.status = status;
            this.record = record;
            this.recorderOnly = recorderOnly;
            this.container = container;
            status.DisableRichText();
            Refresh();
        }
        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.25f;
            Refresh();
        }
        private void Refresh()
        {
            BasisCaptureSession s = BasisCaptureSession.Current;
            sb.Clear();
            if (s == null) sb.Append(BasisLocalization.Get("settings.developer.capture.status.idle"));
            else
            {
                string state = s.State switch
                {
                    BasisCaptureSession.SessionState.Recording => s.options.peerMode ? BasisLocalization.Get("settings.developer.capture.status.peer") : BasisLocalization.Get("settings.developer.capture.status.recording"),
                    BasisCaptureSession.SessionState.Finalizing => BasisLocalization.Get("settings.developer.capture.status.exporting", Mathf.RoundToInt(s.Assets.Progress * 100f).ToString()),
                    BasisCaptureSession.SessionState.Failed => BasisLocalization.Get("settings.developer.capture.status.failed", s.Error ?? ""),
                    _ => BasisLocalization.Get("settings.developer.capture.status.done"),
                };
                sb.Append(state).Append('\n');
                sb.Append(BasisLocalization.Get("settings.developer.capture.status.stats", Format(s.Elapsed), s.Frame.ToString(), s.ActorCount.ToString(), (s.BytesWritten / (1024f * 1024f)).ToString("0.0"), s.Assets.MeshCount.ToString(), s.Assets.TextureCount.ToString()));
                sb.Append('\n').Append(s.Folder);
            }
            if (Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected)
            {
                string key = !BasisCaptureNetwork.HaveLockState ? "settings.developer.capture.lock.unsupported" : !BasisCaptureNetwork.LocalMayCollect ? "settings.developer.capture.lock.adminsOnly" : BasisCaptureNetwork.ServerLocked ? (BasisCaptureNetwork.MayBypassLock ? "settings.developer.capture.lock.bypass" : "settings.developer.capture.lock.locked") : "settings.developer.capture.lock.open";
                sb.Append('\n').Append(BasisLocalization.Get(key));
            }
            if (BasisCaptureNetwork.ConsentMode && BasisCapture.Recording) sb.Append('\n').Append(BasisLocalization.Get("settings.developer.capture.consent.summary", BasisCaptureNetwork.CountConsent(true).ToString(), BasisCaptureNetwork.CountConsent(false).ToString(), BasisCaptureNetwork.CountConsent(null).ToString()));
            else if (BasisCapture.Recording && BasisCaptureNetwork.NeedsConsent && BasisCaptureSession.Current.options.remotes) sb.Append('\n').Append(BasisLocalization.Get("settings.developer.capture.consent.unavailable"));
            if (BasisCaptureNetwork.LastDenied != null) sb.Append('\n').Append(BasisCaptureNetwork.LastDenied);
            foreach (BasisCaptureNetwork.PeerStatus p in BasisCaptureNetwork.Peers.Values)
            {
                sb.Append('\n').Append(p.name).Append(": ").Append(p.state);
                if (p.total > 0) sb.Append(' ').Append((p.received / (1024f * 1024f)).ToString("0.0")).Append('/').Append((p.total / (1024f * 1024f)).ToString("0.0")).Append(" MB");
            }
            string text = sb.ToString();
            if (text != lastStatus) { status.SetDescription(text); lastStatus = text; }
            string title = BasisCapture.Recording ? BasisLocalization.Get("settings.developer.capture.stop") : BasisLocalization.Get("settings.developer.capture.start");
            if (title != lastButton) { record.Descriptor.SetTitle(title); lastButton = title; }
            bool lockVisible = BasisCaptureNetwork.CanToggleLock;
            if (lockButton != null && lockVisible != lastLockVisible) { lockButton.Descriptor.SetActive(lockVisible); lastLockVisible = lockVisible; }
            string lockTitle = BasisCaptureNetwork.ServerLocked ? BasisLocalization.Get("settings.developer.capture.unlock") : BasisLocalization.Get("settings.developer.capture.lock");
            if (lockButton != null && lockTitle != lastLock) { lockButton.Descriptor.SetTitle(lockTitle); lastLock = lockTitle; }
            bool recorderVisible = BasisCaptureNetwork.MayRecord || BasisCapture.Recording;
            if (recorderVisible != lastRecorderVisible)
            {
                foreach (GameObject go in recorderOnly) if (go != null) go.SetActive(recorderVisible);
                lastRecorderVisible = recorderVisible;
                if (container != null) UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(container);
            }
        }
        private static string Format(double seconds)
        {
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
