using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.EventDriver;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Platform;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisMediaClipShare
{
    private const string FixedNetworkIdentifier = "BasisMediaClipShare";
    private const string ClipIdentifierPrefix = "BasisMediaClip/";
    private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Video;
    public static int MaxClipsPerSender => Math.Clamp(BasisNetworkModeration.GlobalVideoClipMaxPerPlayer, BasisNetworkModeration.MinVideoClipMaxPerPlayer, BasisNetworkModeration.MaxVideoClipMaxPerPlayer);
    public static long MaxInboundBytes => Math.Max(384L * 1024L * 1024L, Math.Max(2, MaxClipsPerSender) * (long)BasisMediaClipProtocol.MaxClipBytes);
    public const double RelayBytesPerSecond = 4.0 * 1024.0 * 1024.0;
    public const double DirectBytesPerSecond = 12.0 * 1024.0 * 1024.0;
    public const double DirectLanBytesPerSecond = 40.0 * 1024.0 * 1024.0;
    private const double BurstSeconds = 0.25;
    private const int MaxChunksPerFrame = 256;
    private const int MaxQueuedSendPackets = 768;
    private const float StallSeconds = 4f;
    private const float PoseIntervalSeconds = 0.1f;
    private const float SpawnDistance = 1.3f;
    private const float NetworkUrlTimeoutSeconds = 15f;
    private const float PanelRefreshSeconds = 0.5f;

    private sealed class Clip
    {
        public Guid Id;
        public ushort SharerId;
        public string SharerName;
        public bool SharerProtected;
        public bool SharerBypassesLock;
        public bool IsLocalSharer;
        public BasisMediaClipContainer Container;
        public string Name;
        public int TotalBytes;
        public int TotalChunks;
        public BasisMediaClipFile Storage;
        public bool[] Received;
        public int ReceivedCount;
        public bool Complete;
        public bool Writing;
        public bool Failed;
        public string FilePath;
        public bool OwnsFile;
        public float LastProgressTime;
        public float LastNeedTime;
        public int LastShownPercent = -1;
        public bool UrlAnnounced;
        public float CreatedTime;
        public float PoseTimer;
        public BasisMediaClipObject View;
        public byte ServerState;
        public bool FromServer;
        public int NeedRounds;
        public bool[] Relayed;
        public bool Deferred;
        public bool Pulled;
        public int PullTarget;
        public bool Adopted;
        public string HandoverNote;
    }

    private sealed class Job
    {
        public Clip Clip;
        public ushort[] Recipients;
        public int Next;
        public Queue<int> Explicit;
        public bool Direct;
        public double Budget;
        public bool ServerUpload;
    }

    private static readonly Dictionary<Guid, Clip> clips = new Dictionary<Guid, Clip>();
    private static readonly List<Job> jobs = new List<Job>();
    private static readonly List<int> scratchIndices = new List<int>();
    private static readonly List<Guid> scratchIds = new List<Guid>();
    private static readonly List<ushort> scratchRecipients = new List<ushort>();
    private static readonly HashSet<Guid> dismissed = new HashSet<Guid>();
    private const float OrphanGraceSeconds = 30f;
    private static readonly Dictionary<Guid, (ushort Leaver, float Deadline)> orphaned = new Dictionary<Guid, (ushort, float)>();
    private static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".mp3", ".m4a", ".flac", ".wav", ".opus", ".aac" };
    private static bool initialized;
    private static bool hasNetworkId;
    private static ushort networkId;
    private static int connectionGeneration;
    private static int resolveGeneration = -1;
    private static double relayBudget;
    private static int jobCursor;
    private static bool panelsVisible;
    private static float panelRefreshTimer;
    private static string cacheDirectory;
    private static long inboundBytes;
    private static byte[] chunkScratch;
    private static NetDataWriter directWriter;

    public static string NetworkIdentifierFor(Guid id) => ClipIdentifierPrefix + id.ToString("N");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        string cacheRoot = Path.Combine(Application.temporaryCachePath, "BasisMediaClips");
        int processId;
        try
        {
            processId = System.Diagnostics.Process.GetCurrentProcess().Id;
        }
        catch (Exception)
        {
            processId = 0;
        }
        cacheDirectory = Path.Combine(cacheRoot, processId.ToString());
        ClearStaleCaches(cacheRoot, processId);

        BasisDesktopFileDrop.RegisterAcceptedExtensions(Extensions);
        BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
        BasisDesktopFileDrop.OnFilesDropped += OnFilesDropped;
        BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerJoined += HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
        BasisNetworkPlayer.OnPlayerLeft += HandlePlayerLeft;
        BasisEventDriver.OnUpdate -= Tick;
        BasisEventDriver.OnUpdate += Tick;
        Application.quitting -= Shutdown;
        Application.quitting += Shutdown;
        BasisNetworkModeration.OnGlobalVideoClipsLockedChanged -= HandleVideoClipsLockChanged;
        BasisNetworkModeration.OnGlobalVideoClipsLockedChanged += HandleVideoClipsLockChanged;
        BasisMediaPersistence.Enabled.OnChanged -= SendPersistence;
        BasisMediaPersistence.Enabled.OnChanged += SendPersistence;
        BasisNetworkModeration.OnGlobalVideoClipMaxMegabytesChanged -= HandleClipMaxSizeChanged;
        BasisNetworkModeration.OnGlobalVideoClipMaxMegabytesChanged += HandleClipMaxSizeChanged;
        HandleClipMaxSizeChanged(BasisNetworkModeration.GlobalVideoClipMaxMegabytes);
        if (BasisNetworkConnection.LocalPlayerIsConnected) HandleLocalPlayerJoined(null, null);
    }

    private static void ClearStaleCaches(string cacheRoot, int processId)
    {
        try
        {
            if (!Directory.Exists(cacheRoot)) return;
            foreach (string directory in Directory.GetDirectories(cacheRoot))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int owner)) continue;
                if (owner != processId && IsProcessAlive(owner)) continue;
                Directory.Delete(directory, true);
            }
        }
        catch (Exception exception)
        {
            BasisDebug.LogWarning($"[BasisMedia] could not clear the clip cache: {exception.Message}", LogTag);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Shutdown()
    {
        Application.quitting -= Shutdown;
        BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
        BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
        BasisEventDriver.OnUpdate -= Tick;
        BasisNetworkModeration.OnGlobalVideoClipsLockedChanged -= HandleVideoClipsLockChanged;
        BasisNetworkModeration.OnGlobalVideoClipMaxMegabytesChanged -= HandleClipMaxSizeChanged;
        BasisMediaPersistence.Enabled.OnChanged -= SendPersistence;
        RemoveAll();
        ReleaseNetworkIdentity();
        initialized = false;
    }

    private static async void HandleLocalPlayerJoined(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
    {
        if (!BasisNetworkConnection.LocalPlayerIsConnected) return;
        int generation = connectionGeneration;
        if (hasNetworkId || resolveGeneration == generation) return;
        resolveGeneration = generation;
        BasisIdResolutionResult resolution = await BasisNetworkIdResolver.ResolveAsync(FixedNetworkIdentifier);
        if (!initialized || hasNetworkId || generation != connectionGeneration) return;
        if (!resolution.Success)
        {
            resolveGeneration = -1;
            BasisDebug.LogError($"[BasisMedia] clip share could not resolve its network identifier.", LogTag);
            return;
        }
        networkId = resolution.Id;
        hasNetworkId = true;
        BasisNetworkGenericMessages.RegisterDirectHandler(networkId, OnDirectNetworkMessage);
        SendToServer(BasisMediaClipProtocol.EncodeId(BasisMediaClipProtocol.OpServerCacheCatalog, Guid.Empty));
        if (BasisMediaPersistence.Enabled.RawValue) SendPersistence(true);
    }

    private static void SendPersistence(bool enabled)
    {
        byte[] payload = new byte[1 + 16 + 1];
        payload[0] = BasisMediaClipProtocol.OpPersistence;
        payload[1 + 16] = enabled ? (byte)1 : (byte)0;
        SendToServer(payload);
    }

    private static void HandleLocalPlayerLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
    {
        ReleaseNetworkIdentity();
        RemoveAll();
    }

    private static void ReleaseNetworkIdentity()
    {
        connectionGeneration++;
        resolveGeneration = -1;
        if (hasNetworkId) BasisNetworkGenericMessages.UnregisterDirectHandler(networkId);
        hasNetworkId = false;
        networkId = 0;
    }

    private static void EnsureNetworkIdentity()
    {
        if (!BasisNetworkConnection.LocalPlayerIsConnected) return;
        if (hasNetworkId)
        {
            if (BasisNetworkIdResolver.KnownIdMap.TryGetValue(FixedNetworkIdentifier, out ushort issued) && issued == networkId) return;
            ReleaseNetworkIdentity();
        }
        HandleLocalPlayerJoined(null, null);
    }

    private static bool TryGetLocalId(out ushort localId) => BasisNetworkConnection.TryGetLocalPlayerID(out localId);

    private static void OnFilesDropped(string[] paths)
    {
        if (paths == null) return;
        for (int i = 0; i < paths.Length; i++)
        {
            if (BasisMediaClipProtocol.TryGetContainer(paths[i], out BasisMediaClipContainer container)) ShareFile(paths[i], container);
        }
    }

    public static bool IsSupportedFile(string path) => BasisMediaClipProtocol.TryGetContainer(path, out _);

    public static async void ShareFile(string path, BasisMediaClipContainer container)
    {
        string fileName = SafeFileName(path);
        if (BasisNetworkModeration.MediaPlayerBlockedLocally)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.locked"));
            return;
        }
        if (BasisNetworkModeration.VideoClipsBlockedLocally)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.clipsLocked"));
            return;
        }
        if (Application.platform == RuntimePlatform.WebGLPlayer)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.unsupportedPlatform"));
            return;
        }
        int ownCount = CountLocalShares();
        if (ownCount >= MaxClipsPerSender)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.limit", MaxClipsPerSender));
            return;
        }
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception exception)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.readFailed", fileName, exception.Message));
            return;
        }
        if (length <= 0 || length > BasisMediaClipProtocol.MaxClipBytes)
        {
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.tooLarge", fileName, FormatMegabytes(length), FormatMegabytes(BasisMediaClipProtocol.MaxClipBytes)));
            return;
        }
        GetSpawnPose(out Vector3 position, out Quaternion rotation);
        Guid id = Guid.NewGuid();
        string cachePath = Path.Combine(cacheDirectory, id.ToString("N") + BasisMediaClipProtocol.Extension(container));
        BasisMediaClipFile storage;
        bool isVideo;
        try
        {
            (storage, isVideo) = await Task.Run(() => CopyIntoCache(path, cachePath, container));
        }
        catch (Exception exception)
        {
            TryDeleteFile(cachePath);
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.readFailed", fileName, exception.Message));
            return;
        }
        if (!isVideo || storage.Length <= 0 || storage.Length > BasisMediaClipProtocol.MaxClipBytes)
        {
            DiscardStorage(storage, cachePath);
            ShowNotice(BasisLocalization.Get("mediaClip.popup.title"), BasisLocalization.Get("mediaClip.popup.notVideo", fileName));
            return;
        }
        if (!initialized || CountLocalShares() >= MaxClipsPerSender || BasisNetworkModeration.VideoClipsBlockedLocally)
        {
            DiscardStorage(storage, cachePath);
            return;
        }
        int totalBytes = (int)storage.Length;

        ushort localId = 0;
        bool connected = BasisNetworkConnection.LocalPlayerIsConnected && TryGetLocalId(out localId);
        var clip = new Clip
        {
            Id = id,
            SharerId = localId,
            SharerName = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.SafeDisplayName : string.Empty,
            SharerProtected = connected && BasisNetworkModeration.LocalPlayerHasNode(BasisPermissions.PermNodes.protection),
            SharerBypassesLock = connected && BasisNetworkModeration.LocalPlayerHasGlobalLockBypass(),
            IsLocalSharer = true,
            Container = container,
            Name = BasisMediaClipProtocol.SanitizeName(Path.GetFileNameWithoutExtension(fileName)),
            TotalBytes = totalBytes,
            TotalChunks = BasisMediaClipProtocol.ChunkCount(totalBytes),
            Storage = storage,
            Complete = true,
            FilePath = cachePath,
            OwnsFile = true,
            CreatedTime = Time.realtimeSinceStartup,
            Relayed = new bool[BasisMediaClipProtocol.ChunkCount(totalBytes)],
        };
        clips[clip.Id] = clip;
        clip.View = BasisMediaClipObject.Build(clip.Id, clip.Name, position, rotation, 1f, BasisMediaClipProtocol.IsAudio(clip.Container));
        RefreshBackInfo(clip);
        if (panelsVisible) clip.View.SetPanelVisible(true);
        RegisterShareable(clip);
        BasisMediaClipStore.MarkReady(clip.Id, cachePath);
        BasisDebug.Log($"[BasisMedia] sharing clip '{clip.Name}' ({FormatMegabytes(clip.TotalBytes)}, {clip.TotalChunks} chunks).", LogTag);

        if (!connected || !hasNetworkId)
        {
            AnnounceUrlLocally(clip);
            return;
        }
        ushort[] recipients = CollectRemoteRecipients();
        Send(BasisMediaClipProtocol.EncodeOffer(BuildOffer(clip)), DeliveryMethod.ReliableOrdered, WithSelf(recipients, localId));
        if (recipients.Length > 0) jobs.Add(new Job { Clip = clip, Recipients = recipients });
    }

    private static ushort[] WithSelf(ushort[] recipients, ushort localId)
    {
        var withSelf = new ushort[recipients.Length + 1];
        Array.Copy(recipients, withSelf, recipients.Length);
        withSelf[recipients.Length] = localId;
        return withSelf;
    }

    private static void SendToServer(byte[] buffer)
    {
        if (!hasNetworkId || !TryGetLocalId(out ushort localId)) return;
        Send(buffer, DeliveryMethod.ReliableOrdered, new[] { localId });
    }

    private static (BasisMediaClipFile, bool) CopyIntoCache(string source, string destination, BasisMediaClipContainer container)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.Copy(source, destination, true);
        BasisMediaClipFile storage = BasisMediaClipFile.OpenForRead(destination);
        try
        {
            byte[] head = new byte[16];
            int count = (int)Math.Min(head.Length, storage.Length);
            storage.Read(0, head, 0, count);
            return (storage, BasisMediaClipProtocol.SignatureMatches(new ReadOnlySpan<byte>(head, 0, count), container));
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    private static void DiscardStorage(BasisMediaClipFile storage, string path)
    {
        storage?.Dispose();
        if (!string.IsNullOrEmpty(path)) TryDeleteFile(path);
    }

    private static BasisMediaClipOffer BuildOffer(Clip clip)
    {
        Transform t = clip.View != null ? clip.View.transform : null;
        return new BasisMediaClipOffer
        {
            Id = clip.Id,
            TotalBytes = clip.TotalBytes,
            Container = clip.Container,
            SharerProtected = clip.SharerProtected,
            SharerBypassesLock = clip.SharerBypassesLock,
            Position = t != null ? t.position : Vector3.zero,
            Rotation = t != null ? t.rotation : Quaternion.identity,
            Scale = t != null ? Mathf.Clamp(t.localScale.x, BasisMediaClipProtocol.MinScale, BasisMediaClipProtocol.MaxScale) : 1f,
            Name = clip.Name,
        };
    }

    private static ushort[] CollectRemoteRecipients()
    {
        scratchRecipients.Clear();
        TryGetLocalId(out ushort localId);
        foreach (KeyValuePair<ushort, BasisNetworkPlayer> pair in BasisNetworkPlayers.Players)
        {
            if (pair.Key == localId || pair.Value == null) continue;
            scratchRecipients.Add(pair.Key);
        }
        return scratchRecipients.ToArray();
    }

    private static int CountLocalShares()
    {
        int count = 0;
        foreach (Clip clip in clips.Values)
        {
            if (clip.IsLocalSharer && !clip.Adopted && !playlisted.Contains(clip.Id)) count++;
        }
        return count;
    }

    private static void AnnounceUrlLocally(Clip clip)
    {
        clip.UrlAnnounced = true;
        if (clip.View != null && clip.View.Player != null) clip.View.Player.LoadUrl(BasisMediaClipProtocol.ToUrl(clip.Id));
    }

    private static async void AnnounceUrl(Clip clip)
    {
        clip.UrlAnnounced = true;
        BasisMediaPlayerNetworking networking = clip.View != null ? clip.View.Networking : null;
        if (networking == null) return;
        bool ok = false;
        try
        {
            ok = await networking.TrySetUrlAsync(BasisMediaClipProtocol.ToUrl(clip.Id));
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"[BasisMedia] clip announce failed: {exception.Message}", LogTag);
        }
        if (!ok && clip.View != null && clip.View.Player != null && !clip.View.Player.IsPlaying) AnnounceUrlLocally(clip);
    }

    private static void Send(byte[] buffer, DeliveryMethod method, ushort[] recipients)
    {
        if (!hasNetworkId) return;
        BasisNetworkGenericMessages.OnNetworkMessageSendDirect(networkId, buffer, method, recipients);
    }

    private static void OnDirectNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length == 0) return;
        bool fromSelf = TryGetLocalId(out ushort localId) && senderId == localId;
        if (fromSelf && buffer[0] != BasisMediaClipProtocol.OpServerCacheState && buffer[0] != BasisMediaClipProtocol.OpServerHandover) return;
        try
        {
            switch (buffer[0])
            {
                case BasisMediaClipProtocol.OpOffer: HandleOffer(senderId, buffer, false); break;
                case BasisMediaClipProtocol.OpServerCacheOffer: HandleOffer(senderId, buffer, true); break;
                case BasisMediaClipProtocol.OpServerCacheState: if (fromSelf) HandleServerState(buffer); break;
                case BasisMediaClipProtocol.OpServerHandover: if (fromSelf) HandleServerHandover(buffer); break;
                case BasisMediaClipProtocol.OpChunk: HandleChunk(senderId, buffer); break;
                case BasisMediaClipProtocol.OpRemove: HandleRemove(senderId, buffer); break;
                case BasisMediaClipProtocol.OpPose: HandlePose(senderId, buffer); break;
                case BasisMediaClipProtocol.OpNeed: HandleNeed(senderId, buffer); break;
            }
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"[BasisMedia] clip message {buffer[0]} from {senderId} failed: {exception.Message}", LogTag);
        }
    }

    private static void HandleServerState(byte[] buffer)
    {
        if (!BasisMediaClipProtocol.TryDecodeServerState(buffer, out Guid id, out byte state)) return;
        if (!clips.TryGetValue(id, out Clip clip) || !clip.IsLocalSharer) return;
        clip.ServerState = state;
        if (state != BasisMediaClipProtocol.ServerStateUpload)
        {
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                if (jobs[i].Clip == clip && jobs[i].ServerUpload) jobs.RemoveAt(i);
            }
            BasisDebug.Log(state == BasisMediaClipProtocol.ServerStateHeld ? $"[BasisMedia] the server now holds clip '{clip.Name}' for players who join later." : $"[BasisMedia] the server stopped holding clip '{clip.Name}'.", LogTag);
            return;
        }
        for (int i = 0; i < jobs.Count; i++)
        {
            if (jobs[i].Clip == clip && jobs[i].ServerUpload) return;
        }
        if (TryGetLocalId(out ushort localId)) jobs.Add(new Job { Clip = clip, Recipients = new[] { localId }, ServerUpload = true });
    }

    private static void HandleOffer(ushort senderId, byte[] buffer, bool fromServer)
    {
        if (!BasisMediaClipProtocol.TryDecodeOffer(buffer, out BasisMediaClipOffer offer, out string error))
        {
            BasisDebug.LogWarning($"[BasisMedia] refused a clip offer from {senderId}: {error}.", LogTag);
            return;
        }
        if (clips.TryGetValue(offer.Id, out Clip existing))
        {
            if (!fromServer && existing.SharerId == senderId && existing.View != null) existing.View.SetRemoteTarget(offer.Position, offer.Rotation, offer.Scale);
            return;
        }
        if (dismissed.Contains(offer.Id)) return;
        if (BasisNetworkModeration.MediaPlayerBlockedLocally)
        {
            BasisDebug.Log($"[BasisMedia] ignored a clip from {senderId}: media players are locked.", LogTag);
            return;
        }
        if (BasisNetworkModeration.GlobalVideoClipsLocked && !offer.SharerBypassesLock)
        {
            BasisDebug.Log($"[BasisMedia] ignored a clip from {senderId}: video clips are locked.", LogTag);
            return;
        }
        if (!BasisMediaSettings.ClipAcceptShared.RawValue)
        {
            BasisDebug.Log($"[BasisMedia] ignored a clip from {senderId}: shared clips are turned off in settings.", LogTag);
            return;
        }
        bool listed = playlisted.Contains(offer.Id);
        int fromSender = CountFromSender(senderId, listed);
        if (fromSender >= (listed ? MaxPlaylistedPerSender : MaxClipsPerSender))
        {
            if (!listed && TryPark(senderId, buffer, fromServer, offer.Id)) return;
            BasisDebug.LogWarning($"[BasisMedia] refused a clip from {senderId}: they already share {fromSender}.", LogTag);
            return;
        }
        bool deferred = listed && !wanted.Contains(offer.Id);
        if (!deferred && inboundBytes + offer.TotalBytes > MaxInboundBytes)
        {
            if (listed)
            {
                deferred = true;
                pendingActivation.Add(offer.Id);
            }
            else
            {
                BasisDebug.LogWarning($"[BasisMedia] refused a clip from {senderId}: {FormatMegabytes(inboundBytes)} of clips already held.", LogTag);
                return;
            }
        }
        string sharerName = BasisNetworkPlayers.GetPlayerById(senderId, out BasisNetworkPlayer sharer) && sharer != null ? sharer.SafeDisplayName : string.Empty;
        float now = Time.realtimeSinceStartup;
        var created = new Clip
        {
            Id = offer.Id,
            SharerId = senderId,
            SharerName = sharerName,
            SharerProtected = offer.SharerProtected,
            SharerBypassesLock = offer.SharerBypassesLock,
            Container = offer.Container,
            Name = offer.Name,
            TotalBytes = offer.TotalBytes,
            TotalChunks = BasisMediaClipProtocol.ChunkCount(offer.TotalBytes),
            Received = deferred ? null : new bool[BasisMediaClipProtocol.ChunkCount(offer.TotalBytes)],
            LastProgressTime = now,
            LastNeedTime = now,
            CreatedTime = now,
            UrlAnnounced = true,
            FromServer = fromServer,
            Deferred = deferred,
            Pulled = listed,
        };
        if (deferred)
        {
            clips[created.Id] = created;
            RegisterShareable(created);
            return;
        }
        string partPath = Path.Combine(cacheDirectory, offer.Id.ToString("N") + ".part");
        try
        {
            created.Storage = BasisMediaClipFile.CreateForWrite(partPath, offer.TotalBytes);
        }
        catch (Exception exception)
        {
            TryDeleteFile(partPath);
            BasisDebug.LogWarning($"[BasisMedia] could not make room for a clip from {senderId}: {exception.Message}", LogTag);
            return;
        }
        created.FilePath = partPath;
        created.OwnsFile = true;
        inboundBytes += created.TotalBytes;
        clips[created.Id] = created;
        if (!listed) BuildView(created, offer.Position, offer.Rotation, offer.Scale);
        RegisterShareable(created);
        if (fromServer) SendToServer(BasisMediaClipProtocol.EncodeId(BasisMediaClipProtocol.OpServerCacheRequest, created.Id));
        else if (listed) RequestMissing(created);
    }

    private static void HandleChunk(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaClipProtocol.TryReadChunkHeader(buffer, out Guid id, out int index, out int length)) return;
        if (!clips.TryGetValue(id, out Clip clip) || clip.IsLocalSharer || clip.Complete || clip.Storage == null) return;
        if (clip.SharerId != senderId) return;
        if (index < 0 || index >= clip.TotalChunks || length != BasisMediaClipProtocol.ChunkLength(clip.TotalBytes, index)) return;
        if (clip.Received[index]) return;
        try
        {
            clip.Storage.Write((long)index * BasisMediaClipProtocol.ChunkBytes, buffer, BasisMediaClipProtocol.ChunkHeaderBytes, length);
        }
        catch (Exception exception)
        {
            FailReceive(clip, exception.Message);
            return;
        }
        clip.Received[index] = true;
        clip.ReceivedCount++;
        clip.LastProgressTime = Time.realtimeSinceStartup;
        if (clip.ReceivedCount >= clip.TotalChunks) FinishReceive(clip);
        else
        {
            UpdateReceiveStatus(clip);
            ContinuePull(clip);
        }
    }

    private static async void FinishReceive(Clip clip)
    {
        clip.Complete = true;
        byte[] head = new byte[16];
        int headLength = Math.Min(head.Length, clip.TotalBytes);
        try
        {
            clip.Storage.Read(0, head, 0, headLength);
        }
        catch (Exception exception)
        {
            FailReceive(clip, exception.Message);
            return;
        }
        if (!BasisMediaClipProtocol.SignatureMatches(new ReadOnlySpan<byte>(head, 0, headLength), clip.Container))
        {
            FailReceive(clip, "the data does not match its media format");
            return;
        }
        clip.Writing = true;
        clip.View?.SetStatus(BasisLocalization.Get("mediaClip.status.preparing"));
        ReleaseInbound(clip);
        string partPath = clip.FilePath;
        string path = Path.Combine(cacheDirectory, clip.Id.ToString("N") + BasisMediaClipProtocol.Extension(clip.Container));
        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(partPath, path);
            });
        }
        catch (Exception exception)
        {
            FailReceive(clip, exception.Message);
            return;
        }
        clip.Writing = false;
        clip.FilePath = path;
        if (!clips.TryGetValue(clip.Id, out Clip current) || current != clip)
        {
            TryDeleteFile(path);
            return;
        }
        clip.View?.SetStatus(string.Empty);
        BasisMediaClipStore.MarkReady(clip.Id, path);
        clip.View?.RefreshPanel();
    }

    private static void ReleaseInbound(Clip clip)
    {
        clip.Storage?.Dispose();
        clip.Storage = null;
        if (clip.Received == null) return;
        clip.Received = null;
        inboundBytes -= clip.TotalBytes;
    }

    private static void FailReceive(Clip clip, string reason, string statusKey = "mediaClip.status.failed")
    {
        clip.Failed = true;
        clip.Writing = false;
        ReleaseInbound(clip);
        if (clip.OwnsFile && !string.IsNullOrEmpty(clip.FilePath)) TryDeleteFile(clip.FilePath);
        BasisDebug.LogWarning($"[BasisMedia] clip '{clip.Name}' from {clip.SharerId} failed: {reason}.", LogTag);
        clip.View?.SetStatus(BasisLocalization.Get(statusKey));
    }

    private static void UpdateReceiveStatus(Clip clip)
    {
        if (clip.View == null || clip.TotalChunks <= 0) return;
        int percent = (int)(clip.ReceivedCount * 100L / clip.TotalChunks);
        if (percent == clip.LastShownPercent) return;
        clip.LastShownPercent = percent;
        clip.View.SetStatus(BasisLocalization.Get("mediaClip.status.receiving", percent));
    }

    private static void HandleRemove(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaClipProtocol.TryDecodeId(buffer, BasisMediaClipProtocol.OpRemove, out Guid id)) return;
        if (!clips.TryGetValue(id, out Clip clip)) return;
        bool allowed = senderId == clip.SharerId || (!clip.SharerProtected && !BasisNetworkModeration.GlobalContentRemovalLocked);
        if (!allowed) return;
        RemoveClip(id);
    }

    private static void HandlePose(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaClipProtocol.TryDecodePose(buffer, out Guid id, out Vector3 position, out Quaternion rotation, out float scale)) return;
        if (!clips.TryGetValue(id, out Clip clip) || clip.View == null) return;
        clip.View.SetRemoteTarget(position, rotation, scale);
    }

    private static void HandleNeed(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaClipProtocol.TryDecodeNeed(buffer, out Guid id, scratchIndices)) return;
        if (!clips.TryGetValue(id, out Clip clip) || !clip.IsLocalSharer || clip.Storage == null) return;
        Job job = null;
        for (int i = 0; i < jobs.Count; i++)
        {
            Job candidate = jobs[i];
            if (candidate.Clip == clip && candidate.Explicit != null && candidate.Recipients.Length == 1 && candidate.Recipients[0] == senderId)
            {
                job = candidate;
                break;
            }
        }
        if (job == null)
        {
            job = new Job { Clip = clip, Recipients = new[] { senderId }, Explicit = new Queue<int>() };
            jobs.Add(job);
        }
        for (int i = 0; i < scratchIndices.Count; i++)
        {
            int index = scratchIndices[i];
            if (index >= 0 && index < clip.TotalChunks && job.Explicit.Count < clip.TotalChunks) job.Explicit.Enqueue(index);
        }
        scratchIndices.Clear();
    }

    private static void HandlePlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || !hasNetworkId) return;
        if (TryGetLocalId(out ushort localId) && player.playerId == localId) return;
        foreach (Clip clip in clips.Values)
        {
            if (!clip.IsLocalSharer || clip.Storage == null || clip.ServerState == BasisMediaClipProtocol.ServerStateHeld) continue;
            var recipients = new[] { player.playerId };
            Send(BasisMediaClipProtocol.EncodeOffer(BuildOffer(clip)), DeliveryMethod.ReliableOrdered, recipients);
            if (!playlisted.Contains(clip.Id)) jobs.Add(new Job { Clip = clip, Recipients = recipients });
        }
    }

    private static void HandlePlayerLeft(BasisNetworkPlayer player)
    {
        if (player == null) return;
        ushort leftId = player.playerId;
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            Job job = jobs[i];
            if (Array.IndexOf(job.Recipients, leftId) < 0) continue;
            if (job.Recipients.Length == 1)
            {
                jobs.RemoveAt(i);
                continue;
            }
            scratchRecipients.Clear();
            for (int r = 0; r < job.Recipients.Length; r++)
            {
                if (job.Recipients[r] != leftId) scratchRecipients.Add(job.Recipients[r]);
            }
            job.Recipients = scratchRecipients.ToArray();
        }
        scratchIds.Clear();
        foreach (Clip clip in clips.Values)
        {
            if (!clip.IsLocalSharer && clip.SharerId == leftId) scratchIds.Add(clip.Id);
        }
        bool awaitHandover = BasisNetworkModeration.ServerAnnouncesMediaHandover;
        float deadline = Time.realtimeSinceStartup + OrphanGraceSeconds;
        for (int i = 0; i < scratchIds.Count; i++)
        {
            Guid id = scratchIds[i];
            if (awaitHandover && clips.TryGetValue(id, out Clip kept) && kept.Complete && !kept.Writing) orphaned[id] = (leftId, deadline);
            else RemoveClip(id);
        }
        scratchIds.Clear();
        DropParkedFrom(leftId);
    }

    private static void HandleServerHandover(byte[] buffer)
    {
        if (buffer.Length < 1 + sizeof(ushort) * 2 || !TryGetLocalId(out ushort localId)) return;
        ushort leaver = BitConverter.ToUInt16(buffer, 1);
        ushort successor = BitConverter.ToUInt16(buffer, 1 + sizeof(ushort));
        if (leaver == localId) return;
        bool removeAll = successor == ushort.MaxValue;
        scratchIds.Clear();
        foreach (Clip clip in clips.Values)
        {
            if (!clip.IsLocalSharer && clip.SharerId == leaver) scratchIds.Add(clip.Id);
        }
        int handed = 0;
        for (int i = 0; i < scratchIds.Count; i++)
        {
            Guid id = scratchIds[i];
            orphaned.Remove(id);
            Clip clip = clips[id];
            if (removeAll || !clip.Complete || clip.Writing)
            {
                RemoveClip(id);
                if (!removeAll) dismissed.Remove(id);
                continue;
            }
            string leaverName = string.IsNullOrEmpty(clip.SharerName) ? BasisLocalization.Get("mediaClip.panel.unknownSharer") : clip.SharerName;
            clip.SharerId = successor;
            handed++;
            if (successor != localId)
            {
                string holder = BasisNetworkPlayers.GetPlayerById(successor, out BasisNetworkPlayer player) && player != null && !string.IsNullOrEmpty(player.SafeDisplayName) ? player.SafeDisplayName : BasisLocalization.Get("mediaClip.panel.unknownSharer");
                clip.HandoverNote = BasisLocalization.Get("mediaClip.panel.keptBy", holder, leaverName);
            }
            else
            {
                string failure = AdoptClip(clip);
                clip.HandoverNote = failure == null
                    ? BasisLocalization.Get("mediaClip.panel.keptByYou", leaverName)
                    : BasisLocalization.Get("mediaClip.panel.adoptFailed", failure);
            }
            RefreshBackInfo(clip);
        }
        scratchIds.Clear();
        if (handed > 0) BasisDebug.Log($"[BasisMedia] {handed} clip(s) from player {leaver} now belong to player {successor}{(successor == localId ? " (us)" : string.Empty)}.", LogTag);
    }

    private static string AdoptClip(Clip clip)
    {
        if (string.IsNullOrEmpty(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            BasisDebug.LogWarning($"[BasisMedia] could not take over clip '{clip.Name}': its file is gone.", LogTag);
            return BasisLocalization.Get("mediaClip.panel.adoptFailed.missingFile");
        }
        try
        {
            clip.Storage = BasisMediaClipFile.OpenForRead(clip.FilePath);
        }
        catch (Exception exception)
        {
            BasisDebug.LogWarning($"[BasisMedia] could not take over clip '{clip.Name}': {exception.Message}", LogTag);
            return exception.Message;
        }
        clip.IsLocalSharer = true;
        clip.Adopted = true;
        clip.ServerState = BasisMediaClipProtocol.ServerStateNotHeld;
        clip.Relayed = new bool[clip.TotalChunks];
        return null;
    }

    private static void ExpireOrphans(float now)
    {
        if (orphaned.Count == 0) return;
        scratchIds.Clear();
        foreach (KeyValuePair<Guid, (ushort Leaver, float Deadline)> entry in orphaned)
        {
            if (now >= entry.Value.Deadline) scratchIds.Add(entry.Key);
        }
        for (int i = 0; i < scratchIds.Count; i++)
        {
            Guid id = scratchIds[i];
            ushort leaver = orphaned[id].Leaver;
            orphaned.Remove(id);
            if (clips.TryGetValue(id, out Clip clip) && !clip.IsLocalSharer && clip.SharerId == leaver) RemoveClip(id);
        }
        scratchIds.Clear();
    }

    public static bool CanLocalPlayerRemove(Guid id)
    {
        if (!clips.TryGetValue(id, out Clip clip)) return false;
        if (clip.IsLocalSharer) return true;
        if (!BasisNetworkConnection.LocalPlayerIsConnected) return true;
        return BasisContentShareManager.CanLocalPlayerRemove(clip.SharerId, clip.SharerProtected);
    }

    public static void RequestRemove(Guid id)
    {
        if (!clips.ContainsKey(id)) return;
        bool everyone = CanLocalPlayerRemove(id) && hasNetworkId;
        if (everyone && (clips[id].IsLocalSharer || !clips[id].SharerProtected))
        {
            byte[] remove = BasisMediaClipProtocol.EncodeRemove(id);
            Send(remove, DeliveryMethod.ReliableOrdered, null);
            SendToServer(remove);
        }
        RemoveClip(id);
    }

    public static void SendPose(BasisMediaClipObject view, bool reliable)
    {
        if (view == null || !hasNetworkId || !clips.ContainsKey(view.ClipId)) return;
        Transform t = view.transform;
        float scale = Mathf.Clamp(t.localScale.x, BasisMediaClipProtocol.MinScale, BasisMediaClipProtocol.MaxScale);
        byte[] pose = BasisMediaClipProtocol.EncodePose(view.ClipId, t.position, t.rotation, scale);
        Send(pose, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced, null);
        if (reliable) SendToServer(pose);
    }

    private static void RemoveClip(Guid id)
    {
        if (!clips.Remove(id, out Clip clip)) return;
        orphaned.Remove(id);
        if (!clip.IsLocalSharer) dismissed.Add(id);
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            if (jobs[i].Clip == clip) jobs.RemoveAt(i);
        }
        ReleaseInbound(clip);
        DestroyView(clip);
        BasisMediaClipStore.Forget(id);
        BasisShareableRegistry.Unregister(ShareableId(id));
        if (clip.OwnsFile && !string.IsNullOrEmpty(clip.FilePath)) DeleteFileLater(clip.FilePath);
        wanted.Remove(id);
        pendingActivation.Remove(id);
        OnClipRemoved?.Invoke(id);
    }

    private static void RemoveAll()
    {
        scratchIds.Clear();
        scratchIds.AddRange(clips.Keys);
        for (int i = 0; i < scratchIds.Count; i++) RemoveClip(scratchIds[i]);
        scratchIds.Clear();
        jobs.Clear();
        dismissed.Clear();
        orphaned.Clear();
        ClearPlaylistLinks();
        inboundBytes = 0;
    }

    private static async void DeleteFileLater(string path)
    {
        await Task.Delay(2000);
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            BasisDebug.LogWarning($"[BasisMedia] could not delete clip cache file: {exception.Message}", LogTag);
        }
    }

    private static void Tick()
    {
        if (!initialized) return;
        if (!hasNetworkId) EnsureNetworkIdentity();
        TickPlaylistLinks();
        if (clips.Count == 0)
        {
            relayBudget = 0d;
            return;
        }
        float now = Time.realtimeSinceStartup;
        float deltaTime = Time.unscaledDeltaTime;
        ExpireOrphans(now);
        bool menuOpen = BasisMainMenu.Instance != null;
        bool refreshPanels = false;
        panelRefreshTimer -= deltaTime;
        if (panelRefreshTimer <= 0f)
        {
            panelRefreshTimer = PanelRefreshSeconds;
            refreshPanels = true;
        }
        bool togglePanels = menuOpen != panelsVisible;
        panelsVisible = menuOpen;
        foreach (Clip clip in clips.Values)
        {
            float stall = clip.Pulled ? PullStallSeconds : StallSeconds;
            if (!clip.IsLocalSharer && !clip.Complete && !clip.Failed && clip.Received != null && now - clip.LastProgressTime > stall && now - clip.LastNeedTime > stall)
            {
                clip.LastNeedTime = now;
                RequestMissing(clip);
            }
            BasisMediaClipObject view = clip.View;
            if (view == null) continue;
            if (togglePanels) view.SetPanelVisible(menuOpen);
            else if (refreshPanels) view.RefreshPanel();
            if (clip.IsLocalSharer && !clip.UrlAnnounced)
            {
                if (view.Networking != null && view.Networking.HasNetworkID) AnnounceUrl(clip);
                else if (!BasisNetworkConnection.LocalPlayerIsConnected || now - clip.CreatedTime > NetworkUrlTimeoutSeconds) AnnounceUrlLocally(clip);
            }
            if (view.HeldLocally)
            {
                clip.PoseTimer -= deltaTime;
                if (clip.PoseTimer <= 0f)
                {
                    clip.PoseTimer = PoseIntervalSeconds;
                    SendPose(view, false);
                }
            }
        }
        PumpJobs(deltaTime);
    }

    private static void RequestMissing(Clip clip)
    {
        bool sharerHere = BasisNetworkPlayers.GetPlayerById(clip.SharerId, out _);
        if (!hasNetworkId || (!sharerHere && !clip.FromServer))
        {
            clip.View?.SetStatus(BasisLocalization.Get("mediaClip.status.waiting"));
            return;
        }
        scratchIndices.Clear();
        for (int i = 0; i < clip.TotalChunks && scratchIndices.Count < BasisMediaClipProtocol.MaxNeedIndices; i++)
        {
            if (!clip.Received[i]) scratchIndices.Add(i);
        }
        if (scratchIndices.Count == 0) return;
        clip.PullTarget = clip.ReceivedCount + scratchIndices.Count;
        byte[] need = BasisMediaClipProtocol.EncodeNeed(clip.Id, scratchIndices);
        if (clip.FromServer && (!sharerHere || clip.NeedRounds++ % 2 == 0)) SendToServer(need);
        else Send(need, DeliveryMethod.ReliableOrdered, new[] { clip.SharerId });
        scratchIndices.Clear();
    }

    private static void PumpJobs(float deltaTime)
    {
        if (jobs.Count == 0 || !hasNetworkId)
        {
            relayBudget = 0d;
            return;
        }
        SplitDirectRecipients();
        relayBudget = Math.Min(relayBudget + RelayBytesPerSecond * deltaTime, RelayBytesPerSecond * BurstSeconds);
        for (int i = 0; i < jobs.Count; i++)
        {
            Job job = jobs[i];
            if (!job.Direct) continue;
            double rate = BasisP2PManager.IsP2PSessionLocal(job.Recipients[0]) ? DirectLanBytesPerSecond : DirectBytesPerSecond;
            job.Budget = Math.Min(job.Budget + rate * deltaTime, rate * BurstSeconds);
        }
        int sent = 0;
        int skipped = 0;
        while (jobs.Count > 0 && sent < MaxChunksPerFrame && skipped < jobs.Count)
        {
            if (jobCursor >= jobs.Count) jobCursor = 0;
            Job job = jobs[jobCursor];
            Clip clip = job.Clip;
            if ((job.Direct ? job.Budget : relayBudget) <= 0d || IsCongested(job))
            {
                skipped++;
                jobCursor++;
                continue;
            }
            if (job.ServerUpload && HasRelayStream(job))
            {
                skipped++;
                jobCursor++;
                continue;
            }
            skipped = 0;
            int index;
            if (job.ServerUpload)
            {
                while (job.Next < clip.TotalChunks && clip.Relayed != null && clip.Relayed[job.Next]) job.Next++;
                if (job.Next >= clip.TotalChunks || clip.Storage == null)
                {
                    jobs.RemoveAt(jobCursor);
                    continue;
                }
                index = job.Next++;
            }
            else if (job.Explicit != null)
            {
                if (job.Explicit.Count == 0 || clip.Storage == null)
                {
                    jobs.RemoveAt(jobCursor);
                    continue;
                }
                index = job.Explicit.Dequeue();
            }
            else
            {
                if (job.Next >= clip.TotalChunks || clip.Storage == null)
                {
                    jobs.RemoveAt(jobCursor);
                    continue;
                }
                index = job.Next++;
            }
            int length = BasisMediaClipProtocol.ChunkLength(clip.TotalBytes, index);
            if (length <= 0) continue;
            byte[] packet = length == BasisMediaClipProtocol.ChunkBytes
                ? chunkScratch ??= new byte[BasisMediaClipProtocol.ChunkHeaderBytes + BasisMediaClipProtocol.ChunkBytes]
                : new byte[BasisMediaClipProtocol.ChunkHeaderBytes + length];
            WriteChunkHeader(packet, clip.Id, index);
            try
            {
                clip.Storage.Read((long)index * BasisMediaClipProtocol.ChunkBytes, packet, BasisMediaClipProtocol.ChunkHeaderBytes, length);
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"[BasisMedia] clip '{clip.Name}' could not be read for sending: {exception.Message}", LogTag);
                RemoveJobsFor(clip);
                continue;
            }
            if (!job.Direct || !SendDirect(job.Recipients[0], packet))
            {
                Send(packet, DeliveryMethod.ReliableOrdered, job.Recipients);
                if (clip.Relayed != null) clip.Relayed[index] = true;
            }
            double cost = (double)packet.Length * Math.Max(1, job.Recipients.Length);
            if (job.Direct) job.Budget -= cost;
            else relayBudget -= cost;
            sent++;
            jobCursor++;
        }
    }

    private static bool HasRelayStream(Job upload)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            Job other = jobs[i];
            if (other != upload && other.Clip == upload.Clip && !other.Direct && !other.ServerUpload && other.Explicit == null) return true;
        }
        return false;
    }

    private static bool SendDirect(ushort recipient, byte[] packet)
    {
        directWriter ??= new NetDataWriter(true, BasisMediaClipProtocol.ChunkHeaderBytes + BasisMediaClipProtocol.ChunkBytes + sizeof(ushort));
        directWriter.Reset();
        directWriter.Put(networkId);
        directWriter.Put(packet, 0, packet.Length);
        return BasisP2PManager.SendDirectTo(recipient, directWriter, BasisNetworkCommons.DirectSceneChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void RemoveJobsFor(Clip clip)
    {
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            if (jobs[i].Clip == clip) jobs.RemoveAt(i);
        }
    }

    private static bool IsDirectRecipient(ushort playerId) => BasisP2PManager.TryGetP2PPeer(playerId, out _);

    private static void SplitDirectRecipients()
    {
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            Job job = jobs[i];
            if (job.Direct)
            {
                if (!IsDirectRecipient(job.Recipients[0]))
                {
                    job.Direct = false;
                    job.Budget = 0d;
                }
                continue;
            }
            int direct = 0;
            for (int r = 0; r < job.Recipients.Length; r++)
            {
                if (IsDirectRecipient(job.Recipients[r])) direct++;
            }
            if (direct == 0) continue;
            scratchRecipients.Clear();
            for (int r = 0; r < job.Recipients.Length; r++)
            {
                ushort recipient = job.Recipients[r];
                if (!IsDirectRecipient(recipient))
                {
                    scratchRecipients.Add(recipient);
                    continue;
                }
                jobs.Add(new Job
                {
                    Clip = job.Clip,
                    Recipients = new[] { recipient },
                    Next = job.Next,
                    Explicit = job.Explicit != null ? new Queue<int>(job.Explicit) : null,
                    Direct = true,
                });
            }
            if (scratchRecipients.Count == 0) jobs.RemoveAt(i);
            else job.Recipients = scratchRecipients.ToArray();
        }
    }

    private static bool IsCongested(Job job)
    {
        if (job.Direct)
        {
            return BasisP2PManager.TryGetP2PPeer(job.Recipients[0], out NetPeer peer)
                && peer.GetPacketsCountInQueue(BasisNetworkCommons.DirectSceneChannel, DeliveryMethod.ReliableOrdered) > MaxQueuedSendPackets;
        }
        NetPeer server = BasisNetworkConnection.LocalPlayerPeer;
        return server != null && server.GetPacketsCountInQueue(BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered) > MaxQueuedSendPackets;
    }

    private static void HandleClipMaxSizeChanged(int megabytes)
    {
        BasisMediaClipProtocol.MaxClipBytes = (int)Math.Min(BasisMediaClipProtocol.HardMaxClipBytes, (long)megabytes * 1024L * 1024L);
    }

    private static void HandleVideoClipsLockChanged(bool locked)
    {
        if (!locked) return;
        scratchIds.Clear();
        foreach (Clip clip in clips.Values)
        {
            if (clip.IsLocalSharer)
            {
                if (!BasisNetworkModeration.VideoClipsBlockedLocally) continue;
                for (int i = jobs.Count - 1; i >= 0; i--)
                {
                    if (jobs[i].Clip == clip) jobs.RemoveAt(i);
                }
                continue;
            }
            if (!clip.Complete && !clip.Failed && !clip.SharerBypassesLock) scratchIds.Add(clip.Id);
        }
        for (int i = 0; i < scratchIds.Count; i++)
        {
            if (clips.TryGetValue(scratchIds[i], out Clip clip)) FailReceive(clip, "video clips were locked by an admin", "mediaClip.status.locked");
        }
        scratchIds.Clear();
    }

    public static bool CanSave(Guid id)
    {
        return clips.TryGetValue(id, out Clip clip) && !string.IsNullOrEmpty(clip.FilePath) && BasisMediaClipStore.TryGetReadyPath(id, out _);
    }

    public static async void SaveClip(Guid id, Action<bool> done)
    {
        if (!clips.TryGetValue(id, out Clip clip) || string.IsNullOrEmpty(clip.FilePath) || !BasisMediaClipStore.TryGetReadyPath(id, out string source))
        {
            done?.Invoke(false);
            return;
        }
        string extension = BasisMediaClipProtocol.Extension(clip.Container) ?? Path.GetExtension(source);
        string baseName = SaveFileBaseName(clip.Name);
        string target = null;
        bool ok;
        try
        {
            target = await Task.Run(() =>
            {
                string folder = SaveFolder(BasisMediaClipProtocol.IsAudio(clip.Container));
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                string candidate = Path.Combine(folder, baseName + " " + stamp + extension);
                for (int n = 2; File.Exists(candidate) && n < 1000; n++) candidate = Path.Combine(folder, baseName + " " + stamp + " (" + n + ")" + extension);
                File.Copy(source, candidate, false);
                return candidate;
            });
            ok = true;
        }
        catch (Exception exception)
        {
            ok = false;
            BasisDebug.LogWarning($"[BasisMedia] could not save clip '{clip.Name}': {exception.Message}", LogTag);
        }
        if (ok) BasisDebug.Log($"[BasisMedia] saved clip to {target}", LogTag);
        done?.Invoke(ok);
    }

    private static string SaveFolder(bool audio)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string library = Environment.GetFolderPath(audio ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrEmpty(library)) return Path.Combine(library, "Basis");
