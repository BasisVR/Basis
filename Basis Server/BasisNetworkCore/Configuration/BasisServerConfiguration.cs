using Basis.Network.Core;
using BasisNetworkCore.Security;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

[Serializable]
public class Configuration
{
    public const string ConfigFolderName = "config";
    public const string LogsFolderName = "logs";
    public const string InitialResourcesFolderName = "initialresources";
    public const string DefaultLibraryFolderName = "defaultlibrary";

    /// <summary>
    /// Bump when config changes should force existing files to be rewritten (e.g. to refresh
    /// doc comments). Newly-added settings are healed automatically regardless: on load a
    /// config missing any current field is re-saved with the new settings added.
    /// </summary>
    // 5: EnableUplinkAvatarStream added; bumped so existing config.xml files are rewritten with its
    //    doc comment rather than only silently gaining the field.
    // 7: EnableUplinkAvatarStream removed again; bumped so existing files drop the stale field and
    //    its doc comment instead of carrying a setting nothing reads.
    // 8: hybrid avatar-bundle codec (EnableAvatarBundleZstd and friends) added; bumped so existing
    //    files gain the four settings with their doc comments rather than only the bare fields.
    // 9: per-player abuse caps (MaxNetworkIdsPerPlayer, MaxLoadedResourcesPerPlayer) and the opt-in
    //    scene-relay egress backstop (MaxSceneRelayMegabitsPerSecondPerPlayer) added; bumped so
    //    existing config.xml files gain the three settings with their doc comments.
    // 10: image-pickup replication range (ImagePickupRangeMeters) added.
    // 11: population-drop memory reclaim (IdleMemoryReclaim*) added.
    // 12: BSRSendPhaseBudgetPercent added - the send pass's share of the reduction tick, which was
    //     a constant fitted on one machine. Bumped so existing files gain it with its doc comment.
    // 13: LogConnectionHandshake added - the per-connection auth chatter is now off by default.
    public const int CurrentConfigVersion = 13;
    /// <summary>Schema version stamped into config.xml; 0 = a pre-versioning file that is upgraded on load.</summary>
    public int ConfigVersion = 0;

