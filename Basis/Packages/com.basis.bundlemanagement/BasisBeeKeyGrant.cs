using Unity.Scripting.LifecycleManagement;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            byte[] header = await Read(0, BasisBeeConstants.RemoteHeaderSize);
            if (header == null) return (null, "The content header could not be read.");
            long connectorLength = BinaryPrimitives.ReadInt64LittleEndian(header);
            if (connectorLength <= BasisEncryptionWrapper.GrantSaltBytes || connectorLength > BasisBeeConstants.MaxConnectorBytes) return (null, "The content header is invalid.");
            byte[] connectorBytes = await Read(BasisBeeConstants.RemoteHeaderSize, (int)connectorLength);
            if (connectorBytes == null) return (null, "The content description could not be read.");
            BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(password, connectorBytes, new BasisProgressReport());
            if (connector?.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0) return (null, "The content description could not be unlocked.");
            List<byte[]> salts = new List<byte[]> { Slice(connectorBytes, 0) };
            long start = BasisBeeConstants.RemoteHeaderSize + connectorLength;
            foreach (BasisBundleGenerated section in connector.BasisBundleGenerated)
            {
                if (section == null || section.EndByte <= 0) return (null, "The content description is damaged.");
                if (!await AddSalt(salts, Read, start)) return (null, "The content file is shorter than its description.");
                if (section.GraphicsStatePayloads != null)
                {
                    foreach (BasisGraphicsStatePayload payload in section.GraphicsStatePayloads)
                    {
                        if (payload != null && payload.Length > 0 && payload.Offset > 0 && payload.Offset < section.EndByte && !await AddSalt(salts, Read, start + payload.Offset)) return (null, "The content file is shorter than its description.");
                    }
                }
                start += section.EndByte;
            }
            if (salts.Count > BasisEncryptionWrapper.MaxGrantEntries) salts.RemoveRange(BasisEncryptionWrapper.MaxGrantEntries, salts.Count - BasisEncryptionWrapper.MaxGrantEntries);
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
        for (int index = 0; index < salts.Count; index++)
        {
            int offset = 2 + index * BasisEncryptionWrapper.GrantEntryBytes;
            byte[] key = BasisEncryptionWrapper.DeriveSectionKey(password, salts[index]);
            Buffer.BlockCopy(salts[index], 0, grant, offset, BasisEncryptionWrapper.GrantSaltBytes);
            Buffer.BlockCopy(key, 0, grant, offset + BasisEncryptionWrapper.GrantSaltBytes, BasisEncryptionWrapper.GrantKeyBytes);
            Array.Clear(key, 0, key.Length);
        }
        return grant;
    }
    private static async Task<bool> AddSalt(List<byte[]> salts, Func<long, int, Task<byte[]>> read, long start)
    {
        byte[] salt = await read(start, BasisEncryptionWrapper.GrantSaltBytes);
        if (salt != null) salts.Add(salt);
        return salt != null;
    }
    private static byte[] Slice(byte[] source, int offset)
    {
        byte[] result = new byte[BasisEncryptionWrapper.GrantSaltBytes];
        Buffer.BlockCopy(source, offset, result, 0, result.Length);
        return result;
    }
}
