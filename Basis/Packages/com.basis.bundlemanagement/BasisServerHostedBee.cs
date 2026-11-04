using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
[AutoStaticsCleanup]
public static partial class BasisServerHostedBee
{
    public const string Scheme = "basis-host";
    public const int IdHexLength = 32;
    public delegate Task<(byte[] Data, string Error)> RangeFetcher(string id, long start, int length, Action<float> progress, CancellationToken cancellationToken);
    public static RangeFetcher Fetcher;
    public static string BuildUrl(string id) => Scheme + "://bee/" + id;
    public static bool TryParse(string url, out string id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(uri.Host, "bee", StringComparison.OrdinalIgnoreCase)) return false;
        string candidate = uri.AbsolutePath.Trim('/');
        if (candidate.Length != IdHexLength) return false;
        for (int index = 0; index < candidate.Length; index++)
        {
            char c = candidate[index];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        id = candidate.ToLowerInvariant();
        return true;
    }
    public static bool IsHostedUrl(string url) => TryParse(url, out _);
}
public sealed class BasisHostedBeeLayout
{
    private struct Segment
    {
        public long VirtualStart, Length, FileOffset;
        public byte[] Synthetic;
    }
    private readonly List<Segment> segments = new List<Segment>();
    private long fileLength;
    private DateTime lastWriteUtc;
    public string FilePath { get; private set; }
    public string Id { get; private set; }
    public long VirtualLength { get; private set; }
    public int SharedSectionCount { get; private set; }
    public static async Task<(BasisHostedBeeLayout Layout, string Error)> BuildAsync(string filePath, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return (null, "Local bee file not found.");
        if (string.IsNullOrWhiteSpace(password)) return (null, "Unlock password is empty.");
        FileInfo info = new FileInfo(filePath);
        long length = info.Length;
        byte[] head = await ReadAtAsync(filePath, 0, (int)Math.Min(BasisBeeConstants.RemoteHeaderSize, length), cancellationToken).ConfigureAwait(false);
        if (head.Length < BasisBeeConstants.RemoteHeaderSize) return (null, "Local bee file is too small.");
        BasisHostedBeeLayout layout = new BasisHostedBeeLayout { FilePath = filePath, fileLength = length, lastWriteUtc = info.LastWriteTimeUtc };
        long remoteConnector = BitConverter.ToInt64(head, 0);
        if (remoteConnector > 0 && remoteConnector <= BasisBeeConstants.MaxConnectorBytes && BasisBeeConstants.RemoteHeaderSize + remoteConnector <= length)
        {
            byte[] connectorBytes = await ReadAtAsync(filePath, BasisBeeConstants.RemoteHeaderSize, (int)remoteConnector, cancellationToken).ConfigureAwait(false);
            BasisBundleConnector connector = await TryDecrypt(password, connectorBytes).ConfigureAwait(false);
            if (connector?.BasisBundleGenerated != null && connector.BasisBundleGenerated.Length > 0 && SumSections(connector, out long sections) && BasisBeeConstants.RemoteHeaderSize + remoteConnector + sections == length)
            {
                layout.segments.Add(new Segment { VirtualStart = 0, Length = length, FileOffset = 0 });
                layout.VirtualLength = length;
                layout.SharedSectionCount = connector.BasisBundleGenerated.Length;
                layout.Id = ComputeId(head, connectorBytes);
                return (layout, null);
            }
        }
        int cacheConnector = BitConverter.ToInt32(head, 0);
        if (cacheConnector <= 0 || cacheConnector > BasisBeeConstants.MaxConnectorBytes || BasisBeeConstants.DiskHeaderSize + (long)cacheConnector >= length) return (null, "Local bee file layout is not recognised.");
        byte[] cacheConnectorBytes = await ReadAtAsync(filePath, BasisBeeConstants.DiskHeaderSize, cacheConnector, cancellationToken).ConfigureAwait(false);
        BasisBundleConnector cacheConnectorData = await TryDecrypt(password, cacheConnectorBytes).ConfigureAwait(false);
        if (cacheConnectorData?.BasisBundleGenerated == null || cacheConnectorData.BasisBundleGenerated.Length == 0 || !SumSections(cacheConnectorData, out _)) return (null, "Local bee connector could not be read.");
        long sectionOnDisk = length - BasisBeeConstants.DiskHeaderSize - cacheConnector;
        int match = FindSection(cacheConnectorData, sectionOnDisk, BasisBundleConnector.IsPlatform);
        if (match < 0) match = FindSection(cacheConnectorData, sectionOnDisk, BasisBundleConnector.IsGenericBundle);
        if (match < 0) return (null, "Local bee section does not match its connector.");
        byte[] header = BitConverter.GetBytes((long)cacheConnector);
        layout.segments.Add(new Segment { VirtualStart = 0, Length = header.Length, FileOffset = -1, Synthetic = header });
        layout.segments.Add(new Segment { VirtualStart = header.Length, Length = cacheConnector, FileOffset = BasisBeeConstants.DiskHeaderSize });
        long cursor = header.Length + cacheConnector;
        for (int index = 0; index < cacheConnectorData.BasisBundleGenerated.Length; index++)
        {
            long sectionLength = cacheConnectorData.BasisBundleGenerated[index].EndByte;
            layout.segments.Add(new Segment { VirtualStart = cursor, Length = sectionLength, FileOffset = index == match ? BasisBeeConstants.DiskHeaderSize + cacheConnector : -1 });
            cursor += sectionLength;
        }
        layout.VirtualLength = cursor;
        layout.SharedSectionCount = 1;
        layout.Id = ComputeId(header, cacheConnectorBytes);
        return (layout, null);
    }
    public async Task<(byte[] Data, string Error)> ReadAsync(long start, int length, CancellationToken cancellationToken = default)
    {
        if (length <= 0) return (null, "Range is outside the shared file.");
        byte[] result = new byte[length];
        string error = await ReadIntoAsync(start, result, length, cancellationToken).ConfigureAwait(false);
        return error == null ? (result, null) : (null, error);
    }
    public async Task<string> ReadIntoAsync(long start, byte[] destination, int length, CancellationToken cancellationToken = default)
    {
        if (destination == null || start < 0 || length <= 0 || length > destination.Length || start > VirtualLength - length) return "Range is outside the shared file.";
        FileInfo info = new FileInfo(FilePath);
        if (!info.Exists || info.Length != fileLength || info.LastWriteTimeUtc != lastWriteUtc) return "Local bee file changed since it was shared.";
        long end = start + length;
        FileStream stream = null;
        try
        {
            foreach (Segment segment in segments)
            {
                long segmentEnd = segment.VirtualStart + segment.Length;
                if (segmentEnd <= start || segment.VirtualStart >= end) continue;
                long from = Math.Max(start, segment.VirtualStart), to = Math.Min(end, segmentEnd);
                int count = (int)(to - from), offset = (int)(from - start);
                if (segment.Synthetic != null)
                {
                    Buffer.BlockCopy(segment.Synthetic, (int)(from - segment.VirtualStart), destination, offset, count);
                    continue;
                }
                if (segment.FileOffset < 0) return "That part of the avatar is not in the local file.";
                stream ??= new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: BasisTasks.ThreadsAvailable);
                stream.Seek(segment.FileOffset + (from - segment.VirtualStart), SeekOrigin.Begin);
                int total = 0;
                while (total < count)
                {
                    int read = await stream.ReadAsync(destination, offset + total, count - total, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) return "Local bee file ended early.";
                    total += read;
                }
            }
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }
    private static async Task<BasisBundleConnector> TryDecrypt(string password, byte[] connectorBytes)
    {
        try { return await BasisEncryptionToData.GenerateMetaFromBytes(password, connectorBytes, null).ConfigureAwait(false); }
        catch { return null; }
    }
    private static bool SumSections(BasisBundleConnector connector, out long total)
    {
        total = 0;
        foreach (BasisBundleGenerated entry in connector.BasisBundleGenerated)
        {
            if (entry == null || entry.EndByte <= 0 || entry.EndByte > BasisBeeConstants.MaxSectionBytes) return false;
            total += entry.EndByte;
        }
        return true;
    }
    private static int FindSection(BasisBundleConnector connector, long length, Func<BasisBundleGenerated, bool> predicate)
    {
        for (int index = 0; index < connector.BasisBundleGenerated.Length; index++)
        {
            BasisBundleGenerated entry = connector.BasisBundleGenerated[index];
            if (entry.EndByte == length && predicate(entry)) return index;
        }
        return -1;
    }
    private static string ComputeId(byte[] header, byte[] connectorBytes)
    {
        using SHA256 sha = SHA256.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformFinalBlock(connectorBytes, 0, connectorBytes.Length);
        byte[] hash = sha.Hash;
        const string digits = "0123456789abcdef";
        char[] chars = new char[BasisServerHostedBee.IdHexLength];
        for (int index = 0; index < chars.Length / 2; index++)
        {
            chars[index * 2] = digits[hash[index] >> 4];
            chars[index * 2 + 1] = digits[hash[index] & 15];
        }
        return new string(chars);
    }
    private static async Task<byte[]> ReadAtAsync(string path, long offset, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: BasisTasks.ThreadsAvailable);
        stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer, total, count - total, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
        }
        if (total == count) return buffer;
        byte[] partial = new byte[total];
        Buffer.BlockCopy(buffer, 0, partial, 0, total);
        return partial;
    }
}