    public int PeerLimit = ushort.MaxValue;
    public ushort SetPort = 4296;
    /// <summary>Display name returned by the unconnected server-info query — what shows up as the row title in a client server-list UI.</summary>
    public string ServerName = "Basis Server";
    /// <summary>Short MOTD returned alongside the server name in the info query response. Two short lines render cleanly in the list UI.</summary>
    public string ServerMotd = "";
    public bool EnableStatistics = true;
    public bool HasFileSupport = true;
    public string HealthCheckHost = "localhost";
    public ushort HealthCheckPort = 10666;
    public string HealthPath = "/health";
    public bool HealthIncludeBSRProfiling = false;
    public bool IdleMemoryReclaimEnabled = true;
    public int IdleMemoryReclaimSettleSeconds = 30;
    public int IdleMemoryReclaimMinimumPeak = 8;
    public int BSRSMillisecondDefaultInterval = 50;
    public int BSRBaseMultiplier = 1;
    public float BSRSIncreaseRate = 0.005f;
    public float BSRSlowestSendRate = 2.55f;
    public int DistanceUpdateIntervalTicks = 125;
    public bool EnableComputeOffload = true;
    public string ComputeDevice = "";
    public int ComputeDistanceUpdateIntervalTicks = 32;
    public float HighQualityDistance = 10f;
    public float MediumQualityDistance = 20f;
    public float LowQualityDistance = 40f;
    public bool OverrideAutoDiscoveryOfIpv = false;
    public string IPv4Address = "0.0.0.0";
    public string IPv6Address = "::";
    public string Password = "default_password";
    public bool UseAuth = true;
    public bool UseAuthIdentity = true;
    public string NetworkStackId = "";
    public BasisUserRestrictionMode BasisUserRestrictionMode;
    public int HowManyDuplicateAuthCanExist = 2;
    public int AuthValidationTimeOutMiliseconds = 9000;
    public bool EnableConsole = true;
    /// <summary>
    /// When true, the avatar reduction system bundles per-receiver avatar messages
    /// and emits them deflated on CompressedAvatarBundleChannel. Falls back to
    /// per-message uncompressed sends when a receiver has too few queued messages
    /// for compression to be worthwhile, or when the compressed result would
    /// exceed peer MTU. Clients must implement the matching decoder.
    /// </summary>
    public bool EnableAvatarBundleCompression = true;
    /// <summary>Minimum queued avatar messages to a single receiver before a bundle is even attempted. Two correlated avatar payloads already LZ4 well and share one datagram; AvatarBundleMinBytes still guards the tiny-delta case.</summary>
    public int AvatarBundleMinMessages = 2;
    /// <summary>Minimum uncompressed bundle bytes before LZ4 compression is attempted. With LZ4 having near-zero per-call setup, 128 just guards the very smallest cases where LZ4 can't find any redundancy.</summary>
    public int AvatarBundleMinBytes = 128;
    /// <summary>
    /// Compress keyframe/full avatar bundles with Zstd against an embedded 16 KiB trained
    /// dictionary instead of LZ4. Measured 16.7-18.1% fewer bundle bytes at 250 clients for
    /// roughly 2x the compression CPU. Delta-only bundles stay on LZ4 either way — Zstd is a
    /// 2.8-4.5% LOSS on those. Has no effect unless a dictionary is embedded in the build
    /// (see BasisAvatarBundleDictionary); dictionary-less Zstd is worse than LZ4, so there is
    /// deliberately no partial mode. Clients must implement the matching decoder.
    /// </summary>
    public bool EnableAvatarBundleZstd = true;
    /// <summary>
    /// Also route delta-only bundles through Zstd. Measured a 2.8-4.5% loss against LZ4, so this
    /// is off; it exists to re-measure the traffic-class split on new data without a rebuild.
    /// </summary>
    public bool AvatarBundleZstdDeltaBundles = false;
    /// <summary>
    /// Zstd compression level for avatar bundles. Negative levels trade ratio for speed. -2 is
    /// the measured sweet spot (~17.3% saving at ~2.3x LZ4 CPU); -3 costs meaningfully less CPU
    /// for ~15-16%.
    /// </summary>
    public int AvatarBundleZstdLevel = -2;
    /// <summary>
    /// Highest BSR load-shed tier (0 healthy .. 2 maximum shedding) at which Zstd is still used.
    /// Above it every bundle falls back to LZ4. Zstd buys bandwidth with CPU, which is the right
    /// trade while the tick has headroom and the wrong one once the server is already shedding
    /// avatar quality to keep up. Set to 2 to keep Zstd on at all tiers, or -1 to disable it.
    /// </summary>
    public int AvatarBundleZstdMaxShedTier = 1;
    /// <summary>
    /// When true, the reduction system sends periodic full avatar keyframes and, in between, sends
    /// only the fields that changed since the last keyframe (per-bone dirty mask) on
    /// DeltaAvatarChannel. Cuts server→client bandwidth heavily for idle/distant avatars. When false,
    /// every send is a full keyframe (legacy behavior). Clients must implement the delta decoder.
    /// </summary>
    public bool EnableAvatarDeltaCompression = true;
    /// <summary>
    /// Milliseconds between forced full avatar keyframes per sender when delta compression is on.
    /// Lower = faster recovery from a lost keyframe over unreliable UDP, but more keyframe bandwidth;
    /// higher = smaller average bandwidth but longer worst-case staleness. 500ms is a balanced default.
    /// </summary>
    public int AvatarDeltaKeyframeIntervalMs = 500;
    /// <summary>
    /// Ceiling for the adaptive keyframe stretch. While a sender's deltas stay tiny (idle avatar)
    /// the periodic keyframe interval doubles per streak of small deltas, up to this value; any
    /// real motion snaps it back to AvatarDeltaKeyframeIntervalMs. Receivers that miss a keyframe
    /// request one on demand (v42 DeltaControlKeyframeRequest) instead of waiting the stretch out.
    /// Set to 0 (or at/below the base interval) to disable stretching.
    /// </summary>
    public int AvatarDeltaKeyframeMaxIntervalMs = 2000;
    /// <summary>
    /// Strip AdditionalAvatarData (face blendshapes, custom avatar-behaviour params) from the Low
    /// and VeryLow avatar tiers. Faces are unreadable past the Medium quality distance, and the
    /// reliable low-frequency behaviour channel still reaches everyone, so this only removes
    /// bytes nobody can see. High and Medium keep the data.
    /// </summary>
    public bool StripAdditionalDataAtLowQuality = true;
    /// <summary>
    /// Accept client→server avatar deltas on DeltaAvatarChannel and advertise support to clients
    /// (v42). Clients then upload a full keyframe every ~500 ms plus small deltas in between
    /// instead of full 237-byte frames every packet — 60-90% less uplink/ingress avatar traffic.
    /// When false, clients upload full keyframes only (legacy behavior).
    /// </summary>
    public bool EnableUplinkAvatarDelta = true;
    /// <summary>
    /// Hold shared images in server RAM so a joining player is handed them immediately, instead of
    /// the original sharer having to re-upload every image to each arrival. Costs memory; saves the
    /// sharer's uplink and gets pictures on the wall far sooner in a busy instance.
    /// </summary>
    public bool ImageCacheEnabled = true;
    /// <summary>
    /// Ceiling on the image cache, in megabytes. Set 0 to hold nothing (equivalent to disabling the
    /// cache). This is a hard cap on retained payloads, not a target.
    /// </summary>
    public int ImageCacheMaxMegabytes = 512;
    /// <summary>
    /// Floor on one player's slice of the cache, in megabytes. The buffer is divided evenly between
    /// everyone currently holding images so nobody can crowd anyone else out; without a floor a busy
    /// instance would shrink each share below a single image and cache nothing at all. An owner over
    /// their share evicts their own oldest image, never another player's.
    /// </summary>
    public int ImageCacheMinimumPerOwnerMegabytes = 32;
    /// <summary>
    /// Server egress one sharing player may spend on image replication, in megabits per second.
    ///
    /// A shared image is relayed to every player who is not on a direct P2P link, so the server
    /// forwards it once per recipient: this budget divided by the fan-out is the rate the sharer
    /// actually uploads at. The client cannot see how much pipe a server has, so before this was
    /// advertised it assumed a deliberately timid 4 Mb/s and a busy instance took minutes to move
    /// one picture no matter how fast either end was. Set it to the share of the uplink an image
    /// transfer may occupy and the sharer will use it.
    ///
    /// Sized per sharer, not in total, so several people sharing at once can cost this much each.
    /// The worst case is this times the number of simultaneous sharers; on a small pipe divide
    /// accordingly. 0 leaves the client on its own conservative default.
    /// </summary>
    public int ImageShareEgressMegabitsPerSecond = 200;

