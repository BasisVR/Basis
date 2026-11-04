using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisMediaClipStore
{
    private sealed class Waiter
    {
        public BasisMediaPlayer Player;
        public int Generation;
        public string Url;
    }

    private sealed class Resolver : IBasisVideoResolver
    {
        public int Priority => 1000;
        public bool CanResolve(string url) => BasisMediaClipProtocol.IsClipUrl(url);
        public bool TryResolve(BasisMediaPlayer player, string url) => Resolve(player, url);
    }

    private static readonly Dictionary<Guid, string> readyPaths = new Dictionary<Guid, string>();
    private static readonly Dictionary<Guid, List<Waiter>> waiters = new Dictionary<Guid, List<Waiter>>();
    [NoAutoStaticsCleanup] private static Resolver resolver;

    public static event Action<Guid> OnClipReady;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        resolver ??= new Resolver();
        BasisMediaUrlRouter.Register(resolver);
    }

    public static bool TryGetReadyPath(Guid id, out string path) => readyPaths.TryGetValue(id, out path);

    public static void MarkReady(Guid id, string path)
    {
        if (id == Guid.Empty || string.IsNullOrEmpty(path)) return;
        readyPaths[id] = path;
        if (waiters.Remove(id, out List<Waiter> pending))
        {
            for (int i = 0; i < pending.Count; i++)
            {
                Waiter waiter = pending[i];
                if (waiter.Player == null || waiter.Player.LoadGeneration != waiter.Generation) continue;
                Open(waiter.Player, waiter.Url, path);
            }
        }
        OnClipReady?.Invoke(id);
    }

    public static void Forget(Guid id)
    {
        readyPaths.Remove(id);
        waiters.Remove(id);
    }

    private static bool Resolve(BasisMediaPlayer player, string url)
    {
        if (player == null || !BasisMediaClipProtocol.TryParseUrl(url, out Guid id)) return false;
        if (readyPaths.TryGetValue(id, out string path))
        {
            Open(player, url, path);
            return true;
        }
        if (!waiters.TryGetValue(id, out List<Waiter> list))
        {
            list = new List<Waiter>();
            waiters[id] = list;
        }
        list.RemoveAll(w => w.Player == null || w.Player == player);
        list.Add(new Waiter { Player = player, Generation = player.LoadGeneration, Url = url });
        return true;
    }

    private static void Open(BasisMediaPlayer player, string url, string path)
    {
        player.OpenResolved(new BasisResolvedMedia
        {
            Url = path,
            SourceUrl = url,
            Liveness = BmLiveness.Vod,
            Provider = "clip",
        });
    }
}
