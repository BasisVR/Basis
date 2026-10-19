using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;

/// <summary>
/// Every <see cref="BasisMediaPlayer"/> alive in the scene. Players add
/// themselves in Awake and drop out in OnDestroy; consumers that build UI
/// over the set (an in-world players panel, a session governor) watch
/// <see cref="OnChanged"/> rather than scanning the scene.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMediaPlayerRegistry
{
    private static readonly List<BasisMediaPlayer> players = new List<BasisMediaPlayer>();

    public static IReadOnlyList<BasisMediaPlayer> Players => players;
    public static int Count => players.Count;

    public static event Action OnChanged;

    public static void Add(BasisMediaPlayer player)
    {
        if (player == null) return;
        if (players.Contains(player)) return;
        players.Add(player);
        OnChanged?.Invoke();
    }

    public static void Remove(BasisMediaPlayer player)
    {
        if (player == null) return;
        if (players.Remove(player)) OnChanged?.Invoke();
    }
}