    /// <summary>
    /// Rate the server replays cached images to ONE arriving player, in megabits per second.
    ///
    /// This is the download side of image sharing, and it is the server's own send — not something
    /// a client can be trusted to pace, because the client never asked for it. When somebody joins,
    /// the cache hands them every image the room already holds so the original sharers do not have
    /// to send them all again. That replay used to go out in a single synchronous burst: an
    /// instance sitting near the cache ceiling would push hundreds of megabytes into one peer's
    /// reliable queue the moment it connected, which is a bad first ten seconds for that player and
    /// a spike in server memory for everyone.
    ///
    /// Sized per arriving player, so several joining at once cost this much each — the join burst
    /// after a restart is the case to size for. 0 = unpaced, which restores the old behaviour.
    /// </summary>
    public int ImageShareDownloadMegabitsPerSecond = 200;

    /// <summary>
    /// Headroom the server-side egress backstop allows over
    /// <see cref="ImageShareEgressMegabitsPerSecond"/> before it starts dropping, as a percentage.
    ///
    /// The advertised budget and the enforced one must not be the same number. A well-behaved
    /// client paces itself to the advertised figure, but its accounting is not the server's: it
    /// measures against its own clock, rounds chunks differently, and bursts across a tick
    /// boundary. Enforcing at exactly the advertised rate would break honest transfers on jitter
    /// alone, which is far worse than the abuse it is meant to stop.
    ///
    /// 150 = drop only once a sender is sustaining half again what it was told it could have, which
    /// no honest client does and no rate-limited one can hide behind.
    /// </summary>
    public int ImageShareEgressEnforcementPercent = 150;

