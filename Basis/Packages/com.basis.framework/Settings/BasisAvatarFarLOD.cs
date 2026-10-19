using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// Far avatars for remote players whose real avatar isn't loaded: a ~20-bone, ~8k-triangle
/// proxy baked into the bee connector, built at runtime as a REAL <see cref="BasisAvatar"/>
/// (see <see cref="BasisFarAvatarBuilder"/>) and installed through the exact same pipeline as
/// every other avatar. Loading avatar, bundle avatar, glTF avatar and far avatar are all the
/// same thing to the rest of the system — avatars are swapped, never hidden or disabled.
///
/// The far avatar lives PAST the max avatar range slider: inside avatar range the real
/// avatar always shows; beyond it the range system unloads the real avatar and the far
/// avatar replaces the loading dummy. It also fronts the player while their real avatar
/// downloads, has no build for this platform, or failed to load.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisAvatarFarLOD
{
    /// <summary>Master switch. When false, players without a real avatar show the loading dummy.</summary>
    public static bool Enabled;

    /// <summary>Avatar swaps admitted per transmit tick; each one costs a full avatar install.</summary>
    public static int MaxTransitionsPerTick = 4;

    /// <summary>
    /// Wall-clock ceiling on the swaps admitted per transmit tick. The count above bounds how
    /// many installs run, not what they cost — an install is a build plus a full remote
    /// calibration, and a heavy one drags the whole tick with it. This measures the swaps
    /// themselves (never the per-player flag reconciliation) and stops admitting more once the
    /// budget is gone; the rest stay pending and commit on later ticks, exactly as an exhausted
    /// count budget does.
    /// </summary>
    public static float MaxTransitionMillisecondsPerTick = 2f;

    // Tick runs under the transmit marker, which reports one number for the whole loop. These
    // separate the budgeted swaps — the only work here that costs milliseconds — from the
    // per-player flag reconciliation, so a spike says which one it was.

    [NoAutoStaticsCleanup] private static readonly System.Diagnostics.Stopwatch sTransitionClock = new System.Diagnostics.Stopwatch();
    private static long sTransitionBudgetTicks = long.MaxValue;

    /// <summary>
    /// Time.unscaledTime sampled once for the whole tick. The retry gates below read it per
    /// remote, and Time.unscaledTime is an engine property call, not a field — one sample for
    /// the loop is both cheaper and more consistent than one per player.
    /// </summary>
    private static float sTickUnscaledTime;

    /// <summary>
    /// Opens a fresh swap-time budget. Called once at the top of the transmit tick's merged
    /// post-processing loop, before any <see cref="Tick"/>, which relies on the timestamp
    /// captured here.
    /// </summary>
    public static void BeginTickBudget()
    {
        // Retires far avatar versions whose last wearer went away. Deliberately here and not in
        // the release itself: that runs inside Unity's destruction pass, where the prototype and
        // its holder cannot be reliably destroyed. See DrainPendingTeardowns.
        BasisFarAvatarBuilder.DrainPendingTeardowns();
        sTransitionClock.Reset();
        sTransitionBudgetTicks = (long)(MaxTransitionMillisecondsPerTick * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        sTickUnscaledTime = Time.unscaledTime;
    }

    /// <summary>True once this tick has spent its swap budget. ElapsedTicks, not Elapsed — the
    /// guard runs once per remote per tick and TimeSpan construction is not free at crowd scale.</summary>
    private static bool BudgetSpent => sTransitionClock.ElapsedTicks >= sTransitionBudgetTicks;

    /// <summary>
    /// Reload through the normal pipeline, charged against the per-tick swap budget. Every
    /// branch below that reloads goes through here so nothing escapes the accounting.
    /// </summary>
    private static void ChargedReload(BasisRemotePlayer remote)
    {
        sTransitionClock.Start();
        using (BasisNetworkMarkers.TransmitFarLodReload.Auto())
        {
            remote.ReloadAvatar();
        }
        sTransitionClock.Stop();
    }

    public static void ApplyFromSettings()
    {
        bool wasEnabled = Enabled;
        Enabled = BasisSettingsDefaults.UseAvatarFarLod.RawValue;
        if (wasEnabled != Enabled)
        {
            ReapplyAllRemotes();
        }
    }

    /// <summary>
    /// Per-remote reconciliation, called from the transmit tick's merged post-processing
    /// loop. Edge-triggered: does nothing while the current avatar kind matches the desired
    /// one, and consumes one unit of <paramref name="transitionBudget"/> per swap.
    /// </summary>
    public static void Tick(BasisRemotePlayer remote, ref int transitionBudget)
    {
        // IsDestroyed: a leaving player is still in this tick's transmit snapshot after the
        // factory unloaded their avatar — the null-avatar branch below would try to install
        // a fallback on the torn-down player ("Missing Object during calibration").
        if (remote == null || remote.IsDestroyed || transitionBudget <= 0 || BudgetSpent)
        {
            return;
        }

        // One Unity null comparison for the whole method. BasisAvatar is a UnityEngine.Object,
        // so every `== null` on it is a managed-to-native call, and this path used to pay three
        // per remote per tick — here, again inside IsFarLodActive, and a third time inside
        // WornFarVersion. Each branch below that can replace the avatar returns straight after
        // doing so, and the fetch branch only caches a payload (installs are tick-only), so the
        // local cannot go stale within one call.
        BasisAvatar wornAvatar = remote.BasisAvatar;
        bool hasAvatar = wornAvatar != null;
        bool wearingFar = hasAvatar && wornAvatar.IsFarLodAvatar;

        // While a real avatar downloads, cache the payload as soon as the connector (which
        // downloads first) carries one — the far avatar can then front the download.
        if (remote.IsLoadingAnAvatar && string.IsNullOrEmpty(remote.FarLodOverridePayload))
        {
            BasisLoadableBundle loadingBundle = remote.AlwaysRequestedAvatar;
            if (loadingBundle?.BasisBundleConnector != null && !string.IsNullOrEmpty(loadingBundle.BasisBundleConnector.FarLodBase64))
            {
                CaptureFarLodFallback(remote, loadingBundle);
            }
        }

        // Wearing nothing at all — the fallback load never ran or failed (e.g. the factory
        // wasn't initialized yet at join). IsConsideredFallBackAvatar defaults to true, so
        // every branch below would assume a dummy is present; load it before evaluating.
        if (!hasAvatar && !remote.IsLoadingAnAvatar)
        {
            transitionBudget--;
            sTransitionClock.Start();
            using (BasisNetworkMarkers.TransmitFarLodReload.Auto())
            {
                BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(remote, Vector3.zero, Quaternion.identity);
            }
            sTransitionClock.Stop();
            return;
        }

        // Join-time evaluation, deferred to this loop so it uses the job's distance: joiners
        // load only the fallback, and an out-of-range joiner never gets a range edge, so run
        // one CreateAvatar pass for them — it reads visibility settings and either starts the
        // far avatar fetch or keeps the fallback. In-range joiners are excluded (their range
        // edge, pending or committed, loads the full avatar).
        if (!remote.FarLodInitialEvaluated && !remote.InAvatarRange && !remote.PendingRangeActive &&
            !remote.IsLoadingAnAvatar && remote.IsConsideredFallBackAvatar && !wearingFar &&
            remote.AlwaysRequestedAvatar != null)
        {
            remote.FarLodInitialEvaluated = true;
            transitionBudget--;
            ChargedReload(remote);
            return;
        }

        // The connector-only fetch can fail transiently (network hiccup, an expired cert on
        // the bee host) — without a retry those players sit on the dummy forever. Bees known
        // to carry no payload are excluded: capture records the source it inspected even
        // when it found nothing there.
        //
        // The bundle walk that resolves the source string is inside the gate, not ahead of
        // it: this runs for every remote on every transmit tick, and the flag reads below
        // reject almost all of them, so paying a property chain + string compare per player
        // to feed a branch that is nearly always dead is the whole cost at crowd scale.
        if (remote.FarLodInitialEvaluated && !remote.InAvatarRange && !remote.IsLoadingAnAvatar &&
            remote.IsConsideredFallBackAvatar && !wearingFar &&
            !remote.FarLodConnectorFetchInFlight && string.IsNullOrEmpty(remote.FarLodOverridePayload) &&
            sTickUnscaledTime >= remote.FarLodNextFetchRetryTime)
        {
            string requestedSource = remote.AlwaysRequestedAvatar?.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (!string.IsNullOrEmpty(requestedSource) && remote.FarLodOverrideSource != requestedSource)
            {
                remote.FarLodNextFetchRetryTime = sTickUnscaledTime + 10f;
                RequestFarLodPayload(remote, remote.AlwaysRequestedAvatar);
            }
        }

        bool wantsFar = WantsFarAvatar(remote);

        if (wantsFar)
        {
            // A far avatar from a previous avatar record counts as not-desired: the install
            // below swaps it to the resolved version like any other stale representation.
            // Resolved-version testing stays deferred to here because only this branch reads
            // the answer, and it still costs a version-string compare per far-LOD wearer.
            bool wearingDesiredFar = wearingFar && BasisFarAvatarBuilder.IsWearingResolvedVersion(remote);
            if (!wearingDesiredFar)
            {
                // This tick is the ONLY place far avatars install — CreateAvatar and the load
                // error path run on IO/task continuations where the remote-bone/jiggle pipelines
                // can have in-flight jobs, so they only warm the parse and keep the current
                // avatar up. Over the fallback dummy an install is always safe (it also fronts
                // in-flight downloads); a live real avatar is only replaced between loads.
                bool canInstall = remote.IsConsideredFallBackAvatar || !remote.IsLoadingAnAvatar;
                if (canInstall)
                {
                    bool installed;
                    sTransitionClock.Start();
                    using (BasisNetworkMarkers.TransmitFarLodInstall.Auto())
                    {
                        installed = BasisFarAvatarBuilder.TryInstall(remote);
                    }
                    sTransitionClock.Stop();
                    if (installed)
                    {
                        transitionBudget--;
                    }
                }
            }
        }
        else if (!wantsFar && wearingFar && !remote.IsLoadingAnAvatar)
        {
            // Back in range (or the far avatar became unwanted) — reload through the normal
            // pipeline: in range loads the real avatar, hidden/blocked/disabled drops to the
            // dummy. The range edge usually beats this branch; it covers missed edges.
            transitionBudget--;
            ChargedReload(remote);
        }
        else if (!wantsFar && !wearingFar && !remote.IsConsideredFallBackAvatar && !remote.IsLoadingAnAvatar &&
                 ((!remote.InAvatarRange && !remote.AvatarAlwaysLoaded) || remote.HasFailedAvatarLoadGlobally))
        {
            // A real avatar was kept up for a pending far install whose payload then died
            // (refused parse / cleared) — without this it would stay loaded past the range
            // boundary forever. Reload routes it to the dummy through the normal pipeline.
            transitionBudget--;
            ChargedReload(remote);
        }
        else if (Enabled && !wantsFar && !wearingFar && !remote.InAvatarRange && !remote.IsLoadingAnAvatar &&
                 remote.IsConsideredFallBackAvatar && remote.HasFarLodPayload &&
                 sTickUnscaledTime >= remote.FarLodNextFetchRetryTime)
        {
            // Payload in hand, player out of range on the dummy, yet no far avatar wanted —
            // one of the gates is refusing; name it instead of sitting silent. The master
            // switch being off is a setting, not a refusal — no warning for it.
            remote.FarLodNextFetchRetryTime = sTickUnscaledTime + 10f;
            BasisDebug.LogWarning($"Far avatar for {remote.DisplayName} has a payload but is gated: enabled={Enabled} blocked={remote.IsEffectivelyBlocked} alwaysShow={remote.AvatarAlwaysLoaded}", BasisDebug.LogTag.Avatar);
        }
    }

    /// <summary>
    /// Should this player be wearing the far avatar right now? No avatar-range check beyond
    /// the InAvatarRange flag: the far avatar IS the beyond-range representation, so a range
    /// of zero simply means everyone wears their far avatar; UseAvatarFarLod off is the
    /// switch that drops players to the loading dummy instead. For always-loaded players
    /// the far avatar only bridges downloads and dead loads.
    /// </summary>
    public static bool WantsFarAvatar(BasisRemotePlayer remote)
    {
        bool wantsFar = Enabled &&
            !remote.IsEffectivelyBlocked && remote.HasFarLodPayload &&
            (!remote.InAvatarRange || remote.HasFailedAvatarLoadGlobally || remote.IsLoadingAnAvatar);
        if (remote.AvatarAlwaysLoaded && !remote.IsLoadingAnAvatar && !remote.HasFailedAvatarLoadGlobally)
        {
            wantsFar = false;
        }
        return wantsFar;
    }

    /// <summary>
    /// CreateAvatar-side desire with the in-flight load projected as finished. MUST agree
    /// with <see cref="WantsFarAvatar"/> once IsLoadingAnAvatar clears — a disagreement makes
    /// the transmit tick reverse CreateAvatar's swap every pass, a full avatar install each
    /// time, forever.
    /// </summary>
    public static bool WantsFarAvatarAfterLoad(BasisRemotePlayer remote)
    {
        return Enabled &&
            !remote.IsEffectivelyBlocked && remote.HasFarLodPayload &&
            (!remote.InAvatarRange || remote.HasFailedAvatarLoadGlobally) &&
            !(remote.AvatarAlwaysLoaded && !remote.HasFailedAvatarLoadGlobally);
    }

    /// <summary>
    /// Caches the original bundle's far avatar payload on the player. Used whenever the real
    /// avatar isn't in hand: mid-download, no section for this platform, load failure, out of
    /// range. The payload is a fixed small cost regardless of how heavy the real avatar is.
    /// </summary>
    public static void CaptureFarLodFallback(BasisRemotePlayer remote, BasisLoadableBundle bundle)
    {
        BasisBundleConnector connector = bundle?.BasisBundleConnector;
        if (remote == null || connector == null)
        {
            return;
        }
        CaptureFarLodFallback(remote, connector, bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
    }

    /// <summary>
    /// Core capture from an already-parsed connector — the shared per-URL fetch hands the
    /// SAME connector to every wearer of that avatar, so the payload string is one instance
    /// referenced by all of them, never N copies.
    /// </summary>
    public static void CaptureFarLodFallback(BasisRemotePlayer remote, BasisBundleConnector connector, string source)
    {
        if (remote == null || connector == null || string.IsNullOrEmpty(source))
        {
            return;
        }
        if (string.IsNullOrEmpty(connector.FarLodBase64))
        {
            // Remember that this source was inspected and had nothing — stops the transmit
            // tick's fetch retry from re-reading a known payload-less bee forever.
            remote.FarLodOverrideSource = source;
            remote.FarLodOverridePayload = null;
            remote.FarLodOverrideVersion = null;
            return;
        }
        if (string.IsNullOrEmpty(connector.UniqueVersion))
        {
            BasisDebug.LogWarning($"Far avatar capture for {remote.DisplayName} declined: connector has no UniqueVersion.", BasisDebug.LogTag.Avatar);
            return;
        }
        if (remote.FarLodOverrideSource != source || string.IsNullOrEmpty(remote.FarLodOverridePayload))
        {
            remote.FarLodOverridePayload = connector.FarLodBase64;
            remote.FarLodOverrideVersion = connector.UniqueVersion;
            remote.FarLodOverrideSource = source;
            remote.ResetFarLodForNewAvatar();
        }
    }

    [NoAutoStaticsCleanup] private static readonly SemaphoreSlim sConnectorFetchGate = new SemaphoreSlim(4);

    /// <summary>
    /// Fetches just the bee connector (two ranged requests, no bundle download) for a player
    /// whose avatar was never loaded — out-of-range players get their real silhouette without
    /// ever paying for the full avatar. Fire-and-forget; the transmit tick installs the far
    /// avatar once the payload lands.
    /// </summary>
    /// <summary>
    /// True when the bundle carries a REAL parsed connector. Network-converted bundles are
    /// constructed with a non-null EMPTY connector husk (see BasisBundleConversionNetwork),
    /// so a plain null check reads "connector present" for every fresh joiner and starves
    /// the fetch path.
    /// </summary>
    public static bool HasRealConnector(BasisLoadableBundle bundle)
    {
        return !string.IsNullOrEmpty(bundle?.BasisBundleConnector?.UniqueVersion);
    }

    /// <summary>
    /// Connector fetches shared per bee URL: N wearers of one avatar await ONE ranged
    /// download instead of racing N tasks through the gate. The old per-player fetches each
    /// started a 30s budget that also covered the QUEUE — in a same-avatar lobby everyone
    /// past the first gate-width waited out their budget behind identical downloads and got
    /// cancelled. Completed successes stay cached so later wearers capture instantly;
    /// failures are evicted so the tick's per-player backoff retries with a fresh fetch.
    /// Main-thread access only.
    /// </summary>
    private sealed class ConnectorFetch
    {
        public BasisBundleConnector Connector;
        /// <summary>True when the failure is worth refetching (network blip, timeout).</summary>
        public bool Transient;
    }

    private static readonly Dictionary<string, Task<ConnectorFetch>> sConnectorFetchesByUrl = new Dictionary<string, Task<ConnectorFetch>>(8);
    private static readonly List<string> sConnectorSweepScratch = new List<string>(4);

    public static async void RequestFarLodPayload(BasisRemotePlayer remote, BasisLoadableBundle bundle)
    {
        if (remote == null || bundle == null || remote.FarLodConnectorFetchInFlight ||
            string.IsNullOrEmpty(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
        {
            return;
        }
        if (HasRealConnector(bundle))
        {
            CaptureFarLodFallback(remote, bundle);
            return;
        }

        string url = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        remote.FarLodConnectorFetchInFlight = true;
        try
        {
            if (!sConnectorFetchesByUrl.TryGetValue(url, out Task<ConnectorFetch> fetch))
            {
                SweepConnectorFetches();
                fetch = FetchConnectorForUrl(url, bundle);
                sConnectorFetchesByUrl[url] = fetch;
            }

            ConnectorFetch result = await fetch;

            if (result.Connector == null)
            {
                // Transient failures are evicted so the tick's backoff refetches; fatal ones
                // (corrupt/unreadable bee) stay cached so every wearer's retry resolves
                // instantly without hammering the host for a bee that will never parse.
                if (result.Transient &&
                    sConnectorFetchesByUrl.TryGetValue(url, out Task<ConnectorFetch> current) && current == fetch)
                {
                    sConnectorFetchesByUrl.Remove(url);
                }
                return;
            }
            if (remote.IsDestroyed)
            {
                return;
            }
            // Identity by bee URL, not instance — a second avatar message during the fetch
            // recreates AlwaysRequestedAvatar as a new object for the same bee.
            string currentSource = remote.AlwaysRequestedAvatar?.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (currentSource == url)
            {
                CaptureFarLodFallback(remote, result.Connector, url);
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"Far avatar connector capture failed for {remote.DisplayName}: {e.Message}", BasisDebug.LogTag.Avatar);
        }
        finally
        {
            remote.FarLodConnectorFetchInFlight = false;
        }
    }

    private const int ConnectorFetchAttempts = 2;

    /// <summary>
    /// The one download per bee URL. Never throws — a null connector means failure, logged
    /// once here instead of once per wearer. The 30s timeout is created fresh PER ATTEMPT
    /// and only after the gate slot is held, so neither our queue nor a retry burns it (the
    /// "Cancelled before starting" wave was the old shared budget expiring while queued).
    /// Transient failures (network blips, timeouts) retry once in place before deferring to
    /// the tick's backoff; fatal ones (corrupt bee) stop immediately.
    /// </summary>
    private static async Task<ConnectorFetch> FetchConnectorForUrl(string url, BasisLoadableBundle bundle)
    {
        try
        {
            await sConnectorFetchGate.WaitAsync();
            try
            {
                if (HasRealConnector(bundle))
                {
                    return new ConnectorFetch { Connector = bundle.BasisBundleConnector };
                }
                string lastError = "none";
                bool lastTransient = true;
                for (int attempt = 1; attempt <= ConnectorFetchAttempts; attempt++)
                {
                    using CancellationTokenSource fetchTimeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(30));
                    BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { LoadableBundle = bundle };
                    BasisMetaLoadResult metaResult = await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, new BasisProgressReport(), fetchTimeout.Token);
                    if (HasRealConnector(bundle))
                    {
                        return new ConnectorFetch { Connector = bundle.BasisBundleConnector };
                    }
                    lastError = metaResult.Error ?? "none";
                    lastTransient = metaResult.IsTransient;
                    if (!metaResult.IsTransient)
                    {
                        break;
                    }
                }
                BasisDebug.LogWarning($"Far avatar connector fetch produced nothing for {url}: transient={lastTransient} error={lastError}", BasisDebug.LogTag.Avatar);
                return new ConnectorFetch { Transient = lastTransient };
            }
            finally
            {
                sConnectorFetchGate.Release();
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"Far avatar connector fetch failed for {url}: {e.Message}", BasisDebug.LogTag.Avatar);
            return new ConnectorFetch { Transient = true };
        }
    }

    /// <summary>Bounds the per-URL cache; dropping a completed success only costs a refetch.</summary>
    private static void SweepConnectorFetches()
    {
        if (sConnectorFetchesByUrl.Count < 32)
        {
            return;
        }
        sConnectorSweepScratch.Clear();
        foreach (KeyValuePair<string, Task<ConnectorFetch>> entry in sConnectorFetchesByUrl)
        {
            if (entry.Value.IsCompleted)
            {
                sConnectorSweepScratch.Add(entry.Key);
            }
        }
        for (int Index = 0; Index < sConnectorSweepScratch.Count; Index++)
        {
            sConnectorFetchesByUrl.Remove(sConnectorSweepScratch[Index]);
        }
    }

    /// <summary>
    /// Seed hook, run at the end of remote calibration. A far avatar's own calibration is a
    /// no-op here; any other avatar re-evaluates payload availability (a new calibration may
    /// mean a new avatar version).
    /// </summary>
    public static void SeedAfterCalibration(BasisRemotePlayer remote)
    {
        if (remote?.BasisAvatar == null || remote.BasisAvatar.IsFarLodAvatar)
        {
            return;
        }
        remote.ResetFarLodForNewAvatar();
    }

    private static void ReapplyAllRemotes()
    {
        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            BasisRemotePlayer remote = kvp.Value;
            if (remote == null)
            {
                continue;
            }
            if (!Enabled && remote.IsFarLodActive && !remote.IsLoadingAnAvatar)
            {
                // Reload routes them back to the dummy (or real avatar if in range). The
                // payload cache stays, so re-enabling restores far avatars next tick.
                remote.ReloadAvatar();
            }
        }
    }
}
