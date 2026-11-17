using System;
using System.Globalization;
namespace Basis.Capture
{
    public static class BasisCapture
    {
        public static bool Recording => BasisCaptureSession.Current != null && BasisCaptureSession.Current.State == BasisCaptureSession.SessionState.Recording && !BasisCaptureSession.Current.options.peerMode;
        public static BasisCaptureSession Start()
        {
            if (Recording) return BasisCaptureSession.Current;
            if (!BasisCaptureNetwork.MayRecord)
            {
                BasisCaptureNetwork.LastDenied = !BasisCaptureNetwork.HaveLockState ? "This server does not allow scene capture." : !BasisCaptureNetwork.LocalMayCollect ? "Only server admins can record on this server." : "Scene capture is locked on this server. An admin can unlock it.";
                BasisDebug.LogWarning(BasisCaptureNetwork.LastDenied);
                return null;
            }
            BasisCaptureNetwork.LastDenied = null;
            float minutes = float.TryParse(BasisCaptureSettings.MaxMinutes.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) && m > 0f ? m : 0f;
            BasisCaptureSession.Options o = new BasisCaptureSession.Options
            {
                remotes = BasisCaptureSettings.IncludeRemotes.RawValue,
                scene = BasisCaptureSettings.IncludeScene.RawValue,
                geometry = BasisCaptureSettings.ExportGeometry.RawValue,
                microphone = BasisCaptureSettings.Microphone.RawValue,
                sessionId = (uint)new Random().Next(1, int.MaxValue),
                maxSeconds = minutes * 60f,
                rateHz = RateFromSetting(),
            };
            bool consent = o.remotes && BasisCaptureNetwork.NeedsConsent;
            if (consent)
            {
                BasisCaptureNetwork.ResetConsent();
                o.includeRemote = BasisCaptureNetwork.HasConsent;
            }
            BasisCaptureSession s = BasisCaptureSession.Start(o);
            if (s == null) return null;
            if (consent) BasisCaptureNetwork.BeginCollection(s);
            return s;
        }
        public static readonly string[] Rates = { "30", "60", "90", "120", "144", "every" };
        public static float RateFromSetting() => float.TryParse(BasisCaptureSettings.RateHz.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float hz) && hz > 0f ? hz : 0f;
        public static void Stop()
        {
            BasisCaptureSession s = BasisCaptureSession.Current;
            if (s == null || s.options.peerMode) return;
            s.Stop();
            if (BasisCaptureNetwork.Collecting && BasisCaptureNetwork.CollectSession == s.options.sessionId) BasisCaptureNetwork.EndCollection();
        }
        public static void Toggle()
        {
            if (Recording) Stop(); else Start();
        }
    }
}