    /// <summary>
    /// Maximum world-space distance, in metres, at which a player is eligible to receive an image
    /// pickup. Advertised to clients and applied by the sharing client, so it is a bandwidth budget
    /// rather than an access control - nothing here or on the receiver rejects an out-of-range image.
    ///
    /// The server image cache never learns where anybody is. It offers a joiner the spawn header of
    /// each image it holds - tens of bytes - and that client measures the distance itself and asks for
    /// the ones it wants, picking up the rest as it walks toward them. 0 is unlimited: every player
    /// receives every image, which is how it behaved before this setting.
    /// </summary>
    public float ImagePickupRangeMeters = 64f;

    public bool EnableBSRProfiling = false;

    /// <summary>
    /// Log the per-connection authentication handshake ("Processing connection from peer N",
    /// "Sending out Writer with size : N").
    ///
    /// <para>Off by default because it is two lines per joiner on a path that is walked once per
    /// join, and a mass rejoin walks it for everybody at once — a 2000-player instance coming back
    /// after a restart writes four thousand lines that say nothing a reader can act on. The line
    /// that matters, "Peer connected: N", is always logged, as is every rejection, so the default
    /// still shows who got in and who did not. Turn this on to trace a handshake that is failing
    /// between those two points.</para>
    /// </summary>
    public bool LogConnectionHandshake = false;
    /// <summary>
    /// Worker cap for the BSR tick's parallel phases (send loop, message processing, distance
    /// sweep). 0 = auto (a quarter of the cores, floored at 4 and capped at 8).
    ///
    /// More workers is not free: the tick runs ~275x/s, so each phase pays dispatch and wake cost
    /// per tick per worker, and every extra thread adds GC poll-point traffic. Measured at 500
    /// players on a 32-thread box: 32 workers = 11.0 cores, 16 = 8.6, 8 = 6.6, 4 = 6.4, at equal
    /// or better throughput. Raise it if you run far more players per instance than that and the
    /// profile shows the send loop itself saturating.
    /// </summary>
    public int BSRMaxDegreeOfParallelism = 0;
    /// <summary>
    /// Share of the BSR tick period the send pass is sized against, as a percentage. 0 = the
    /// fitted default of 60. Clamped to 20..85.
    ///
    /// The send pool's width comes from a throughput rate this host measures for itself, so what
    /// is left to choose is not how fast a worker is but how many of the period's milliseconds the
    /// pass may spend - that many pairs per millisecond, for that many milliseconds, is a worker
    /// count. The remainder is not spare: the queue drain, message processing, the distance slice
    /// and the transport kick run in the same tick, and what those cost is a property of the box,
    /// which is why this is a setting rather than the constant it used to be. Too high and the
    /// send pass fits its budget while the tick overruns anyway, which the load controller answers
    /// by shedding players; too low and the pool is sized wider than the machine while the tick
    /// sits half idle.
    ///
    /// Nothing in the process can fit this, because the send pass has no view of what the phases
    /// beside it cost. BasisServerBenchmark measures that split under load and writes the value.
    /// </summary>
    public int BSRSendPhaseBudgetPercent = 0;
    /// <summary>
    /// Furthest the reduction system may slice its roster under load. 0 = scale with population.
    ///
    /// Slicing is the last-resort lever: at slice N each tick serves only 1/N of the receivers, so
    /// everyone's update rate drops uniformly. The cap decides how far the server is allowed to
    /// degrade before it stops degrading and simply overruns its tick instead.
    ///
    /// It was a fixed 32, chosen when 2000 was a large instance. At 8000 players a cap of 32 still
    /// leaves 250 receivers per tick, so a struggling server reaches the ceiling with nowhere left
    /// to go and starts missing the period — which is the failure slicing exists to prevent.
    /// Automatic holds the per-tick fan-out roughly flat as population grows.
    ///
    /// Set a positive value only to pin it; higher caps trade update rate for keeping the tick.
    /// </summary>
    public int BSRMaxSliceCount = 0;
    /// <summary>
    /// Opus voice frame duration pushed to every client (20 or 40 ms). 20 is the low-latency
    /// default; 40 halves the voice packet rate (25/s instead of 50/s) and with it the
    /// per-packet UDP/header overhead — roughly a third of voice wire cost — at the price of
    /// +20 ms voice latency. Admins can still change it live; this is only the boot default.
    /// </summary>
    public int VoiceFrameDurationMs = 20;
    public bool DisallowHeadless = false;

