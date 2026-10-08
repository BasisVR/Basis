using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Caps how many media players decode at once on this machine.
///
/// Props can carry media players, so the number in a world is not the world
/// author's to decide: anyone can spawn more, and every open session costs
/// this viewer stream bandwidth, memory and decode work whether or not they are
/// looking at it. A handful of autoplaying prop players will exhaust a
/// standalone headset's link and frame budget on their own.
///
/// The nearest few players stay running and the rest go dormant. Dormant means
/// the session is closed rather than paused (a paused session still holds its
/// buffer, frame pool and decoder), with the URL and playback position
/// remembered, so re-activating costs an ordinary join and lands where the
/// player would have been.
///
/// The cap is the viewer's setting, never the world's, for the same reason the
/// decode route is: it describes this machine.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMediaSessionGovernor
{
    /// <summary>How near a dormant player must be, as a fraction of the
    /// distance to the active player it would displace, before it takes its
    /// place. Without a margin the two swap back and forth every time the
    /// viewer moves between them.</summary>
    const float SwapMargin = 0.8f;

    /// <summary>How long a player stays where it was put after being activated
    /// or made dormant. Stops a walk past a row of screens from opening and
    /// closing sessions the whole way along.</summary>
    const float DwellSeconds = 5f;

    /// <summary>How long an explicit promotion outranks distance.</summary>
    const float PromotionSeconds = 60f;

    const float EvaluateIntervalSeconds = 1f;

    class Entry
    {
        public BasisMediaPlayer Player;
        public bool Dormant;
        public string Url;
        public double PositionSeconds;
        public bool Live;
        /// <summary>The viewer had it paused when it went dormant.</summary>
        public bool Paused;
        public float SettledAt;
        public float PromotedUntil;
        public bool AwaitingResume;
        public float ResumeStartedAt;
    }

    static readonly Dictionary<BasisMediaPlayer, Entry> entries = new();
    static readonly List<Entry> ranked = new();
    static readonly List<BasisMediaSessionSelection.Candidate> candidates = new();
    static readonly List<int> toActivate = new();
    static readonly List<int> toDemote = new();
    static float nextEvaluate;

    /// <summary>Players this governor has closed to stay inside the cap.</summary>
    public static int DormantCount
    {
        get
        {
            int count = 0;
            foreach (Entry entry in entries.Values)
            {
                if (entry.Dormant) count++;
            }

            return count;
        }
    }

    /// <summary>The cap in force on this machine. 0 means no cap.</summary>
    public static int MaxActive => Mathf.Max(0, BasisMediaSettings.MaxActivePlayers.RawValue);

    /// <summary>Whether this player is dormant because of the cap, rather than
    /// simply not playing.</summary>
    public static bool IsDormant(BasisMediaPlayer player) =>
        player != null && entries.TryGetValue(player, out Entry entry) && entry.Dormant;

    /// <summary>
    /// Put a player at the front of the queue: it is activated now, and the
    /// furthest active player gives up its slot. This is what selecting a
    /// dormant player in the menu, or a world script deliberately starting one,
    /// should call.
    /// </summary>
    public static void Promote(BasisMediaPlayer player)
    {
        if (player == null) return;
        Entry entry = GetOrCreate(player);
        entry.PromotedUntil = Time.unscaledTime + PromotionSeconds;
        Evaluate();
    }

    static Driver driver;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        // Statics outlive a domain reload with reload disabled in the editor, so
        // a second play session would otherwise inherit the previous one's view
        // of which players were dormant, and stack up a second driver.
        entries.Clear();
        ranked.Clear();
        nextEvaluate = 0f;
        if (driver != null) return;

        var host = new GameObject(nameof(BasisMediaSessionGovernor)) { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(host);
        driver = host.AddComponent<Driver>();
    }

    sealed class Driver : MonoBehaviour
    {
        BasisMediaDriverTick frameTick;

        void Awake() => frameTick = new BasisMediaDriverTick(Tick);

        void OnEnable() => frameTick.Arm();

        void OnDisable() => frameTick.Disarm();

        void Update() => frameTick.RunFromUpdate();
    }

    static void Tick()
    {
        ResumeWhereReady();
        if (Time.unscaledTime < nextEvaluate) return;
        nextEvaluate = Time.unscaledTime + EvaluateIntervalSeconds;
        Evaluate();
    }

    static Entry GetOrCreate(BasisMediaPlayer player)
    {
        if (!entries.TryGetValue(player, out Entry entry))
        {
            // Eligible immediately. The dwell exists to stop a player that just
            // moved from moving straight back, and a player we have never seen
            // has not moved. Dating it from now would let every session in a
            // scene open at once and stay open for the first few seconds, when
            // the cap is most needed.
            entry = new Entry { Player = player, SettledAt = float.NegativeInfinity };
            entries[player] = entry;
        }

        return entry;
    }

    static void Evaluate()
    {
        int cap = MaxActive;
        Prune();
        if (cap <= 0)
        {
            // No cap: bring back anything this governor closed, and stop.
            foreach (Entry entry in entries.Values)
            {
                if (entry.Dormant) Activate(entry);
            }

            return;
        }

        ranked.Clear();
        IReadOnlyList<BasisMediaPlayer> players = BasisMediaPlayerRegistry.Players;
        for (int i = 0; i < players.Count; i++)
        {
            BasisMediaPlayer player = players[i];
            if (player == null) continue;
            Entry entry = GetOrCreate(player);

            // A player nobody has started does not hold a session and so costs
            // nothing to leave alone. Only players that are running, or that we
            // stopped, are competing for a slot.
            if (!entry.Dormant && !HoldsSession(player)) continue;

            // Someone started a dormant player behind our back. That is an
            // intent to watch it, so treat it as a promotion rather than
            // closing it again on the next pass.
            if (entry.Dormant && HoldsSession(player))
            {
                entry.Dormant = false;
                entry.PromotedUntil = Time.unscaledTime + PromotionSeconds;
                entry.SettledAt = Time.unscaledTime;
            }

            ranked.Add(entry);
        }

        if (ranked.Count == 0) return;

        Vector3 listener = ListenerPosition();
        ranked.Sort((a, b) => CompareForSlot(a, b, listener));

        // The retained set is chosen before anything moves, so a dormant
        // player held off by the swap margin leaves the one it would have
        // displaced running. The dwell keeps a walk past a row of screens
        // from opening and closing sessions the whole way.
        float now = Time.unscaledTime;
        candidates.Clear();
        for (int i = 0; i < ranked.Count; i++)
        {
            Entry entry = ranked[i];
            candidates.Add(new BasisMediaSessionSelection.Candidate
            {
                Active = !entry.Dormant,
                Promoted = IsPromoted(entry, now),
                Held = now - entry.SettledAt < DwellSeconds,
                Distance = DistanceTo(entry, listener),
            });
        }

        BasisMediaSessionSelection.Select(candidates, cap, SwapMargin, toActivate, toDemote);
        for (int i = 0; i < toDemote.Count; i++) Demote(ranked[toDemote[i]]);
        for (int i = 0; i < toActivate.Count; i++) Activate(ranked[toActivate[i]]);
    }

    static bool IsPromoted(Entry entry, float now) => now < entry.PromotedUntil;

    static int CompareForSlot(Entry a, Entry b, Vector3 listener)
    {
        float now = Time.unscaledTime;
        bool pa = IsPromoted(a, now), pb = IsPromoted(b, now);
        if (pa != pb) return pa ? -1 : 1;
        return DistanceTo(a, listener).CompareTo(DistanceTo(b, listener));
    }

    /// <summary>True distance rather than the squared form, because the swap
    /// margin is expressed as a fraction of one and squaring would silently
    /// change what that fraction means. The square root is negligible for a
    /// handful of players once a second.</summary>
    static float DistanceTo(Entry entry, Vector3 listener)
    {
        if (entry.Player == null) return float.MaxValue;
        return Vector3.Distance(entry.Player.transform.position, listener);
    }

    /// <summary>Where the viewer is. Falls back to the local camera, and then to
    /// the origin, so a headless or camera-less run still ranks deterministically
    /// rather than throwing.</summary>
    static Vector3 ListenerPosition()
    {
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local != null) return local.transform.position;
        return BasisLocalCameraDriver.HasInstance ? BasisLocalCameraDriver.Position : Vector3.zero;
    }

    /// <summary>Whether the player is holding engine resources: anything but
    /// closed. A finished or failed session has already released them.</summary>
    static bool HoldsSession(BasisMediaPlayer player)
    {
        switch (player.State)
        {
            case BmState.Opening:
            case BmState.Buffering:
            case BmState.Playing:
            case BmState.Paused:
                return true;
            default:
                return false;
        }
    }

    static void Demote(Entry entry)
    {
        BasisMediaPlayer player = entry.Player;
        if (player == null) return;

        entry.Url = ShareableUrl(player);
        entry.Live = player.liveness == BmLiveness.Live || player.DurationSeconds <= 0d;
        entry.PositionSeconds = entry.Live ? 0d : player.PositionSeconds;
        entry.Paused = player.IsPaused;
        entry.Dormant = true;
        entry.AwaitingResume = false;
        entry.SettledAt = Time.unscaledTime;
        player.Stop();
    }

    static void Activate(Entry entry)
    {
        BasisMediaPlayer player = entry.Player;
        entry.Dormant = false;
        entry.SettledAt = Time.unscaledTime;
        if (player == null || string.IsNullOrEmpty(entry.Url)) return;

        player.LoadApprovedUrl(entry.Url);
        // Live sources rejoin at the edge; there is nothing to return to, and
        // nothing to pause.
        if (entry.Live) return;
        if (entry.PositionSeconds <= 0d && !entry.Paused) return;
        entry.AwaitingResume = true;
        entry.ResumeStartedAt = Time.unscaledTime;
    }

    /// <summary>
    /// Puts a reactivated player back where it was, once its session is running
    /// far enough to accept a seek, and pauses it again if the viewer had it
    /// paused. A shared-playback player's position is left alone: its owner's
    /// is the truth, and it arrives on the next heartbeat; one that was paused
    /// with the room is paused again rather than playing out loud until the
    /// room's state arrives.
    /// </summary>
    static void ResumeWhereReady()
    {
        foreach (Entry entry in entries.Values)
        {
            if (!entry.AwaitingResume) continue;
            BasisMediaPlayer player = entry.Player;
            if (player == null) { entry.AwaitingResume = false; continue; }

            BmState state = player.State;
            if (state == BmState.Opening || state == BmState.Buffering) continue;
            entry.AwaitingResume = false;
            if (state != BmState.Playing && state != BmState.Paused) continue;
            // Seeking a shared-playback player locally would fight the owner.
            // Carrying the component is not the same as being in a
            // session, though: offline, or before a NetworkID is assigned,
            // there is no owner to defer to and nothing else will place this
            // player. HasNetworkID is the same test the component applies to
            // itself before it treats anything as networked.
            if (player.TryGetComponent(out BasisMediaPlayerNetworking networking)
                && networking.HasNetworkID)
            {
                if (entry.Paused) player.Pause();
                continue;
            }
            if (player.DurationSeconds > 0d && entry.PositionSeconds > 0d)
            {
                player.Seek(BasisMediaSessionSelection.ResumeTarget(
                    entry.Paused, entry.PositionSeconds, Time.unscaledTime - entry.ResumeStartedAt));
            }
            // The engine holds a pause across the seek, so the order does not
            // matter: the session lands on the frame and stays there.
            if (entry.Paused) player.Pause();
        }
    }

    /// <summary>What to reopen with: the page URL a resolver recorded, since the
    /// stream it extracted is issued per client and will have expired by the
    /// time this player wakes up.</summary>
    static string ShareableUrl(BasisMediaPlayer player)
    {
        BasisResolvedMedia media = player.Media;
        if (media != null) return media.SourceUrl ?? string.Empty;
        return player.url ?? string.Empty;
    }

    static void Prune()
    {
        if (entries.Count == 0) return;
        List<BasisMediaPlayer> gone = null;
        foreach (KeyValuePair<BasisMediaPlayer, Entry> pair in entries)
        {
            if (pair.Key == null) (gone ??= new List<BasisMediaPlayer>()).Add(pair.Key);
        }

        if (gone == null) return;
        for (int i = 0; i < gone.Count; i++) entries.Remove(gone[i]);
    }
}
