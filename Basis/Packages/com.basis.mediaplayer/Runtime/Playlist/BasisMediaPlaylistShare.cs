using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.BasisUI;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisMediaPlaylistShare
{
    private const string FixedNetworkIdentifier = "BasisMediaPlaylist";
    private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Video;
    private const float DropConfirmSeconds = 5f;
    private const int PrefetchAhead = 2;
    // Disabled: needs more work before shipping (never run in Unity or with a peer). Also re-add the EmbeddedItemsCatalog entry.
    public static readonly bool Enabled = false;

    private struct PendingDrop
    {
        public BasisMediaPlaylist Playlist;
        public float Time;
    }

    private static readonly Dictionary<Guid, BasisMediaPlaylist> playlists = new Dictionary<Guid, BasisMediaPlaylist>();
    private static readonly Dictionary<Guid, PendingDrop> pendingDrops = new Dictionary<Guid, PendingDrop>();
    private static readonly List<Guid> scratchEntries = new List<Guid>();
    private static readonly List<Guid> scratchIds = new List<Guid>();
    private static readonly List<BasisMediaPlaylist> scratchPlaylists = new List<BasisMediaPlaylist>();
    private static readonly HashSet<Guid> wantedScratch = new HashSet<Guid>();
    private static bool initialized;
    private static bool hasNetworkId;
    private static ushort networkId;
    private static int connectionGeneration;
    private static int resolveGeneration = -1;
    private static bool wantedDirty;
    private static BasisMediaPlaylist hovered;

    public static IReadOnlyDictionary<Guid, BasisMediaPlaylist> Playlists => playlists;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (!Enabled || initialized) return;
        initialized = true;
        BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerJoined += HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
        BasisNetworkPlayer.OnPlayerLeft += HandlePlayerLeft;
        BasisMediaClipShare.OnClipRemoved -= HandleClipRemoved;
        BasisMediaClipShare.OnClipRemoved += HandleClipRemoved;
        BasisEventDriver.OnUpdate -= Tick;
        BasisEventDriver.OnUpdate += Tick;
        Application.quitting -= Shutdown;
        Application.quitting += Shutdown;
        if (BasisNetworkConnection.LocalPlayerIsConnected) HandleLocalPlayerJoined(null, null);
    }

    private static void Shutdown()
    {
        Application.quitting -= Shutdown;
        BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
        BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
        BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
        BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
        BasisMediaClipShare.OnClipRemoved -= HandleClipRemoved;
        BasisEventDriver.OnUpdate -= Tick;
        ReleaseNetworkIdentity();
        pendingDrops.Clear();
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
            BasisDebug.LogError("[BasisMedia] playlists could not resolve their network identifier.", LogTag);
            return;
        }
        networkId = resolution.Id;
        hasNetworkId = true;
        BasisNetworkGenericMessages.RegisterDirectHandler(networkId, OnDirectNetworkMessage);
        Send(BasisMediaPlaylistProtocol.HelloBytes, DeliveryMethod.ReliableOrdered, null);
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && playlist.IsHost) SendState(playlist, null);
        }
    }

    private static void HandleLocalPlayerLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
    {
        ReleaseNetworkIdentity();
        DestroyRemotes(_ => true);
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

    private static void HandlePlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || !hasNetworkId) return;
        if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) && player.playerId == localId) return;
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && playlist.IsHost) SendState(playlist, new[] { player.playerId });
        }
    }

    private static void HandlePlayerLeft(BasisNetworkPlayer player)
    {
        if (player == null) return;
        ushort left = player.playerId;
        DestroyRemotes(p => p.HostId == left);
    }

    private static void DestroyRemotes(Predicate<BasisMediaPlaylist> match)
    {
        scratchPlaylists.Clear();
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && !playlist.IsHost && match(playlist)) scratchPlaylists.Add(playlist);
        }
        for (int i = 0; i < scratchPlaylists.Count; i++)
        {
            BasisMediaPlaylist playlist = scratchPlaylists[i];
            Unregister(playlist);
            UnityEngine.Object.Destroy(playlist.gameObject);
        }
        scratchPlaylists.Clear();
    }

    public static bool IsConnected => hasNetworkId;

    public static void Register(BasisMediaPlaylist playlist)
    {
        if (playlist == null || playlist.Id == Guid.Empty) return;
        playlists[playlist.Id] = playlist;
        wantedDirty = true;
        if (playlist.IsHost && hasNetworkId) SendState(playlist, null);
    }

    public static void Unregister(BasisMediaPlaylist playlist)
    {
        if (playlist == null || !playlists.TryGetValue(playlist.Id, out BasisMediaPlaylist known) || known != playlist) return;
        playlists.Remove(playlist.Id);
        if (hovered == playlist) hovered = null;
        if (playlist.IsHost && hasNetworkId) Send(BasisMediaPlaylistProtocol.EncodeClose(playlist.Id), DeliveryMethod.ReliableOrdered, null);
        IReadOnlyList<Guid> entries = playlist.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!IsInAnyPlaylist(entries[i], null)) BasisMediaClipShare.DiscardPlaylisted(entries[i]);
        }
        wantedDirty = true;
    }

    public static bool IsInAnyPlaylist(Guid clip, BasisMediaPlaylist except)
    {
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && playlist != except && playlist.Contains(clip)) return true;
        }
        return false;
    }

    public static void EntryAdded(BasisMediaPlaylist playlist, Guid clip)
    {
        pendingDrops.Remove(clip);
        playlist.GetEjectPose(out Vector3 position, out Quaternion rotation);
        BasisMediaClipShare.SetPlaylisted(clip, true, position, rotation);
        wantedDirty = true;
    }

    public static void EntryRemoved(BasisMediaPlaylist playlist, Guid clip)
    {
        wantedDirty = true;
        if (IsInAnyPlaylist(clip, playlist) || pendingDrops.ContainsKey(clip)) return;
        playlist.GetEjectPose(out Vector3 position, out Quaternion rotation);
        BasisMediaClipShare.SetPlaylisted(clip, false, position, rotation);
    }

    public static void CurrentChanged() => wantedDirty = true;

    public static void SendState(BasisMediaPlaylist playlist, ushort[] recipients)
    {
        if (!hasNetworkId || playlist == null || !playlist.IsHost) return;
        Send(BasisMediaPlaylistProtocol.EncodeState(playlist.BuildState(), playlist.Entries), DeliveryMethod.ReliableOrdered, recipients);
    }

    public static void SendRequest(BasisMediaPlaylist playlist, BasisMediaPlaylistAction action, int index, Guid clip)
    {
        if (playlist == null) return;
        if (playlist.IsHost)
        {
            ushort self = BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) ? localId : (ushort)0;
            playlist.HandleRequest(self, action, index, clip);
            return;
        }
        if (!hasNetworkId) return;
        Send(BasisMediaPlaylistProtocol.EncodeRequest(playlist.Id, action, index, clip), DeliveryMethod.ReliableOrdered, new[] { playlist.HostId });
    }

    public static void SendPose(BasisMediaPlaylist playlist, bool reliable)
    {
        if (!hasNetworkId || playlist == null) return;
        Transform t = playlist.transform;
        float scale = Mathf.Clamp(t.localScale.x, BasisMediaClipProtocol.MinScale, BasisMediaClipProtocol.MaxScale);
        Send(BasisMediaPlaylistProtocol.EncodePose(playlist.Id, t.position, t.rotation, scale), reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced, null);
    }

    private static void Send(byte[] buffer, DeliveryMethod method, ushort[] recipients)
    {
        if (!hasNetworkId) return;
        BasisNetworkGenericMessages.OnNetworkMessageSendDirect(networkId, buffer, method, recipients);
    }

    private static void OnDirectNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length == 0) return;
        if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) && senderId == localId) return;
        try
        {
            switch (buffer[0])
            {
                case BasisMediaPlaylistProtocol.OpState: HandleState(senderId, buffer); break;
                case BasisMediaPlaylistProtocol.OpRequest: HandleRequest(senderId, buffer); break;
                case BasisMediaPlaylistProtocol.OpPose: HandlePose(buffer); break;
                case BasisMediaPlaylistProtocol.OpClose: HandleClose(senderId, buffer); break;
                case BasisMediaPlaylistProtocol.OpHello: HandleHello(senderId); break;
            }
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"[BasisMedia] playlist message {buffer[0]} from {senderId} failed: {exception.Message}", LogTag);
        }
    }

    private static void HandleState(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaPlaylistProtocol.TryDecodeState(buffer, out BasisMediaPlaylistState state, scratchEntries)) return;
        if (playlists.TryGetValue(state.Id, out BasisMediaPlaylist playlist) && playlist != null)
        {
            if (playlist.IsHost || playlist.HostId != senderId) return;
        }
        else
        {
            if (BasisNetworkModeration.MediaPlayerBlockedLocally) return;
            playlist = BasisMediaPlaylist.CreateRemote(state.Id, senderId, state.Position, state.Rotation, state.Scale);
            playlists[state.Id] = playlist;
        }
        playlist.ApplyState(state, scratchEntries);
        scratchEntries.Clear();
    }

    private static void HandleRequest(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaPlaylistProtocol.TryDecodeRequest(buffer, out Guid id, out BasisMediaPlaylistAction action, out int index, out Guid clip)) return;
        if (!playlists.TryGetValue(id, out BasisMediaPlaylist playlist) || playlist == null || !playlist.IsHost) return;
        playlist.HandleRequest(senderId, action, index, clip);
    }

    private static void HandlePose(byte[] buffer)
    {
        if (!BasisMediaPlaylistProtocol.TryDecodePose(buffer, out Guid id, out Vector3 position, out Quaternion rotation, out float scale)) return;
        if (playlists.TryGetValue(id, out BasisMediaPlaylist playlist) && playlist != null) playlist.SetRemoteTarget(position, rotation, scale);
    }

    private static void HandleClose(ushort senderId, byte[] buffer)
    {
        if (!BasisMediaPlaylistProtocol.TryDecodeClose(buffer, out Guid id)) return;
        if (!playlists.TryGetValue(id, out BasisMediaPlaylist playlist) || playlist == null || playlist.IsHost || playlist.HostId != senderId) return;
        Unregister(playlist);
        UnityEngine.Object.Destroy(playlist.gameObject);
    }

    private static void HandleHello(ushort senderId)
    {
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && playlist.IsHost) SendState(playlist, new[] { senderId });
        }
    }

    private static void HandleClipRemoved(Guid clip)
    {
        pendingDrops.Remove(clip);
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist != null && playlist.IsHost && playlist.Contains(clip)) playlist.HandleRequest(0, BasisMediaPlaylistAction.Remove, -1, clip);
        }
        wantedDirty = true;
    }

    public static void UpdateHover(BasisMediaClipObject clip)
    {
        if (!Enabled) return;
        BasisMediaPlaylist target = FindDropTarget(clip);
        if (target == hovered) return;
        if (hovered != null) hovered.SetDropHover(false);
        hovered = target;
        if (hovered != null) hovered.SetDropHover(true);
    }

    public static bool TryDrop(BasisMediaClipObject clip)
    {
        if (!Enabled) return false;
        if (hovered != null)
        {
            hovered.SetDropHover(false);
            hovered = null;
        }
        BasisMediaPlaylist target = FindDropTarget(clip);
        if (target == null) return false;
        Guid id = clip.ClipId;
        if (target.Contains(id) || IsInAnyPlaylist(id, null)) return false;
        if (target.Entries.Count >= BasisMediaPlaylistProtocol.MaxEntries)
        {
            target.FlashNotice(BasisLocalization.Get("mediaPlaylist.full", BasisMediaPlaylistProtocol.MaxEntries));
            return false;
        }
        pendingDrops[id] = new PendingDrop { Playlist = target, Time = Time.realtimeSinceStartup };
        target.GetEjectPose(out Vector3 position, out Quaternion rotation);
        BasisMediaClipShare.SetPlaylisted(id, true, position, rotation);
        SendRequest(target, BasisMediaPlaylistAction.Insert, -1, id);
        return true;
    }

    private static BasisMediaPlaylist FindDropTarget(BasisMediaClipObject clip)
    {
        if (clip == null) return null;
        Bounds bounds = clip.DropBounds;
        BasisMediaPlaylist best = null;
        float bestDistance = float.MaxValue;
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist == null || !playlist.TryGetSlotBounds(out Bounds slot) || !slot.Intersects(bounds)) continue;
            float distance = (slot.center - bounds.center).sqrMagnitude;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = playlist;
        }
        return best;
    }

    private static void Tick()
    {
        if (!initialized) return;
        if (!hasNetworkId) EnsureNetworkIdentity();
        if (pendingDrops.Count > 0)
        {
            float now = Time.realtimeSinceStartup;
            scratchIds.Clear();
            foreach (KeyValuePair<Guid, PendingDrop> pair in pendingDrops)
            {
                if (now - pair.Value.Time > DropConfirmSeconds) scratchIds.Add(pair.Key);
            }
            for (int i = 0; i < scratchIds.Count; i++)
            {
                Guid id = scratchIds[i];
                BasisMediaPlaylist target = pendingDrops[id].Playlist;
                pendingDrops.Remove(id);
                if (IsInAnyPlaylist(id, null)) continue;
                Vector3 position = Vector3.zero;
                Quaternion rotation = Quaternion.identity;
                if (target != null) target.GetEjectPose(out position, out rotation);
                BasisMediaClipShare.SetPlaylisted(id, false, position, rotation);
            }
            scratchIds.Clear();
        }
        if (!wantedDirty) return;
        wantedDirty = false;
        wantedScratch.Clear();
        foreach (BasisMediaPlaylist playlist in playlists.Values)
        {
            if (playlist == null) continue;
            IReadOnlyList<Guid> entries = playlist.Entries;
            int count = entries.Count;
            if (count == 0) continue;
            int start = Mathf.Max(0, playlist.Current);
            for (int i = 0; i <= PrefetchAhead && i < count; i++)
            {
                int index = start + i;
                if (index >= count)
                {
                    if (!playlist.Repeat) break;
                    index -= count;
                }
                wantedScratch.Add(entries[index]);
            }
        }
        BasisMediaClipShare.SetWanted(wantedScratch);
    }
}
