using System;
using System.IO;
using System.Threading.Tasks;
using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.UI.UI_Panels;
using K4os.Compression.LZ4;
using UnityEngine;

/// <summary>
/// Receives the chunked log bundle an admin requested with
/// <see cref="BasisNetworkModeration.RequestAllLogs"/>, reassembles it, LZ4-decompresses it,
/// and expands the contained files into a dated folder next to the local settings:
/// <c>Application.persistentDataPath/PulledServerLogs/&lt;ServerName-safe&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;/</c>.
///
/// Driven from <see cref="BasisNetworkModeration.AdminMessage"/> (main thread) via the
/// LogBundleBegin / LogBundleChunk / LogBundleEnd admin messages. The wire format and
/// container layout are documented on the server's BasisServerLogBundleService.
/// </summary>
public static class BasisLogBundleReceiver
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private const int MaxChunks = 4_000_000;

    // A log bundle is only ever the answer to RequestAllLogs. Without this the server can start an
    // unsolicited transfer: a 256 MB allocation, a write into persistentDataPath and a popup, none
    // of which the local player asked for.
    private const double ArmedWindowSeconds = 300d;
    private static bool _armed;
    private static DateTime _armedUtc;

    private static bool _active;
    private static string _serverNameSafe;
    private static bool _isCompressed;
    private static int _payloadBytes;
    private static int _rawBytes;
    private static int _totalChunks;
    private static int _received;
    private static byte[] _buffer;
    private static int _offset;

    // Loading-bar progress (admin "pull server logs" is a chunked download with real elapsed time).
    private const string ProgressKey = "ServerLogDownload";
    private const string DownloadLabel = "Downloading server logs";
    private const string ExtractLabel = "Extracting server logs";
    private static int _lastReportedPercent;

    // Chunks map to 0–90% so the bar stays open across the End()/extraction handoff; extraction
    // reports 95% and completion reports 100% (which removes the entry). Throttled to whole-percent
    // changes so a many-chunk transfer doesn't flood the main-thread dispatch queue.
    private static void ReportReceiveProgress()
    {
        float pct = _totalChunks > 0 ? (_received / (float)_totalChunks) * 90f : 2f;
        if (pct < 2f) pct = 2f;
        int rounded = Mathf.RoundToInt(pct);
        if (rounded == _lastReportedPercent) return;
        _lastReportedPercent = rounded;
        BasisUILoadingBar.ProgressReport(ProgressKey, pct, DownloadLabel);
    }

    // Reporting 100 removes the entry (and closes the bar if nothing else is loading).
    private static void ClearProgress() => BasisUILoadingBar.ProgressReport(ProgressKey, 100f, DownloadLabel);

    public static void ArmForLocalRequest()
    {
        _armed = true;
        _armedUtc = DateTime.UtcNow;
    }

    public static void Begin(NetDataReader reader)
    {
        if (!_armed || (DateTime.UtcNow - _armedUtc).TotalSeconds > ArmedWindowSeconds)
        {
            _armed = false;
            BasisDebug.LogError("Ignoring a log bundle that was never requested from this client.");
            Reset();
            return;
        }
        _armed = false;
        try
        {
            string serverNameSafe = reader.GetString();
            string fileName = reader.GetString(); // reserved (currently always "logs")
            bool isCompressed = reader.GetBool();
            int payloadBytes = reader.GetInt();
            int rawBytes = reader.GetInt();
            int totalChunks = reader.GetInt();
            _ = fileName;

            if (payloadBytes < 0 || payloadBytes > MaxBytes ||
                rawBytes < 0 || rawBytes > MaxBytes ||
                totalChunks < 0 || totalChunks > MaxChunks)
            {
                BasisDebug.LogError($"Rejected log bundle: implausible header (payload={payloadBytes}, raw={rawBytes}, chunks={totalChunks}).");
                Reset();
                return;
            }

            _serverNameSafe = Sanitize(serverNameSafe);
            _isCompressed = isCompressed;
            _payloadBytes = payloadBytes;
            _rawBytes = rawBytes;
            _totalChunks = totalChunks;
            _buffer = new byte[payloadBytes];
            _offset = 0;
            _received = 0;
            _active = true;
            BasisDebug.Log($"Receiving server log bundle: {payloadBytes / 1024} KB in {totalChunks} chunk(s).", BasisDebug.LogTag.Networking);
            _lastReportedPercent = -1;
            ReportReceiveProgress();
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Log bundle begin failed: {e.Message}");
            Reset();
        }
    }

    public static void Chunk(NetDataReader reader)
    {
        try
        {
            int index = reader.GetInt();
            byte[] data = reader.GetBytesWithLength();
            _ = index;
            if (!_active || _buffer == null || data == null) return;
            if (_offset + data.Length > _buffer.Length)
            {
                BasisDebug.LogError("Log bundle chunk overran the expected size; aborting.");
                Reset();
                return;
            }
            Buffer.BlockCopy(data, 0, _buffer, _offset, data.Length);
            _offset += data.Length;
            _received++;
            ReportReceiveProgress();
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Log bundle chunk failed: {e.Message}");
            Reset();
        }
    }

    public static void End(NetDataReader reader)
    {
        bool ok;
        string message;
        try
        {
            ok = reader.GetBool();
            message = reader.GetString();
        }
        catch
        {
            ok = false;
            message = string.Empty;
        }

        if (!_active)
        {
            // A failure can arrive with no preceding Begin (e.g. permission denied server-side
            // sends a plain message instead). Surface anything useful, otherwise ignore.
            if (!ok && !string.IsNullOrEmpty(message)) BasisNetworkModeration.DisplayMessage(message);
            return;
        }

        if (!ok)
        {
            BasisDebug.LogError($"Server reported log bundle failure: {message}");
            ClearProgress();
            BasisNetworkModeration.DisplayMessage(string.IsNullOrEmpty(message) ? "Server failed to send logs." : message);
            Reset();
            return;
        }

        if (_offset != _payloadBytes || _received != _totalChunks)
        {
            BasisDebug.LogError($"Log bundle incomplete ({_offset}/{_payloadBytes} bytes, {_received}/{_totalChunks} chunks).");
            ClearProgress();
            BasisNetworkModeration.DisplayMessage("Log transfer was incomplete; please try again.");
            Reset();
            return;
        }

        byte[] payload = _buffer;
        int payloadLen = _payloadBytes;
        int rawLen = _rawBytes;
        bool compressed = _isCompressed;
        string serverNameSafe = _serverNameSafe;

        // persistentDataPath must be read on the main thread; we are on it (AdminMessage runs main-thread).
        string root = Path.Combine(Application.persistentDataPath, "PulledServerLogs");
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string destDir = Path.Combine(root, $"{serverNameSafe}_{stamp}");
        Reset();

        BasisUILoadingBar.ProgressReport(ProgressKey, 95f, ExtractLabel);
        _ = Task.Run(() => ExpandAndNotify(payload, payloadLen, rawLen, compressed, destDir));
    }

    private static void ExpandAndNotify(byte[] payload, int payloadLen, int rawLen, bool compressed, string destDir)
    {
        try
        {
            byte[] raw;
            if (compressed)
            {
                raw = new byte[rawLen];
                int decoded = LZ4Codec.Decode(payload, 0, payloadLen, raw, 0, rawLen);
                if (decoded != rawLen)
                {
                    throw new InvalidDataException($"LZ4 decode produced {decoded} bytes, expected {rawLen}.");
                }
            }
            else
            {
                raw = payload;
                rawLen = payloadLen;
            }

            int fileCount = ExtractContainer(raw, rawLen, destDir);

            BasisDebug.Log($"Saved {fileCount} server log file(s) to {destDir}", BasisDebug.LogTag.Networking);
            ClearProgress();
            BasisDeviceManagement.EnqueueOnMainThread(() =>
                BasisNetworkModeration.DisplayMessageWithFolder($"Server logs saved to:\n{destDir}", destDir));
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to save server logs: {e.Message}");
            ClearProgress();
            BasisDeviceManagement.EnqueueOnMainThread(() =>
                BasisNetworkModeration.DisplayMessage($"Failed to save server logs: {e.Message}"));
        }
    }

    private static int ExtractContainer(byte[] raw, int rawLen, string destDir)
    {
        Directory.CreateDirectory(destDir);
        int written = 0;
        using MemoryStream memory = new MemoryStream(raw, 0, rawLen, writable: false);
        using BinaryReader binaryReader = new BinaryReader(memory, System.Text.Encoding.UTF8, leaveOpen: true);

        int count = binaryReader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string relative = binaryReader.ReadString();
            int length = binaryReader.ReadInt32();
            if (length < 0 || length > memory.Length - memory.Position)
            {
                throw new InvalidDataException("Log container declared a file length past the end of the buffer.");
            }
            byte[] data = binaryReader.ReadBytes(length);

            string outPath = SafeCombine(destDir, relative);
            if (outPath == null)
            {
                BasisDebug.LogWarning($"Skipped log entry with unsafe path: {relative}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, data);
            written++;
        }
        return written;
    }

    // Prevent path traversal: ensure the resolved path stays under destDir.
    private static string SafeCombine(string destDir, string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath)) return null;
        string relative = SanitizePathSegments(entryPath);
        if (string.IsNullOrEmpty(relative)) return null;
        string combined = Path.GetFullPath(Path.Combine(destDir, relative));
        string root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return combined;
    }

    // Replace OS-invalid characters in each path segment so entries the server stored under another
    // OS still write locally (e.g. crash reports named "did:key:..." are illegal paths on Windows).
    private static string SanitizePathSegments(string entryPath)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string[] parts = entryPath.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || parts[i] == "." || parts[i] == "..") continue;
            foreach (char c in invalid)
                parts[i] = parts[i].Replace(c, '_');
        }
        return string.Join("/", parts);
    }

    private static void Reset()
    {
        _active = false;
        _buffer = null;
        _offset = 0;
        _received = 0;
        _payloadBytes = 0;
        _rawBytes = 0;
        _totalChunks = 0;
        _isCompressed = false;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "server";
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        value = value.Trim().Replace(' ', '_');
        return string.IsNullOrEmpty(value) ? "server" : value;
    }
}