    // Global lockout defaults applied at server boot. Users need the matching
    // basis.resource.lockbypass.{avatar,prop,world} permission to load while locked.
    public bool AvatarsLocked = false;
    public bool PropsLocked = false;
    public bool WorldsLocked = true;
    /// <summary>
    /// When true, peers may not share saved-server entries through the content
    /// share system. Toggled live via the admin panel and persisted to config.xml
    /// alongside the other content lockouts. Default off so existing deployments
    /// behave as before.
    /// </summary>
    public bool ServersLocked = false;
    /// <summary>
    /// When true, the server tells every client to hard-disable the desktop third-person
    /// camera. Toggled live via the admin panel and persisted to config.xml alongside the
    /// other content lockouts. Default off so existing deployments behave as before.
    /// </summary>
    public bool ThirdPersonDisabled = false;
    /// <summary>
    /// When true, the server strips AdditionalAvatarDatas (blendshapes, custom-behaviour
    /// params) from every inbound avatar sync message before propagating to other peers.
    /// Muscle/position/rotation still sync normally; only the additional-data payload is
    /// dropped. Toggled live via the admin panel and persisted alongside the other
    /// content lockouts. Default off.
    /// </summary>
    public bool AdditionalAvatarDataLock = false;
    /// <summary>
    /// Per-category bitmask of camera photo-metadata embedding categories disallowed for all
    /// clients. 0 = everything allowed (default). Seeds BasisGlobalLockManager at boot and is
    /// broadcast to clients in GlobalGetLockState.
    /// </summary>
    public byte CameraMetadataDisallowMask = 0;
    public bool CrashReportingEnabled = true;
    public float MaxMicrophoneRangeMeters = 25f;
    public float MaxHearingRangeMeters = 25f;
    public float MinAvatarEyeHeightMeters = 0.1f;
    public float MaxAvatarEyeHeightMeters = 100f;
    public int MaxContentSpheresPerPlayer = 32;
    /// <summary>
    /// Most distinct network ids one player may register in a session. Every synced object (prop,
    /// synced transform, image manager) claims one from a shared 65,536-wide space that is only
    /// reclaimed when the instance empties, so without a per-player ceiling one client can exhaust
    /// the whole space and lock everyone else out of registering objects. Defaults to half the
    /// space, which no honest client approaches; raise it only if a single user legitimately spawns
    /// tens of thousands of networked objects. 0 or negative restores the generous default rather
    /// than removing the cap.
    /// </summary>
    public int MaxNetworkIdsPerPlayer = 32768;
    /// <summary>
    /// Most loaded resources (props/worlds spawned via LoadResource) one player may hold at once.
    /// Each retained entry keeps the resource's URL and metadata in server RAM and is rebroadcast to
    /// every client, so an uncapped client can exhaust memory and flood the relay. Sized well above
    /// normal heavy use (thousands per player); raise it for instances that legitimately hold more.
    /// 0 or negative restores the default rather than removing the cap.
    /// </summary>
    public int MaxLoadedResourcesPerPlayer = 16384;
    /// <summary>
    /// Opt-in server-side ceiling on NON-image scene-relay egress one player may spend, in megabits
    /// per second, charged on fan-out (payload times recipients). Image traffic is metered
    /// separately by the image bandwidth governor. 0 (default) disables it entirely and preserves
    /// the historical behaviour — a legitimate instance's scene-traffic ceiling is deployment
    /// specific, so this is off until an operator sets a value. Set it as a backstop against a
    /// modified client broadcasting arbitrary scene payloads to the whole room.
    /// </summary>
    public int MaxSceneRelayMegabitsPerSecondPerPlayer = 0;
    public bool PlayspaceMoverLocked = false;
    public bool DirectConnectLocked = false;
    /// <summary>
    /// When true, every client blocks sandboxed Cilbox code on avatars from running; props and
    /// worlds keep their own. Seeds BasisGlobalLockManager at boot and can be toggled live from the
    /// admin panel; the state is broadcast to clients in GlobalGetLockState. Default off.
    /// </summary>
    public bool CilboxLocked = false;
    /// <summary>
    /// When true, non-bypass clients cannot share new image pickups and won't accept inbound ones.
    /// Enforced client-side — image pickups ride the generic scene relay, so the server can't single
    /// them out the way it blocks content shares. Seeds BasisGlobalLockManager at boot, can be toggled
    /// live from the admin panel, and is broadcast to clients in GlobalGetLockState. Default off.
    /// </summary>
    public bool ImagesLocked = false;
    /// <summary>
    /// When false (default) clients two-bone-IK anchor remote avatars' tracked hands/feet to their sent
    /// world targets so they stop sliding; when true, every client falls back to pure-FK playback for
    /// remotes. Seeds BasisGlobalLockManager at boot, can be toggled live from the admin panel, and is
    /// broadcast to clients in GlobalGetLockState. Default off (feature on).
    /// </summary>
    public bool EndEffectorIKDisabled = false;
    /// <summary>
    /// When true, the server refuses to relay text chat messages and typing state from peers
    /// lacking basis.chat.lockbypass. Enforced server-side — text chat has its own channel, so a
    /// modified client cannot talk past the lock. Seeds BasisGlobalLockManager at boot, can be
    /// toggled live from the admin panel, and is broadcast to clients in GlobalGetLockState so
    /// their composers grey out. Default off.
    /// </summary>
    public bool TextChatLocked = false;
    /// <summary>
    /// When true, the server refuses to relay voice (normal and announce) from peers lacking
    /// basis.voice.lockbypass. Enforced server-side — voice has its own channels, so a modified
    /// client cannot talk past the lock. Seeds BasisGlobalLockManager at boot, can be toggled live
    /// from the admin panel, and is broadcast to clients in GlobalGetLockState so they also stop
    /// transmitting rather than burning upstream bandwidth into a dropped stream. Default off.
    /// </summary>
    public bool VoiceChatLocked = false;
    /// <summary>
    /// When true, non-bypass clients neither load new media player URLs nor accept inbound ones.
    /// Enforced client-side — media player state rides the generic scene relay, so the server can't
    /// single it out the way it blocks content shares. Already-playing media keeps playing until
    /// replaced. Seeds BasisGlobalLockManager at boot and is broadcast in GlobalGetLockState.
    /// Default off.
    /// </summary>
    public bool MediaPlayerLocked = false;
    /// <summary>
    /// When true, non-bypass clients cannot capture photos with the handheld camera. Enforced
    /// client-side — capture is entirely local, so nothing reaches the server to block. Distinct
    /// from CameraMetadataDisallowMask, which only strips embedded metadata from photos that are
    /// still taken. Seeds BasisGlobalLockManager at boot and is broadcast in GlobalGetLockState.
    /// Default off.
    /// </summary>
    public bool CameraCaptureLocked = false;
    /// <summary>
    /// When true, non-bypass clients cannot pick up or grab props. Enforced client-side — grabbing
    /// is local interaction logic, and the resulting motion rides ordinary transform sync the server
    /// can't distinguish from any other movement. Distinct from PropsLocked, which blocks prop
    /// *loading* rather than handling already-spawned ones. Default off.
    /// </summary>
    public bool PropGrabbingLocked = false;
    /// <summary>
    /// When true, clients render other players' display names with rich-text markup stripped and
    /// TMP rich text disabled on the nameplate. Enforced client-side. Default off.
    /// </summary>
    public bool SafeDisplayNamesForced = false;