#endif
        return Path.Combine(Application.persistentDataPath, "Basis", audio ? "Audio" : "Videos");
    }

    private static string SaveFileBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = BasisLocalization.Get("mediaClip.defaultName");
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length && builder.Length < 80; i++)
        {
            char c = name[i];
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '.' ? '_' : c);
        }
        string result = builder.ToString().Trim();
        return result.Length == 0 ? "Basis clip" : result;
    }

    private static void RefreshBackInfo(Clip clip)
    {
        if (clip.View == null) return;
        string sharer = clip.IsLocalSharer && !clip.Adopted
            ? BasisLocalization.Get("mediaClip.panel.sharedByYou")
            : BasisLocalization.Get("mediaClip.status.sharedBy", string.IsNullOrEmpty(clip.SharerName) ? BasisLocalization.Get("mediaClip.panel.unknownSharer") : clip.SharerName);
        string format = (BasisMediaClipProtocol.Extension(clip.Container) ?? string.Empty).TrimStart('.').ToUpperInvariant();
        if (!string.IsNullOrEmpty(clip.HandoverNote)) sharer += "\n" + clip.HandoverNote;
        clip.View.SetBackInfo(sharer, BasisLocalization.Get("mediaClip.panel.details", FormatMegabytes(clip.TotalBytes), format));
    }

    private static void WriteChunkHeader(byte[] packet, Guid id, int index)
    {
        packet[0] = BasisMediaClipProtocol.OpChunk;
        id.TryWriteBytes(new Span<byte>(packet, 1, BasisMediaClipProtocol.GuidBytes));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(packet, 1 + BasisMediaClipProtocol.GuidBytes, sizeof(int)), index);
    }

    private static string ShareableId(Guid id) => "mediaclip:" + id.ToString("N");

    private static void RegisterShareable(Clip clip)
    {
        Guid id = clip.Id;
        BasisShareableRegistry.Register(new BasisShareableEntry
        {
            Id = ShareableId(id),
            Kind = BasisShareableKind.Video,
            Title = clip.Name,
            SharerName = clip.SharerName,
            Protected = clip.SharerProtected,
            Actions = new List<BasisShareableAction>
            {
                new BasisShareableAction { Style = BasisShareableActionStyle.Destructive, Invoke = () => RequestRemove(id) },
            },
        });
    }

    private static void GetSpawnPose(out Vector3 position, out Quaternion rotation)
    {
        if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null)
        {
            BasisLocalCameraDriver.Instance.transform.GetPositionAndRotation(out Vector3 cameraPosition, out Quaternion cameraRotation);
            Vector3 forward = cameraRotation * Vector3.forward;
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = cameraRotation * Vector3.up;
            flat.Normalize();
            position = cameraPosition + flat * SpawnDistance;
            rotation = Quaternion.LookRotation(flat, Vector3.up);
            return;
        }
        position = Vector3.zero;
        rotation = Quaternion.identity;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return BasisMediaClipProtocol.SanitizeName(Path.GetFileName(path));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string FormatMegabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";

    private static void ShowNotice(string title, string description)
    {
        BasisDebug.LogWarning($"[BasisMedia] {description}", LogTag);
        bool menuWasOpen = BasisMainMenu.Instance != null;
        if (!menuWasOpen) BasisMainMenu.Open();
        if (BasisMainMenu.Instance == null) return;
        if (BasisMainMenu.Instance.Dialogue) BasisMainMenu.Instance.Dialogue.ReleaseInstance();
        BasisMainMenu.Instance.OpenDialogue(title, description, BasisLocalization.Get("mediaClip.popup.accept"), _ =>
        {
            if (!menuWasOpen) BasisMainMenu.Close();
        }, category: BasisNotificationCategory.Content);
    }
}
