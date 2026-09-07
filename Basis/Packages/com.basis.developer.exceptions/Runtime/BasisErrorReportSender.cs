using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System.Text.RegularExpressions;

/// <summary>
/// Sends the first sighting of an error/exception to the server on
/// EventsChannel / EventType_ErrorReport. Identity (UUID, display name, platform) is
/// NOT sent — the server attaches it from the peer's connect metadata. Only transmits
/// while the server has reporting enabled (BasisNetworkModeration.CrashReportingEnabled,
/// pushed by the server). Dedup is the caller's job (BasisExceptionNotifier.Seen).
/// </summary>
public static class BasisErrorReportSender
{
    private const int MaxMessageChars = 2000;
    private const int MaxStackChars = 12000;

    // A live report is one fire-and-forget reliable packet, so there's no real progress to track —
    // this briefly surfaces that a report left the device and clears itself after
    // UploadIndicatorSeconds. Keyed and shared, so a burst of distinct errors keeps one indicator up instead
    // of stacking many.
    private const string UploadIndicatorKey = "ErrorReportUpload";
    private const string UploadIndicatorLabel = "Uploading error report";
    private const float UploadIndicatorPercent = 80f;
    private const float UploadIndicatorSeconds = 1.5f;

    public static void Report(byte severity, string system, string message, string stackTrace)
    {
        if (!BasisNetworkModeration.CrashReportingEnabled) return;
        BasisDeviceManagement.EnqueueOnMainThread(() => Send(severity, system, message, stackTrace, showUploadIndicator: true));
    }

    /// <summary>
    /// Re-send a report captured in a previous (crashed) session. Marked with severity 2
    /// so the server records it as a "crash" rather than a live error/exception. Same
    /// gating as <see cref="Report"/> — only transmits while connected and reporting is on.
    /// Does not persist (the report is already on disk via <see cref="BasisCrashReportStore"/>).
    /// </summary>
    public static void SendPrevious(string system, string message, string stackTrace)
    {
        if (!BasisNetworkModeration.CrashReportingEnabled) return;
        // No per-report indicator here: the carried-over batch is acknowledged once as a whole by
        // BasisCrashReportStore.TryReplay rather than flashing once per replayed report.
        BasisDeviceManagement.EnqueueOnMainThread(() => Send(2, system, message, stackTrace, showUploadIndicator: false));
    }

    private static void Send(byte severity, string system, string message, string stackTrace, bool showUploadIndicator)
    {
        try
        {
            if (!BasisNetworkModeration.CrashReportingEnabled) return;
            if (BasisNetworkConnection.LocalPlayerPeer == null) return;

            // Redact IPs, machine name, and user/profile paths before anything leaves the device.
            byte[] blob = PermissionCompression.CompressExtras(new[]
            {
                Redact(system) ?? string.Empty,
                Redact(Truncate(message, MaxMessageChars)),
                Redact(Truncate(stackTrace, MaxStackChars)),
            });

            NetDataWriter writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.EventType_ErrorReport);
            writer.Put(severity);
            writer.PutBytesWithLength(blob);
            BasisNetworkConnection.LocalPlayerPeer.Send(
                writer,
                BasisNetworkCommons.EventsChannel,
                DeliveryMethod.ReliableOrdered);

            // Briefly surface that a report left the device (see UploadIndicator* notes above).
            if (showUploadIndicator)
            {
                BasisUILoadingBar.ProgressReportTransient(UploadIndicatorKey, UploadIndicatorPercent, UploadIndicatorLabel, UploadIndicatorSeconds);
            }

            // A successful live send means we're connected and reporting is enabled, so this
            // is also the right moment to flush any crash reports held over from a previous
            // session. Guarded internally so it only happens once.
            BasisCrashReportStore.TryReplay();
        }
        catch
        {
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max);
    }

    /// <summary>
    /// Strip sensitive local details from a report field before it is sent: IPv4/IPv6
    /// addresses, the machine name, the OS user name, and the user-profile path segment
    /// that Unity bakes into stack-trace file paths (e.g. C:\Users\Name\...).
    /// </summary>
    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            value = Regex.Replace(value, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[ip]");
            value = Regex.Replace(value, @"\b(?:[A-Fa-f0-9]{1,4}:){2,7}[A-Fa-f0-9]{1,4}\b", "[ip]");
            value = Regex.Replace(value, @"([Uu]sers[\\/])[^\\/\r\n]+", "$1[user]");
            value = Regex.Replace(value, @"(/home/)[^/\r\n]+", "$1[user]");
            value = RedactToken(value, SafeName(() => UnityEngine.SystemInfo.deviceName), "[machine]");
            value = RedactToken(value, SafeName(() => System.Environment.MachineName), "[machine]");
            value = RedactToken(value, SafeName(() => System.Environment.UserName), "[user]");
        }
        catch
        {
        }
        return value;
    }

    private static string RedactToken(string value, string token, string replacement)
    {
        // Skip very short tokens to avoid mangling unrelated text.
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token) || token.Length < 3) return value;
        try { return Regex.Replace(value, Regex.Escape(token), replacement, RegexOptions.IgnoreCase); }
        catch { return value; }
    }

    private static string SafeName(System.Func<string> getter)
    {
        try { return getter(); } catch { return null; }
    }
}
