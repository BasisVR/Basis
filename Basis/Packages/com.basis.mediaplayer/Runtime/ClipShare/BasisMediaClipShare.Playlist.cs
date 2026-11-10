using System;
using System.Collections.Generic;
using System.IO;
using Basis.BasisUI;
using UnityEngine;

public static partial class BasisMediaClipShare
{
    public const int MaxPlaylistedPerSender = BasisMediaPlaylistProtocol.MaxEntries;
    private const int MaxParkedOffers = 2048;
    private const float ParkedOfferSeconds = 180f;
    private const float PullStallSeconds = 0.75f;
    private const float LinkTickSeconds = 1f;

    private struct ParkedOffer
    {
        public ushort Sender;
        public byte[] Buffer;
        public bool FromServer;
        public float Time;
    }

    private static readonly HashSet<Guid> playlisted = new HashSet<Guid>();
    private static readonly HashSet<Guid> wanted = new HashSet<Guid>();
    private static readonly HashSet<Guid> pendingActivation = new HashSet<Guid>();
    private static readonly Dictionary<Guid, ParkedOffer> parked = new Dictionary<Guid, ParkedOffer>();
    private static readonly List<Guid> linkScratch = new List<Guid>();
    private static float linkTimer;

    public static event Action<Guid> OnClipRemoved;

    public static bool HasClip(Guid id) => clips.ContainsKey(id) || parked.ContainsKey(id);

    public static bool IsPlaylisted(Guid id) => playlisted.Contains(id);

    public static bool TryGetClipInfo(Guid id, out string name, out bool audio, out string sharer, out float progress, out bool failed)
    {
        if (clips.TryGetValue(id, out Clip clip))
        {
            name = clip.Name;
            audio = BasisMediaClipProtocol.IsAudio(clip.Container);
            sharer = clip.SharerName;
            failed = clip.Failed;
            progress = clip.Complete || clip.IsLocalSharer ? 1f : clip.Deferred ? -1f : clip.TotalChunks > 0 ? (float)clip.ReceivedCount / clip.TotalChunks : 0f;
            return true;
        }
        name = string.Empty;
        audio = false;
        sharer = string.Empty;
        failed = false;
        progress = -1f;
        if (!parked.TryGetValue(id, out ParkedOffer offer)) return false;
        if (BasisMediaClipProtocol.TryDecodeOffer(offer.Buffer, out BasisMediaClipOffer decoded, out _))
        {
            name = decoded.Name;
            audio = BasisMediaClipProtocol.IsAudio(decoded.Container);
        }
        return true;
    }

    public static bool TryGetClipSharer(Guid id, out ushort sharerId, out bool isLocal)
    {
        if (clips.TryGetValue(id, out Clip clip))
        {
            sharerId = clip.SharerId;
            isLocal = clip.IsLocalSharer;
            return true;
        }
        sharerId = 0;
        isLocal = false;
        if (!parked.TryGetValue(id, out ParkedOffer offer)) return false;
        sharerId = offer.Sender;
        return true;
    }

    public static void SetPlaylisted(Guid id, bool listed, Vector3 ejectPosition, Quaternion ejectRotation)
    {
        if (listed)
        {
            if (!playlisted.Add(id)) return;
            if (parked.Remove(id, out ParkedOffer offer)) HandleOffer(offer.Sender, offer.Buffer, offer.FromServer);
            if (!clips.TryGetValue(id, out Clip clip)) return;
            clip.UrlAnnounced = true;
            DestroyView(clip);
            if (clip.IsLocalSharer || clip.Complete || clip.Failed) return;
            clip.Pulled = true;
            if (clip.ReceivedCount > 0 || clip.Storage == null || wanted.Contains(id)) return;
            string partPath = clip.FilePath;
            ReleaseInbound(clip);
            if (!string.IsNullOrEmpty(partPath)) TryDeleteFile(partPath);
            clip.FilePath = null;
            clip.Deferred = true;
            return;
        }
        if (!playlisted.Remove(id)) return;
        wanted.Remove(id);
        if (!clips.TryGetValue(id, out Clip ejected) || ejected.View != null) return;
        if (ejected.Deferred && !Activate(ejected)) pendingActivation.Add(id);
        if (ejected.IsLocalSharer) ejected.UrlAnnounced = false;
        ejected.CreatedTime = Time.realtimeSinceStartup;
        BuildView(ejected, ejectPosition, ejectRotation, 1f);
    }

    public static void DiscardPlaylisted(Guid id)
    {
        if (!playlisted.Remove(id)) return;
        parked.Remove(id);
        RemoveClip(id);
    }

    public static void SetWanted(HashSet<Guid> ids)
    {
        wanted.Clear();
        foreach (Guid id in ids)
        {
            if (!playlisted.Contains(id)) continue;
            wanted.Add(id);
            if (clips.TryGetValue(id, out Clip clip) && clip.Deferred && !Activate(clip)) pendingActivation.Add(id);
        }
    }

