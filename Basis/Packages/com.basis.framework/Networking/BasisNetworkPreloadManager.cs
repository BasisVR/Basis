using Unity.Scripting.LifecycleManagement;
using Basis;
using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BundledContentHolder;
using static SerializableBasis;

/// <summary>
/// Client-side manager for preloaded and synchronized resource loading.
/// Handles downloading content without spawning, tracking readiness,
/// and responding to server spawn signals.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisNetworkPreloadManager
{
    /// <summary>
    /// Timeout duration for synchronized loads. If a client hasn't finished
    /// downloading within this window, it reports failure to the server.
    /// </summary>
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Stores preloaded resources that have been downloaded but not yet spawned.
    /// Key is LoadedNetID.
    /// </summary>
    public static readonly ConcurrentDictionary<string, PreloadedResource> PreloadedResources = new();

    private static CancellationTokenSource _cts = new CancellationTokenSource();

    public class PreloadedResource
    {
        public LocalLoadResource LoadResource;
        public BasisTrackedBundleWrapper BundleWrapper;
        public bool IsReady, Downloading, SpawnRequested;
        public byte Swap;
        public DateTime PreloadStartUtc;
    }

    private sealed class SharedDownload
    {
        public Task<BasisTrackedBundleWrapper> Task;
        public CancellationTokenSource Cancel;
        public float Progress;
        public volatile string Stage;
        public int Shown = -1;
    }
    private static readonly ConcurrentDictionary<string, SharedDownload> inFlight = new ConcurrentDictionary<string, SharedDownload>();
    private static async Task HandlePreload(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;
        BasisDebug.Log($"PreloadManager: Beginning preload for {resource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);
        var preloaded = new PreloadedResource
        {
            LoadResource = resource,
            IsReady = false,
            Downloading = true,
            PreloadStartUtc = DateTime.UtcNow,
        };
        PreloadedResources[netId] = preloaded;
        BasisTrackedBundleWrapper wrapper = await DownloadShared(resource);
        preloaded.BundleWrapper = wrapper;
        preloaded.IsReady = wrapper != null;
        preloaded.Downloading = false;
    }
    private static async Task<BasisTrackedBundleWrapper> DownloadShared(LocalLoadResource resource)
    {
        string key = DownloadKey(resource.CombinedURL);
        if (!inFlight.TryGetValue(key, out SharedDownload shared) || shared.Task.IsCompleted)
        {
            shared = new SharedDownload { Cancel = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token) };
            shared.Task = DownloadToDisc(resource, shared, key);
            inFlight[key] = shared;
        }
        return await shared.Task;
    }
    private static string DownloadKey(string url) => string.IsNullOrEmpty(url) ? string.Empty : BasisIOManagement.CanonicalizeRemoteUrl(url);
    private static async Task<BasisTrackedBundleWrapper> DownloadToDisc(LocalLoadResource resource, SharedDownload shared, string key)
    {
        CancellationTokenSource loadCancel = shared.Cancel;
        try
        {
            BasisLoadableBundle loadBundle = new BasisLoadableBundle
            {
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
                {
                    RemoteBeeFileLocation = resource.CombinedURL,
                    IsNetworkSourced = true
                },
                UnlockPassword = resource.UnlockPassword,
            };
            BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
            {
                LoadableBundle = loadBundle,
            };
            BasisProgressReport report = new BasisProgressReport();
            report.OnProgressReport += (_, progress, stage) =>
            {
                shared.Progress = progress;
                if (!string.IsNullOrEmpty(stage))
                {
                    shared.Stage = stage;
                }
                int whole = (int)progress;
                if (Interlocked.Exchange(ref shared.Shown, whole) != whole)
                {
                    BasisDeviceManagement.EnqueueOnMainThread(() => PublishDownloadProgress(key, progress, shared.Stage));
                }
            };
            CancellationToken cancel = loadCancel.Token;
            await BasisLoadHandler.EnsureInitializationComplete();
            await BasisBeeKeyGrant.EnsureResolvedAsync(loadBundle, cancel);
            string error = await BasisBeeManagement.DownloadToCache(wrapper, report, cancel);
            if (!string.IsNullOrEmpty(error))
            {
                throw new Exception($"Failed to download BEE file: {error}");
            }
            BasisDebug.Log($"PreloadManager: {resource.CombinedURL} is on disk (NetID={resource.LoadedNetID})", BasisDebug.LogTag.Networking);
            return wrapper;
        }
        catch (Exception ex)
        {
            if (loadCancel.IsCancellationRequested)
            {
                BasisDebug.Log($"PreloadManager: Preload of {resource.CombinedURL} (NetID={resource.LoadedNetID}) was cancelled", BasisDebug.LogTag.Networking);
            }
            else
            {
                BasisDebug.LogError($"PreloadManager: Failed to preload {resource.CombinedURL} (NetID={resource.LoadedNetID}): {ex.Message}");
            }
            return null;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, SharedDownload>>)inFlight).Remove(new KeyValuePair<string, SharedDownload>(key, shared));
            loadCancel.Dispose();
        }
    }
    private static void PublishDownloadProgress(string key, float progress, string stage)
    {
        foreach (PredownloadStatus status in predownloads.Values)
        {
            if (status.DownloadKey != key || status.Local != PredownloadLocalState.Downloading)
            {
                continue;
            }
            status.LocalProgress = progress;
            status.LocalStage = stage;
            SyncDownloadEntry(status);
            OnPredownloadChanged?.Invoke(status);
        }
    }
    public static bool IsInProgress(PredownloadStatus status)
    {
        if (status == null || status.Closed)
        {
            return false;
        }
        return status.Local == PredownloadLocalState.Downloading || status.Local == PredownloadLocalState.Waiting || (status.HasCounts && status.Left > 0);
    }
    public static string PlayerName(ushort playerId)
    {
        if (playerId == BasisPredownloadProtocol.ServerRequester)
        {
            return BasisLocalization.Get("notifications.downloads.server");
        }
        if (BasisNetworkPlayers.GetPlayerById(playerId, out BasisNetworkPlayer player) && player?.Player != null)
        {
            string name = player.Player.SafeDisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = player.Player.DisplayName;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        return BasisLocalization.Get("notifications.downloads.unknownPlayer", playerId);
    }
    public static string PlayerNames(ushort[] playerIds, int total, int shown)
    {
        if (playerIds == null || playerIds.Length == 0)
        {
            return null;
        }
        int count = Math.Min(playerIds.Length, Math.Max(1, shown));
        string[] names = new string[count];
        for (int index = 0; index < count; index++)
        {
            names[index] = PlayerName(playerIds[index]);
        }
        string joined = string.Join(", ", names);
        int more = Math.Max(total, playerIds.Length) - count;
        return more > 0 ? BasisLocalization.Get("notifications.downloads.andMore", joined, more) : joined;
    }
    public static string DescribeWaiting(PredownloadStatus status, int shown)
    {
        string names = PlayerNames(status.WaitingPeers, status.Left, shown);
        return names == null ? null : BasisLocalization.Get("notifications.downloads.waitingOn", names);
    }
    public static string DescribeDetails(PredownloadStatus status)
    {
        List<string> lines = new List<string>();
        if (status.HasCounts)
        {
            lines.Add(BasisLocalization.Get("notifications.downloads.startedBy", PlayerName(status.Requester)));
            lines.Add(BasisLocalization.Get("notifications.downloads.readyCount", status.Ready, status.Total));
        }
        string waiting = DescribeWaiting(status, BasisPredownloadProtocol.MaxNamedPeers);
        if (waiting != null)
        {
            lines.Add(waiting);
        }
        string failed = PlayerNames(status.FailedPeers, status.Failed, BasisPredownloadProtocol.MaxNamedPeers);
        if (failed != null)
        {
            lines.Add(BasisLocalization.Get("notifications.downloads.failedFor", failed));
        }
        string skipped = PlayerNames(status.SkippedPeers, status.Skipped, BasisPredownloadProtocol.MaxNamedPeers);
        if (skipped != null)
        {
            lines.Add(BasisLocalization.Get("notifications.downloads.notResponding", skipped));
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
    private static bool LocalIsAdmin() => BasisNetworkManagement.LocalPermissions.Contains(PermNodes.protection);
    private static void SyncDownloadEntry(PredownloadStatus status)
    {
        if (!IsInProgress(status))
        {
            if (status.Entry != null)
            {
                BasisDownloadCenter.End(status.Entry);
                status.Entry = null;
            }
            return;
        }
        if (status.Entry == null)
        {
            string netId = status.LoadedNetID;
            status.Entry = BasisDownloadCenter.Begin(status.Mode == 1 ? BasisDownloadKind.World : BasisDownloadKind.Prop, status.Url, null, null);
            status.Entry.CancelAction = () => RequestCancel(netId);
            status.Entry.CancelAllowed = () => CanCancel(status, LocalIsAdmin());
        }
        BasisDownload entry = status.Entry;
        entry.DisplayName = status.Name;
        if (status.Local == PredownloadLocalState.Downloading || status.Local == PredownloadLocalState.Waiting)
        {
            entry.Progress = Mathf.Clamp(status.LocalProgress, 0f, 99f);
            entry.Stage = string.IsNullOrEmpty(status.LocalStage) ? BasisLocalization.Get("library.instantiated.predownload.downloading", Mathf.RoundToInt(status.LocalProgress)) : status.LocalStage;
        }
        else
        {
            entry.Progress = status.Total > 0 ? Mathf.Clamp(100f * (status.Ready + status.Failed) / status.Total, 0f, 99f) : 0f;
            entry.Stage = DescribeWaiting(status, 3) ?? BasisLocalization.Get(status.Local == PredownloadLocalState.Failed ? "library.instantiated.predownload.failedWaiting" : "library.instantiated.predownload.waiting", status.Left, status.Total);
        }
        entry.Details = DescribeDetails(status);
    }
    private static void CancelUnusedDownload(string key)
    {
        if (string.IsNullOrEmpty(key) || !inFlight.TryGetValue(key, out SharedDownload shared))
        {
            return;
        }
        foreach (PreloadedResource preloaded in PreloadedResources.Values)
        {
            if (preloaded.Downloading && DownloadKey(preloaded.LoadResource.CombinedURL) == key)
            {
                return;
            }
        }
        foreach (PredownloadStatus status in predownloads.Values)
        {
            if (status.DownloadKey == key && status.Local == PredownloadLocalState.Downloading)
            {
                return;
            }
        }
        try
        {
            shared.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
    public static bool CanCancel(PredownloadStatus status, bool isAdmin)
    {
        if (status == null || status.Closed)
        {
            return false;
        }
        return isAdmin || (!string.IsNullOrEmpty(status.Creator) && BasisLocalPlayer.Instance != null && status.Creator == BasisLocalPlayer.Instance.UUID);
    }
    public static void RequestCancel(string netId)
    {
        if (string.IsNullOrEmpty(netId))
        {
            return;
        }
        BasisClientMessageRegistry.Send(BasisPredownloadProtocol.MessageName, netId, static (writer, id) =>
        {
            writer.Put(BasisPredownloadProtocol.OpCancel);
            writer.Put(id);
        });
    }
    private static void Cancelled(string netId)
    {
        string key = predownloads.TryGetValue(netId, out PredownloadStatus status) ? status.DownloadKey : null;
        if (PreloadedResources.TryRemove(netId, out PreloadedResource preloaded))
        {
            key ??= DownloadKey(preloaded.LoadResource.CombinedURL);
        }
        ClosePredownload(netId);
        CancelUnusedDownload(key);
        BasisDebug.Log($"PreloadManager: {netId} was cancelled", BasisDebug.LogTag.Networking);
    }

    /// <summary>
    /// Called when the client receives a LoadResource message with LoadStrategy = 2 (Synchronized).
    /// Downloads content while sending status to the server, then reports readiness. Spawning happens
    /// when the server sends a SpawnPreloaded signal, or right after the download if that signal came first.
    /// </summary>
    public static async Task HandleSynchronizedPreload(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;
        CancellationToken session = _cts.Token;
        PredownloadStatus status = TrackPredownload(netId);
        status.Url = resource.CombinedURL;
        status.DownloadKey = DownloadKey(resource.CombinedURL);
        status.Creator = resource.UUIDOfCreator;
        status.Synchronized = true;
        status.Mode = resource.Mode;
        status.Local = PredownloadLocalState.Downloading;
        status.LocalProgress = 0f;
        if (CachedMetaData.TryGetMeta(resource.CombinedURL, out CachedMetaData.CachedContent cached)) status.Name = CleanName(cached?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName);
        PublishPredownload(status);
        BasisDebug.Log($"PreloadManager: Beginning synchronized preload for {resource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);
        Task download = HandlePreload(resource);
        _ = SendStatusWhileDownloading(netId, session);
        await download;
        if (session.IsCancellationRequested || !PreloadedResources.TryGetValue(netId, out PreloadedResource preloaded)) return;
        bool isReady = preloaded.IsReady;
        SendReadyMessage(netId, isReady);
        if (!isReady)
        {
            BasisDebug.LogError($"PreloadManager: Synchronized preload failed for {resource.CombinedURL} (NetID={netId}), reported failure to server");
        }
        else
        {
            BasisDebug.Log($"PreloadManager: Synchronized preload ready for {resource.CombinedURL} (NetID={netId}), reported ready to server");
        }
        if (predownloads.TryGetValue(netId, out PredownloadStatus current) && current == status)
        {
            string name = CleanName(preloaded.BundleWrapper?.LoadableBundle?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName);
            if (!string.IsNullOrEmpty(name)) status.Name = name;
            status.Local = isReady ? PredownloadLocalState.Ready : PredownloadLocalState.Failed;
            PublishPredownload(status);
        }
        if (preloaded.SpawnRequested)
        {
            BasisDebug.Log($"PreloadManager: Everyone else already loaded {netId}, spawning it now", BasisDebug.LogTag.Networking);
            await HandleSpawnPreloaded(new SpawnPreloadedMessage { LoadedNetID = netId, Swap = preloaded.Swap });
        }
    }
    public const int StatusIntervalMilliseconds = 3000;
    public static byte NextSynchronizedStrategy = BasisPredownloadProtocol.StrategySynchronized;
    private static readonly HashSet<string> clearLocalOnSpawn = new HashSet<string>();
    public static byte TakeSynchronizedStrategy()
    {
        byte strategy = NextSynchronizedStrategy;
        NextSynchronizedStrategy = BasisPredownloadProtocol.StrategySynchronized;
        return strategy;
    }
    public static void ClearLocalContentOnSpawn(string netId) => clearLocalOnSpawn.Add(netId);
    private static async Task RemoveLocalWorldsAndProps()
    {
        List<string> local = new List<string>();
        foreach (BasisRuntimeSpawnRegistry.SpawnInstance instance in BasisRuntimeSpawnRegistry.GetAll())
        {
            if (instance != null && instance.SpawnMethod != BasisRuntimeSpawnRegistry.SpawnMethod.Network && (instance.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene || instance.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.GameObject)) local.Add(instance.LoadedNetID);
        }
        for (int index = 0; index < local.Count; index++) await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(local[index]);
    }
    private static async Task SendStatusWhileDownloading(string netId, CancellationToken session)
    {
        try
        {
            while (!session.IsCancellationRequested && PreloadedResources.TryGetValue(netId, out PreloadedResource preloaded) && preloaded.Downloading)
            {
                BasisClientMessageRegistry.Send(BasisPredownloadProtocol.MessageName, netId, static (writer, id) =>
                {
                    writer.Put(BasisPredownloadProtocol.OpStatus);
                    writer.Put(id);
                });
                await Task.Delay(StatusIntervalMilliseconds, session);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
    public static bool Forget(string netId)
    {
        if (!PreloadedResources.TryRemove(netId, out PreloadedResource preloaded)) return false;
        ClosePredownload(netId);
        CancelUnusedDownload(DownloadKey(preloaded.LoadResource.CombinedURL));
        return true;
    }

    /// <summary>
    /// Called when the client receives a LoadResource message with LoadStrategy = 3 (Predownload).
    /// Downloads the content to disc so a later normal load is instant, but never spawns it.
    /// Reports the outcome to the server, which shares how many players are ready with everyone.
    /// </summary>
    public static async Task HandlePredownload(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;
        CancellationToken session = _cts.Token;
        PredownloadStatus status = TrackPredownload(netId);
        status.Url = resource.CombinedURL;
        status.DownloadKey = DownloadKey(resource.CombinedURL);
        status.Creator = resource.UUIDOfCreator;
        status.Mode = resource.Mode;
        status.Local = PredownloadLocalState.Downloading;
        status.LocalProgress = 0f;
        if (CachedMetaData.TryGetMeta(resource.CombinedURL, out CachedMetaData.CachedContent cached)) status.Name = CleanName(cached?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName);
        PublishPredownload(status);
        BasisDebug.Log($"PreloadManager: Beginning predownload for {resource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);
        BasisTrackedBundleWrapper wrapper = await DownloadShared(resource);
        bool ready = wrapper != null;
        if (session.IsCancellationRequested) return;
        BasisClientMessageRegistry.Send(BasisPredownloadProtocol.MessageName, (netId, ready), static (writer, state) =>
        {
            writer.Put(BasisPredownloadProtocol.OpDone);
            writer.Put(state.netId);
            writer.Put(state.ready);
        });
        if (!predownloads.TryGetValue(netId, out PredownloadStatus current) || current != status) return;
        string name = CleanName(wrapper?.LoadableBundle?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName);
        if (!string.IsNullOrEmpty(name)) status.Name = name;
        status.Local = ready ? PredownloadLocalState.Ready : PredownloadLocalState.Failed;
        PublishPredownload(status);
    }
    public enum PredownloadLocalState : byte
    {
        Waiting,
        Downloading,
        Ready,
        Failed,
    }
    public sealed class PredownloadStatus
    {
        public string LoadedNetID, Url, Name, Creator, DownloadKey;
        public byte Mode;
        public ushort Requester;
        public int Ready, Failed, Total, Skipped;
        public float LocalProgress;
        public string LocalStage;
        public ushort[] WaitingPeers, FailedPeers, SkippedPeers;
        public BasisDownload Entry;
        public bool HasCounts, Announced, Closed, Synchronized;
        public PredownloadLocalState Local;
        public int Left => Math.Max(0, Total - Ready - Failed);
    }
    public const float PredownloadHudSeconds = 60f;
    public const int PredownloadNameLength = 40;
    public static event Action<PredownloadStatus> OnPredownloadChanged;
    private static readonly Dictionary<string, PredownloadStatus> predownloads = new Dictionary<string, PredownloadStatus>();
    public static IEnumerable<PredownloadStatus> GetPredownloads() => predownloads.Values;
    public static void Initialize() => BasisClientMessageRegistry.RegisterClientPlugin(BasisPredownloadProtocol.MessageName, HandlePredownloadMessage);
    public static bool TryGetPredownloadForUrl(string url, out PredownloadStatus status)
    {
        status = null;
        if (string.IsNullOrEmpty(url)) return false;
        foreach (PredownloadStatus candidate in predownloads.Values)
        {
            if (candidate.Url == url) status = candidate;
        }
        return status != null;
    }
    public static string DescribePredownload(PredownloadStatus status)
    {
        string name = string.IsNullOrEmpty(status.Name) ? BasisLocalization.Get("library.downloadForEveryone") : status.Name;
        if (!status.HasCounts) return name;
        if (status.Left > 0) return BasisLocalization.Get("library.predownload.progress", name, status.Ready, status.Total, status.Left);
        if (status.Failed > 0) return BasisLocalization.Get("library.predownload.failed", name, status.Ready, status.Total, status.Failed);
        return status.Skipped > 0 ? BasisLocalization.Get("library.predownload.skipped", name, status.Ready, status.Skipped) : BasisLocalization.Get("library.predownload.done", name, status.Total);
    }
    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Replace("<", string.Empty).Replace(">", string.Empty).Trim();
        return name.Length > PredownloadNameLength ? name.Substring(0, PredownloadNameLength) : name;
    }
    private static PredownloadStatus TrackPredownload(string netId)
    {
        if (!predownloads.TryGetValue(netId, out PredownloadStatus status))
        {
            status = new PredownloadStatus { LoadedNetID = netId };
            predownloads[netId] = status;
        }
        return status;
    }
    private static void HandlePredownloadMessage(NetPeer sender, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            if (!reader.TryGetByte(out byte op) || !reader.TryGetString(out string netId) || string.IsNullOrEmpty(netId)) return;
            if (op == BasisPredownloadProtocol.OpCancel)
            {
                BasisDeviceManagement.EnqueueOnMainThread(() => Cancelled(netId));
                return;
            }
            if (op == BasisPredownloadProtocol.OpClosed)
            {
                BasisDeviceManagement.EnqueueOnMainThread(() => ClosePredownload(netId));
                return;
            }
            if (op != BasisPredownloadProtocol.OpProgress || !reader.TryGetUShort(out ushort requester) || !reader.TryGetUShort(out ushort ready) || !reader.TryGetUShort(out ushort failed) || !reader.TryGetUShort(out ushort total)) return;
            if (!reader.TryGetUShort(out ushort skipped)) skipped = 0;
            ushort[] waitingPeers = ReadPeers(reader), failedPeers = ReadPeers(reader), skippedPeers = ReadPeers(reader);
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                PredownloadStatus status = TrackPredownload(netId);
                status.WaitingPeers = waitingPeers;
                status.FailedPeers = failedPeers;
                status.SkippedPeers = skippedPeers;
                status.Skipped = skipped;
                status.Requester = requester;
                status.Ready = ready;
                status.Failed = failed;
                status.Total = total;
                status.HasCounts = true;
                PublishPredownload(status);
            });
        }
        finally
        {
            reader.Recycle();
        }
    }
    private static ushort[] ReadPeers(NetPacketReader reader)
    {
        if (!reader.TryGetByte(out byte count) || count == 0)
        {
            return null;
        }
        ushort[] peers = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            if (!reader.TryGetUShort(out peers[index]))
            {
                return null;
            }
        }
        return peers;
    }
    private static void ClosePredownload(string netId)
    {
        if (!predownloads.Remove(netId, out PredownloadStatus status)) return;
        status.Closed = true;
        SyncDownloadEntry(status);
        BasisUILoadingBar.RemoveDisplay(PredownloadHudKey(netId));
        OnPredownloadChanged?.Invoke(status);
    }
    private static string PredownloadHudKey(string netId) => "predownload." + netId;
    private static void PublishPredownload(PredownloadStatus status)
    {
        SyncDownloadEntry(status);
        OnPredownloadChanged?.Invoke(status);
        if (!status.HasCounts) return;
        if (status.Total <= 0)
        {
            BasisUILoadingBar.RemoveDisplay(PredownloadHudKey(status.LoadedNetID));
            return;
        }
        if (status.Left > 0)
        {
            status.Announced = false;
            BasisUILoadingBar.ProgressReportTransient(PredownloadHudKey(status.LoadedNetID), Mathf.Min(99f, 100f * (status.Ready + status.Failed) / status.Total), DescribePredownload(status), PredownloadHudSeconds);
            return;
        }
        BasisUILoadingBar.RemoveDisplay(PredownloadHudKey(status.LoadedNetID));
        if (status.Announced) return;
        status.Announced = true;
        BasisJoinLeaveNotification.ShowNotification(DescribePredownload(status), status.Failed > 0 ? new Color(1f, 0.6f, 0.2f) : new Color(0.5f, 1f, 0.5f));
    }

    /// <summary>
    /// Sends a readiness message to the server indicating whether this client
    /// has successfully preloaded the resource.
    /// </summary>
    private static void SendReadyMessage(string loadedNetId, bool isReady)
    {
        PreloadReadyMessage readyMsg = new PreloadReadyMessage
        {
            LoadedNetID = loadedNetId,
            IsReady = isReady,
        };

        NetDataWriter writer = new NetDataWriter();
        readyMsg.Serialize(writer);

        BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.PreloadReadyChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Called when the server signals that a preloaded resource should now be spawned.
    /// Unloads all existing scene-type content first, then spawns the new resource.
    /// Done client-side in one step so unload and spawn can't race.
    /// </summary>
    public static async Task HandleSpawnPreloaded(SpawnPreloadedMessage spawnMsg)
    {
        string netId = spawnMsg.LoadedNetID;

        if (!PreloadedResources.TryGetValue(netId, out PreloadedResource preloaded))
        {
            BasisDebug.LogError($"PreloadManager: Received spawn signal for {netId} but no preloaded resource found");
            return;
        }
        if (preloaded.Downloading)
        {
            preloaded.SpawnRequested = true;
            preloaded.Swap = spawnMsg.Swap;
            BasisDebug.Log($"PreloadManager: Spawn signal for {netId} arrived while still downloading, it will spawn when the download finishes", BasisDebug.LogTag.Networking);
            return;
        }
        PreloadedResources.TryRemove(netId, out _);

        if (!preloaded.IsReady || preloaded.BundleWrapper == null)
        {
            BasisDebug.LogError($"PreloadManager: Received spawn signal for {netId} but resource was not ready");
            return;
        }

        // Only unload existing scene content when the synchronized resource is a scene.
        // Props (Mode == 0) should never cause scene unloads.
        if (clearLocalOnSpawn.Remove(netId) && spawnMsg.Swap == BasisPredownloadProtocol.SwapClearAll)
        {
            await RemoveLocalWorldsAndProps();
        }
        if (preloaded.LoadResource.Mode == 1 && spawnMsg.Swap != BasisPredownloadProtocol.SwapKeepAll)
        {
            await UnloadAllSceneContent();
        }

        BasisDebug.Log($"PreloadManager: Spawning preloaded resource {preloaded.LoadResource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);

        // Now spawn using the existing spawn infrastructure
        // The LoadStrategy is set back to 0 (Immediate) so SpawnGameObject/SpawnScene
        // treats it as a normal load
        LocalLoadResource spawnResource = preloaded.LoadResource;
        spawnResource.LoadStrategy = 0;

        switch (spawnResource.Mode)
        {
            case 0: // GameObject
                await BasisNetworkSpawnItem.SpawnGameObject(spawnResource, Selector.Prop);
                break;
            case 1: // Scene
                await BasisNetworkSpawnItem.SpawnScene(spawnResource);
                break;
            default:
                BasisDebug.LogError($"PreloadManager: Unknown mode {spawnResource.Mode} for spawn");
                break;
        }
    }

    /// <summary>
    /// Unloads all scene-type content from the local spawn registry.
    /// Called before a synchronized spawn so the new content replaces existing scenes.
    /// </summary>
    private static async Task UnloadAllSceneContent()
    {
        var allInstances = BasisRuntimeSpawnRegistry.GetAll();
        var sceneInstances = new System.Collections.Generic.List<BasisRuntimeSpawnRegistry.SpawnInstance>();

        foreach (var instance in allInstances)
        {
            if (instance.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene)
            {
                sceneInstances.Add(instance);
            }
        }

        if (sceneInstances.Count == 0) return;

        BasisDebug.Log($"PreloadManager: Unloading {sceneInstances.Count} existing scene(s) before synchronized spawn", BasisDebug.LogTag.Networking);

        foreach (var scene in sceneInstances)
        {
            await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(scene.LoadedNetID);
        }
    }

    /// <summary>
    /// Cleans up all preloaded resources. Called on disconnect or reset.
    /// </summary>
    public static void Reset()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        PreloadedResources.Clear();
        inFlight.Clear();
        clearLocalOnSpawn.Clear();
        NextSynchronizedStrategy = BasisPredownloadProtocol.StrategySynchronized;
        foreach (PredownloadStatus status in predownloads.Values)
        {
            BasisUILoadingBar.RemoveDisplay(PredownloadHudKey(status.LoadedNetID));
            BasisDownloadCenter.End(status.Entry);
            status.Entry = null;
        }
        predownloads.Clear();
    }
}