    // ── REST API ──────────────────────────────────────────────────────────────
    /// <summary>Set to true to enable the REST management API.</summary>
    public bool ApiEnabled = false;
    public string ApiHost = "localhost";
    public ushort ApiPort = 10667;
    /// <summary>Bearer token required on every API request. Empty string disables the API even if ApiEnabled is true.</summary>
    public string ApiKey = "";
    /// <summary>
    /// Read config from file. If no file is found create a default config file at filePath.
    /// Also loads per-transport config sidecars from <c>{configDir}/transports/{stackId}.xml</c>.
    /// </summary>
    public static Configuration LoadFromXml(string filePath)
    {
        RuntimeHelpers.RunClassConstructor(typeof(BasisNetworkStackRegistry).TypeHandle);

        Configuration result;
        var serializer = new XmlSerializer(typeof(Configuration));
        if (File.Exists(filePath))
        {
            using (var fileReader = new StreamReader(filePath))
            {
                result = (Configuration)serializer.Deserialize(fileReader);
            }

            // Heal an older config: if it predates the current schema version or is missing
            // any setting we now write, re-save it so the new settings (with defaults and
            // doc comments) are added without disturbing the values already present.
            if (BasisConfigXmlDocs.NeedsUpgrade(filePath, typeof(Configuration), result))
            {
                BNL.Log($"{filePath} is from an older version; adding missing settings.");
                result.WriteXml(filePath);
            }
        }
        else
        {
            BNL.Log($"{filePath} not found, creating with default values");
            result = new Configuration();
            result.WriteXml(filePath);
        }

        string configDir = Path.GetDirectoryName(filePath);
        BasisTransportConfigStore.LoadAll(configDir);
        return result;
    }

