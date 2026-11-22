using Unity.Scripting.LifecycleManagement;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
[AutoStaticsCleanup]
public static partial class BasisBeeKeyGrant
{
    public delegate Task<string> KeyResolver(BasisLoadableBundle bundle, CancellationToken cancellationToken);
    public static KeyResolver Resolver;
    private const int MaxCachedGrants = 64;
    private static readonly ConcurrentDictionary<string, Task<(byte[] Grant, string Error)>> grants = new ConcurrentDictionary<string, Task<(byte[] Grant, string Error)>>();
    public static async Task EnsureResolvedAsync(BasisLoadableBundle bundle, CancellationToken cancellationToken)
    {
        string request = bundle?.UnlockPassword;
        KeyResolver resolver = Resolver;
        if (resolver == null || !BasisEncryptionWrapper.IsKeyRequest(request)) return;
        string token = await resolver(bundle, cancellationToken);
        if (token != null && bundle.UnlockPassword == request) bundle.UnlockPassword = token;
    }
    public static void Clear() => grants.Clear();
    public static Task<(byte[] Grant, string Error)> BuildAsync(string location, string password, string versionTag)
    {
        string key = string.Concat(location, "\n", versionTag, "\n", password);
        if (grants.TryGetValue(key, out Task<(byte[] Grant, string Error)> existing) && !(existing.IsCompleted && existing.Result.Grant == null)) return existing;
        if (grants.Count >= MaxCachedGrants) grants.Clear();
        Task<(byte[] Grant, string Error)> built = BuildCoreAsync(location, password);
        grants[key] = built;
        return built;
    }
    private static async Task<(byte[] Grant, string Error)> BuildCoreAsync(string location, string password)
    {
        try
        {
            if (BasisEncryptionWrapper.IsKeyGrant(password))
            {
                byte[] held = BasisEncryptionWrapper.GrantBytes(password);
                return held != null ? (held, null) : (null, "The keys this content was loaded with are damaged.");
            }
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(password) || BasisEncryptionWrapper.IsKeyRequest(password)) return (null, "This content has no password of its own to share.");
            BasisHostedBeeLayout layout = null;
            if (BasisIOManagement.TryResolveLocalBeePath(location, out string localPath))
            {
                (BasisHostedBeeLayout built, string layoutError) = await BasisHostedBeeLayout.BuildAsync(localPath, password);
                if (built == null) return (null, layoutError ?? "The content file could not be read.");
                layout = built;
            }
            async Task<byte[]> Read(long start, int length)
            {
                (byte[] Data, string Error) result = layout != null ? await layout.ReadAsync(start, length) : await BasisIOManagement.DownloadRangeAsync(location, start, length, CancellationToken.None);
                return result.Data != null && result.Data.Length == length ? result.Data : null;
            }
            byte[] connectorBytes = layout == null ? await ReadCachedConnectorAsync(location) : null;
            BasisBundleConnector connector = null;
            List<Task<byte[]>> reads = null;
            if (connectorBytes != null)
            {
                Task<byte[]> remoteStart = Read(0, BasisBeeConstants.RemoteHeaderSize + BasisEncryptionWrapper.GrantSaltBytes);
                connector = await BasisEncryptionToData.GenerateMetaFromBytes(password, connectorBytes, new BasisProgressReport());
                reads = StartSaltReads(connector, connectorBytes.Length, Read);
                if (reads == null || !MatchesRemote(await remoteStart, connectorBytes))
                {
                    connectorBytes = null;
                    connector = null;
                    reads = null;
                }
            }
            if (connectorBytes == null)
            {
                byte[] header = await Read(0, BasisBeeConstants.RemoteHeaderSize);
                if (header == null)
                {
                    return (null, "The content header could not be read.");
                }
                long connectorLength = BinaryPrimitives.ReadInt64LittleEndian(header);
                if (connectorLength <= BasisEncryptionWrapper.GrantSaltBytes || connectorLength > BasisBeeConstants.MaxConnectorBytes)
                {
                    return (null, "The content header is invalid.");
                }
                connectorBytes = await Read(BasisBeeConstants.RemoteHeaderSize, (int)connectorLength);
                if (connectorBytes == null)
                {
                    return (null, "The content description could not be read.");
                }
                connector = await BasisEncryptionToData.GenerateMetaFromBytes(password, connectorBytes, new BasisProgressReport());
                if (connector?.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
                {
                    return (null, "The content description could not be unlocked.");
                }
                reads = StartSaltReads(connector, connectorBytes.Length, Read);
                if (reads == null)
                {
                    return (null, "The content description is damaged.");
                }
            }
            byte[][] read = await Task.WhenAll(reads);
            List<byte[]> salts = new List<byte[]>(read.Length + 1) { Slice(connectorBytes, 0) };
            foreach (byte[] salt in read)
            {
                if (salt == null)
                {
                    return (null, "The content file is shorter than its description.");
                }
                salts.Add(salt);
            }
            return (await BasisTasks.Run(() => Derive(password, salts)), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
    private static byte[] Derive(string password, List<byte[]> salts)
    {
        byte[] grant = new byte[2 + salts.Count * BasisEncryptionWrapper.GrantEntryBytes];
        grant[0] = (byte)salts.Count;
        grant[1] = (byte)(salts.Count >> 8);
        BasisTasks.For(0, salts.Count, index =>
        {
            int offset = 2 + index * BasisEncryptionWrapper.GrantEntryBytes;
            byte[] key = BasisEncryptionWrapper.DeriveSectionKey(password, salts[index]);
            Buffer.BlockCopy(salts[index], 0, grant, offset, BasisEncryptionWrapper.GrantSaltBytes);
            Buffer.BlockCopy(key, 0, grant, offset + BasisEncryptionWrapper.GrantSaltBytes, BasisEncryptionWrapper.GrantKeyBytes);
            Array.Clear(key, 0, key.Length);
        });
        return grant;
    }
    private static List<Task<byte[]>> StartSaltReads(BasisBundleConnector connector, long connectorLength, Func<long, int, Task<byte[]>> read)
    {
        if (connector?.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
        {
            return null;
        }
        List<long> offsets = new List<long>();
        long start = BasisBeeConstants.RemoteHeaderSize + connectorLength;
        foreach (BasisBundleGenerated section in connector.BasisBundleGenerated)
        {
            if (section == null || section.EndByte <= 0)
            {
                return null;
            }
            offsets.Add(start);
            if (section.GraphicsStatePayloads != null)
            {
                foreach (BasisGraphicsStatePayload payload in section.GraphicsStatePayloads)
                {
                    if (payload != null && payload.Length > 0 && payload.Offset > 0 && payload.Offset < section.EndByte)
                    {
                        offsets.Add(start + payload.Offset);
                    }
                }
            }
            start += section.EndByte;
        }
        int count = Math.Min(offsets.Count, BasisEncryptionWrapper.MaxGrantEntries - 1);
        List<Task<byte[]>> reads = new List<Task<byte[]>>(count);
        for (int index = 0; index < count; index++)
        {
            reads.Add(read(offsets[index], BasisEncryptionWrapper.GrantSaltBytes));
        }
        return reads;
    }
    private static bool MatchesRemote(byte[] remoteStart, byte[] connectorBytes)
    {
        if (remoteStart == null || BinaryPrimitives.ReadInt64LittleEndian(remoteStart) != connectorBytes.Length)
        {
            return false;
        }
        for (int index = 0; index < BasisEncryptionWrapper.GrantSaltBytes; index++)
        {
            if (remoteStart[BasisBeeConstants.RemoteHeaderSize + index] != connectorBytes[index])
            {
                return false;
            }
        }
        return true;
    }
    private static async Task<byte[]> ReadCachedConnectorAsync(string location)
    {
        try
        {
            if (BasisServerHostedBee.IsHostedUrl(location) || !BasisLoadHandler.IsMetaDataOnDisc(location, out BasisBEEExtensionMeta meta) || !BasisLoadHandler.TryResolveRecordedCachePath(meta?.StoredLocal?.DownloadedBeeFileLocation, out string path))
            {
                return null;
            }
            return await BasisTasks.Run(() => ReadDiskConnector(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
    private static byte[] ReadDiskConnector(string path)
    {
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = new byte[BasisBeeConstants.DiskHeaderSize];
                if (!ReadFully(stream, header))
                {
                    return null;
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= BasisEncryptionWrapper.GrantSaltBytes || length > BasisBeeConstants.MaxConnectorBytes || length > stream.Length - stream.Position)
                {
                    return null;
                }
                byte[] connectorBytes = new byte[length];
                return ReadFully(stream, connectorBytes) ? connectorBytes : null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
    private static bool ReadFully(Stream stream, byte[] buffer)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int count = stream.Read(buffer, filled, buffer.Length - filled);
            if (count <= 0)
            {
                return false;
            }
            filled += count;
        }
        return true;
    }
    private static byte[] Slice(byte[] source, int offset)
    {
        byte[] result = new byte[BasisEncryptionWrapper.GrantSaltBytes];
        Buffer.BlockCopy(source, offset, result, 0, result.Length);
        return result;
    }
}