    private static int CountFromSender(ushort senderId, bool listed)
    {
        int count = 0;
        foreach (Clip clip in clips.Values)
        {
            if (clip.SharerId == senderId && playlisted.Contains(clip.Id) == listed) count++;
        }
        return count;
    }

    private static bool TryPark(ushort senderId, byte[] buffer, bool fromServer, Guid id)
    {
        if (parked.ContainsKey(id)) return true;
        if (parked.Count >= MaxParkedOffers) return false;
        parked[id] = new ParkedOffer { Sender = senderId, Buffer = buffer, FromServer = fromServer, Time = Time.realtimeSinceStartup };
        return true;
    }

    private static void DropParkedFrom(ushort senderId)
    {
        linkScratch.Clear();
        foreach (KeyValuePair<Guid, ParkedOffer> pair in parked)
        {
            if (pair.Value.Sender == senderId) linkScratch.Add(pair.Key);
        }
        for (int i = 0; i < linkScratch.Count; i++) parked.Remove(linkScratch[i]);
        linkScratch.Clear();
    }

    private static void ClearPlaylistLinks()
    {
        parked.Clear();
        wanted.Clear();
        pendingActivation.Clear();
    }

    private static bool Activate(Clip clip)
    {
        if (!clip.Deferred) return true;
        if (inboundBytes + clip.TotalBytes > MaxInboundBytes) return false;
        string partPath = Path.Combine(cacheDirectory, clip.Id.ToString("N") + ".part");
        try
        {
            clip.Storage = BasisMediaClipFile.CreateForWrite(partPath, clip.TotalBytes);
        }
        catch (Exception exception)
        {
            TryDeleteFile(partPath);
            FailReceive(clip, exception.Message);
            clip.Deferred = false;
            return true;
        }
        float now = Time.realtimeSinceStartup;
        clip.Deferred = false;
        clip.Pulled = true;
        clip.FilePath = partPath;
        clip.OwnsFile = true;
        clip.Received = new bool[clip.TotalChunks];
        clip.LastProgressTime = now;
        clip.LastNeedTime = now;
        inboundBytes += clip.TotalBytes;
        pendingActivation.Remove(clip.Id);
        if (clip.FromServer) SendToServer(BasisMediaClipProtocol.EncodeId(BasisMediaClipProtocol.OpServerCacheRequest, clip.Id));
        else RequestMissing(clip);
        return true;
    }

    private static void ContinuePull(Clip clip)
    {
        if (!clip.Pulled || clip.Complete || clip.ReceivedCount < clip.PullTarget) return;
        clip.LastNeedTime = Time.realtimeSinceStartup;
        RequestMissing(clip);
    }

    private static void BuildView(Clip clip, Vector3 position, Quaternion rotation, float scale)
    {
        clip.View = BasisMediaClipObject.Build(clip.Id, clip.Name, position, rotation, scale, BasisMediaClipProtocol.IsAudio(clip.Container));
        RefreshBackInfo(clip);
        if (clip.Failed) clip.View.SetStatus(BasisLocalization.Get("mediaClip.status.failed"));
        else if (!clip.Complete && !clip.IsLocalSharer) UpdateReceiveStatus(clip);
        if (panelsVisible) clip.View.SetPanelVisible(true);
    }

    private static void DestroyView(Clip clip)
    {
        if (clip.View == null) return;
        if (clip.View.Player != null) clip.View.Player.Stop();
        UnityEngine.Object.Destroy(clip.View.gameObject);
        clip.View = null;
    }

    private static void TickPlaylistLinks()
    {
        linkTimer -= Time.unscaledDeltaTime;
        if (linkTimer > 0f) return;
        linkTimer = LinkTickSeconds;
        if (parked.Count > 0)
        {
            float now = Time.realtimeSinceStartup;
            linkScratch.Clear();
            foreach (KeyValuePair<Guid, ParkedOffer> pair in parked)
            {
                if (now - pair.Value.Time > ParkedOfferSeconds) linkScratch.Add(pair.Key);
            }
            for (int i = 0; i < linkScratch.Count; i++) parked.Remove(linkScratch[i]);
        }
        if (pendingActivation.Count > 0)
        {
            linkScratch.Clear();
            linkScratch.AddRange(pendingActivation);
            for (int i = 0; i < linkScratch.Count; i++)
            {
                if (!clips.TryGetValue(linkScratch[i], out Clip clip) || !clip.Deferred)
                {
                    pendingActivation.Remove(linkScratch[i]);
                    continue;
                }
                if (Activate(clip)) pendingActivation.Remove(clip.Id);
            }
        }
        linkScratch.Clear();
    }
}