    /// <summary>
    /// Persist this configuration back to <paramref name="filePath"/>. Used by the
    /// admin panel to make in-game changes (server name, MOTD, allowlist mode)
    /// survive a restart. Writes via a sibling temp file + atomic move so a crash
    /// mid-write doesn't corrupt the live config.
    /// </summary>
    public void SaveToXml(string filePath)
    {
        WriteXml(filePath);
        BasisTransportConfigStore.SaveAll(Path.GetDirectoryName(filePath));
    }

    /// <summary>
    /// Atomically write just this config.xml (temp file + replace), stamping the current
    /// schema version and injecting doc comments. Does not touch the transport sidecars.
    /// </summary>
    private void WriteXml(string filePath)
    {
        ConfigVersion = CurrentConfigVersion;
        var serializer = new XmlSerializer(typeof(Configuration));
        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = filePath + ".tmp";
        using (var writer = new StreamWriter(tempPath))
        {
            BasisConfigXmlDocs.Serialize(serializer, typeof(Configuration), this, writer);
        }
        if (File.Exists(filePath)) File.Replace(tempPath, filePath, null);
        else File.Move(tempPath, filePath);
    }

    /// <summary>
    /// Resolve the canonical config.xml path under <c>{BaseDirectory}/{ConfigFolderName}/config.xml</c>
    /// — same path the bootstrappers (BasisServerConsole.Program / Unity host runner) read on startup.
    /// </summary>
    public static string GetDefaultPath()
    {
        return Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, ConfigFolderName, "config.xml");
    }

    /// <summary>
    /// This code will override what is written in the config.xml if it finds
    /// an Environmental Variable with the same name as a public config field.
    ///
    /// On windows you can test this in the console:
    ///    $env:PeerLimit = "256"
    ///   .\BasisNetworkConsole.exe
    /// But it is intended to allow Linux admins to override defaults during launch.
    /// </summary>
    public void ProcessEnvironmentalOverrides()
    {
        ApplyEnvironmentalOverridesTo(this);
    }

    /// <summary>
    /// Settings established once during boot — socket binds, the transport stack, the health and
    /// API listeners, the console, and disk support. Editing one persists and takes effect on the
    /// next start; everything else is re-applied live by NetworkServer.ApplyLiveConfiguration.
    /// </summary>
    private static readonly string[] RestartOnlyFields =
    {
        nameof(SetPort),
        nameof(IPv4Address),
        nameof(IPv6Address),
        nameof(OverrideAutoDiscoveryOfIpv),
        nameof(NetworkStackId),
        nameof(HasFileSupport),
        nameof(EnableStatistics),
        nameof(EnableConsole),
        nameof(HealthCheckHost),
        nameof(HealthCheckPort),
        nameof(HealthPath),
        nameof(ApiEnabled),
        nameof(ApiHost),
        nameof(ApiPort),
        nameof(ApiKey),
    };

    /// <summary>Whether a field only takes effect after a restart. See <see cref="RestartOnlyFields"/>.</summary>
    public static bool RequiresRestart(string fieldName) =>
        Array.IndexOf(RestartOnlyFields, fieldName) >= 0;

    /// <summary>
    /// Settings a connected client is told about at join time only, so an edit reaches new joiners
    /// but leaves the existing crowd on the value they connected with.
    /// </summary>
    public static bool AppliesToNewJoinsOnly(string fieldName) =>
        fieldName == nameof(BSRSlowestSendRate);

    /// <summary>Field names whose values must never reach the log.</summary>
    public static bool IsSecretFieldName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return false;
        return fieldName.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
            || fieldName.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0
            || fieldName.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
            || fieldName.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ApplyEnvironmentalOverridesTo(object target)
    {
        if (target == null) return;
        Type type = target.GetType();
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            if (!field.FieldType.IsPrimitive && field.FieldType != typeof(string) && field.FieldType.IsClass)
            {
                object nested = field.GetValue(target);
                if (nested != null) ApplyEnvironmentalOverridesTo(nested);
                continue;
            }

            string value = Environment.GetEnvironmentVariable(field.Name);
            if (value == null) continue;

            BNL.Log($"Applying Environmental Override with Field:{field.Name} Value:{(IsSecretFieldName(field.Name) ? "<redacted>" : value)}");

            if (field.FieldType == typeof(int))
            {
                if (int.TryParse(value, out int number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to int. Failed Override");
            }
            else if (field.FieldType == typeof(ushort))
            {
                if (ushort.TryParse(value, out ushort number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to ushort. Failed Override.");
            }
            else if (field.FieldType == typeof(float))
            {
                if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to float. Failed Override.");
            }
            else if (field.FieldType == typeof(string))
            {
                field.SetValue(target, value);
            }
            else if (field.FieldType == typeof(bool))
            {
                if (bool.TryParse(value, out bool boolResult)) field.SetValue(target, boolResult);
                else BNL.LogWarning($"Could not parse '{value}' as bool for field {field.Name}. Failed Override");
            }
            else
            {
                BNL.LogWarning($"Environmental variable type could not be processed for Config Field:{field.Name} Value:{value}");
            }
        }
    }
}
