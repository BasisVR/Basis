using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Basis.Network.Core
{
    /// <summary>
    /// Serializes a config object with <see cref="XmlSerializer"/> and then injects
    /// human-readable XML comments before each element, so the generated/saved config
    /// files document themselves. Comments are written on every save (default-create and
    /// admin-panel save), so they persist across restarts and saves. Reads ignore comments.
    /// A config type with no registered docs is written exactly as XmlSerializer would.
    /// </summary>
    public static class BasisConfigXmlDocs
    {
        public sealed class FieldDoc
        {
            public readonly string Field;
            public readonly string Comment;
            public readonly string Section;
            public FieldDoc(string field, string comment, string section = null)
            {
                Field = field;
                Comment = comment;
                Section = section;
            }
        }

        private sealed class TypeDoc
        {
            public string Header;
            public readonly List<FieldDoc> Fields = new List<FieldDoc>();
        }

        private static readonly Dictionary<Type, TypeDoc> _docs = new Dictionary<Type, TypeDoc>();

        static BasisConfigXmlDocs()
        {
            RegisterServerConfig();
            RegisterLnlConfig();
        }

        /// <summary>Serialize <paramref name="value"/> to <paramref name="writer"/> with doc comments injected for <paramref name="type"/>.</summary>
        public static void Serialize(XmlSerializer serializer, Type type, object value, TextWriter writer)
        {
            var doc = new XDocument();
            using (var xw = doc.CreateWriter())
            {
                serializer.Serialize(xw, value);
            }
            Inject(doc, type);
            doc.Declaration = new XDeclaration("1.0", "utf-8", null);
            doc.Save(writer);
        }

        private static void Inject(XDocument doc, Type type)
        {
            if (doc.Root == null || !_docs.TryGetValue(type, out TypeDoc entry)) return;

            var byField = new Dictionary<string, FieldDoc>();
            foreach (FieldDoc f in entry.Fields) byField[f.Field] = f;

            foreach (XElement el in new List<XElement>(doc.Root.Elements()))
            {
                if (!byField.TryGetValue(el.Name.LocalName, out FieldDoc info)) continue;
                if (!string.IsNullOrEmpty(info.Section)) el.AddBeforeSelf(new XComment(info.Section));
                if (!string.IsNullOrEmpty(info.Comment)) el.AddBeforeSelf(new XComment(info.Comment));
            }

            if (!string.IsNullOrEmpty(entry.Header)) doc.Root.AddFirst(new XComment(entry.Header));
        }

        private const string VersionFieldName = "ConfigVersion";
        private const string CurrentVersionFieldName = "CurrentConfigVersion";

        /// <summary>
        /// True when the config on disk predates the current schema and should be re-saved:
        /// either its stamped <c>ConfigVersion</c> is below the type's <c>CurrentConfigVersion</c>,
        /// or the file is missing any element the current type would write.
        /// </summary>
        public static bool NeedsUpgrade(string filePath, Type type, object loaded)
        {
            if (ReadVersion(loaded) < ReadCurrentVersion(type)) return true;
            return IsMissingAnyField(filePath, type);
        }

        /// <summary>True if the XML at <paramref name="filePath"/> lacks any element the current shape of <paramref name="type"/> would serialize.</summary>
        public static bool IsMissingAnyField(string filePath, Type type)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                XDocument doc = XDocument.Load(filePath);
                if (doc.Root == null) return false;

                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (XElement el in doc.Root.Elements()) present.Add(el.Name.LocalName);

                foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsInitOnly || f.IsLiteral) continue;
                    if (Attribute.IsDefined(f, typeof(XmlIgnoreAttribute))) continue;
                    if (!present.Contains(f.Name)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Stamp the instance <c>ConfigVersion</c> field (if present) up to the type's <c>CurrentConfigVersion</c>.</summary>
        public static void StampVersion(object config)
        {
            if (config == null) return;
            Type type = config.GetType();
            FieldInfo f = type.GetField(VersionFieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) f.SetValue(config, ReadCurrentVersion(type));
        }

        /// <summary>Schema version found in a just-deserialised config; 0 when it predates versioning.</summary>
        public static int ReadVersion(object config)
        {
            if (config == null) return 0;
            FieldInfo f = config.GetType().GetField(VersionFieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(config);
            return 0;
        }

        private static int ReadCurrentVersion(Type type)
        {
            FieldInfo f = type.GetField(CurrentVersionFieldName, BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.IsLiteral && f.FieldType == typeof(int)) return (int)f.GetRawConstantValue();
            return 0;
        }

        private static void RegisterServerConfig()
        {
            var t = new TypeDoc
            {
                Header = " Basis dedicated-server configuration. Any environment variable whose name matches a field below overrides it at launch (e.g. PeerLimit=256). These comments are emitted from code, so they survive restarts AND in-game admin-panel saves. ",
            };
            t.Fields.Add(new FieldDoc("ConfigVersion", " Config schema version, managed automatically. When the server gains new settings this file is rewritten to add them (with their defaults) and this number is bumped — don't edit by hand. "));
            t.Fields.Add(new FieldDoc("PeerLimit", " Maximum number of simultaneously connected peers (players). int. Default 65535. ", " ===== Networking / listener ===== "));
            t.Fields.Add(new FieldDoc("SetPort", " UDP port the server binds and listens on; clients connect to this. ushort, range 1-65535. "));
            t.Fields.Add(new FieldDoc("ServerName", " Display name shown as the row title in client server-list UIs (server-info query). string. "));
            t.Fields.Add(new FieldDoc("ServerMotd", " Short message-of-the-day returned alongside the server name. string; empty = none. "));
            t.Fields.Add(new FieldDoc("EnableStatistics", " Collect transport statistics (per-peer/packet counters) and run the stats worker; surfaced via the health endpoint. true|false. "));
            t.Fields.Add(new FieldDoc("HasFileSupport", " Master switch for writing data to disk: server logs, on-disc moderation lists, auth-identity persistence, chat file support. Set false for an in-memory/ephemeral server. true|false. "));
            t.Fields.Add(new FieldDoc("HealthCheckHost", " Host/interface the HTTP health endpoint binds. string (hostname or IP). ", " ===== Health-check HTTP endpoint ===== "));
            t.Fields.Add(new FieldDoc("HealthCheckPort", " Port for the HTTP health endpoint. ushort, range 1-65535. "));
            t.Fields.Add(new FieldDoc("HealthPath", " URL path served by the health endpoint, e.g. /health. string. "));
            t.Fields.Add(new FieldDoc("HealthIncludeBSRProfiling", " Add a 'bsr' object to the health endpoint response carrying Server Reduction System performance metrics: live tick load (mean tick ms, overrun ratio, tick period, load-shed tier, slicing) plus the last closed 5-second profiling window (per-phase ms/tick, message/send counts, bundle compression). Turning this on starts profiling collection on its own, so EnableBSRProfiling is not required. It also stops the '[BSR Profile]' and '[BSR] Load:' lines being written to the log — the endpoint is serving the same numbers, so they are not duplicated to disc. The endpoint is unauthenticated, so leave this off unless the health port is restricted to trusted callers. true|false; default false. "));
            t.Fields.Add(new FieldDoc("IdleMemoryReclaimEnabled", " Collect and hand memory back to the OS after the crowd leaves. true|false; default true. An emptied server allocates almost nothing, and collection is demand-driven, so without this nothing triggers a collection and everything the session used stays resident - a 1000-player instance that empties keeps its whole peak footprint until somebody joins again. When the population has fallen to a quarter of its peak and stayed there, an empty server gets a blocking compacting gen2 (nobody is connected to pause) and a still-occupied one gets a background gen2, which frees the same garbage without stopping the players who stayed. Turn it off only if you would rather the memory stayed mapped for the next crowd. ", " ===== Memory ===== "));
            t.Fields.Add(new FieldDoc("IdleMemoryReclaimSettleSeconds", " How long the population must stay down before the collection runs, in seconds. int; default 30. Departing players are torn down over several seconds - the reduction system retires a bounded number of them per tick - so collecting the instant the last one leaves would run while their state is still live and reclaim little. Raise it on a server whose crowd churns in and out. "));
            t.Fields.Add(new FieldDoc("IdleMemoryReclaimMinimumPeak", " Smallest peak population worth collecting after, in players. int; default 8. A server that never held more than a handful of people has nothing to hand back, and the collection is not free. "));
            t.Fields.Add(new FieldDoc("BSRSMillisecondDefaultInterval", " Base send interval in milliseconds at zero distance. 50 ms ~= 20 Hz. Lower = more frequent updates and more bandwidth. int (ms). ", " ===== Server Reduction System (avatar sync rate) =====  intervalMs = BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + distance^2 * BSRSIncreaseRate). Nearby players update fast, distant players progressively slower. "));
            t.Fields.Add(new FieldDoc("BSRBaseMultiplier", " Flat multiplier applied to the base interval before distance scaling. number. Default 1. "));
            t.Fields.Add(new FieldDoc("BSRSIncreaseRate", " How quickly the interval grows with squared distance; higher = distant players update much less often. float. Default 0.005. "));
            t.Fields.Add(new FieldDoc("BSRSlowestSendRate", " Slowest send-rate floor handed to clients for very distant peers. float; a value of 0 is treated as unset and replaced with 2.55. "));
            t.Fields.Add(new FieldDoc("DistanceUpdateIntervalTicks", " How many ticks one full refresh of the distance cache is spread over. The cache holds the send interval and quality tier for every pair, and the send loop reads it rather than recomputing distance; this decides how stale those decisions may get. Lower = the tier a pair is served at tracks the distance between them more closely, at the cost of more sweep work per tick. The sweep is O(players^2) per refresh, so the cost of lowering it grows quadratically with population. int (ticks); 125 at a 20 ms tick is a refresh every ~2.5 s. ", null));
            t.Fields.Add(new FieldDoc("EnableComputeOffload", " Whether the distance sweep may run on a GPU when one is present. Falls back to the CPU sweep whenever there is no device, no BasisNetworkCompute.dll beside the server, the device refuses to initialise, or the backend disagrees with the CPU on a startup spot-check - so leaving this on costs nothing on a host without a GPU. What it buys is mostly freshness rather than CPU: the sweep is a small share of a broadcast server's time, but it is what sets how stale the per-pair quality tier may get, and a cheaper sweep affords a lower DistanceUpdateIntervalTicks. bool. ", null));
            t.Fields.Add(new FieldDoc("ComputeDevice", " Which compute device the distance sweep may use, when EnableComputeOffload is on and this host has more than one. Empty picks the best device present, preferring a CUDA one and then the largest memory. An integer picks by position in the list the server logs at startup. Anything else is matched against the device name, so \"4090\" or \"Radeon\" is enough. A selector that matches no device is an error and the sweep stays on the CPU rather than silently running somewhere else - naming a card and getting a different one would look exactly like it having worked. string. ", null));
            t.Fields.Add(new FieldDoc("ComputeDistanceUpdateIntervalTicks", " Refresh period for the distance cache while the sweep is running on a compute device, in ticks. Used INSTEAD of DistanceUpdateIntervalTicks, and only for as long as a device is actually carrying the sweep - if none is found, or one is found and then refused for disagreeing with the CPU, the period reverts to DistanceUpdateIntervalTicks on the spot, so a host without a GPU is never asked to keep up with a schedule fitted to one. This is where the offload is actually spent: moving the sweep to a device saves very little CPU, but it makes the sweep cheap enough to run several times more often, and that is what decides how stale the quality tier a pair is served at may get. int (ticks). ", null));
            t.Fields.Add(new FieldDoc("HighQualityDistance", " Distance thresholds (world units/metres) bucketing peers into sync-quality tiers; squared internally. Keep High < Medium < Low. float. "));
            t.Fields.Add(new FieldDoc("MediumQualityDistance", " Medium-quality distance threshold (see HighQualityDistance). float. "));
            t.Fields.Add(new FieldDoc("LowQualityDistance", " Low-quality distance threshold (see HighQualityDistance). float. "));
            t.Fields.Add(new FieldDoc("OverrideAutoDiscoveryOfIpv", " When true, bind exactly the addresses below instead of auto-discovering the IP version. true|false. ", " ===== Address binding ===== "));
            t.Fields.Add(new FieldDoc("IPv4Address", " IPv4 address to bind. 0.0.0.0 = all IPv4 interfaces. string. "));
            t.Fields.Add(new FieldDoc("IPv6Address", " IPv6 address to bind. ::1 = loopback, :: = all IPv6 interfaces. string. "));
            t.Fields.Add(new FieldDoc("Password", " Password clients must present to join. Change this for any non-local server! string. ", " ===== Authentication ===== "));
            t.Fields.Add(new FieldDoc("UseAuth", " Require the join password (above) to be correct. true|false. "));
            t.Fields.Add(new FieldDoc("UseAuthIdentity", " Require cryptographic player-identity (DID) verification in addition to the password. true|false. The headless load-test client console supports this, so it can stay enabled. "));
            t.Fields.Add(new FieldDoc("NetworkStackId", " Transport stack id. Empty = the default ('litenetlib'); only 'litenetlib' is registered and unknown ids fall back to it. Per-stack tuning lives in config/transports/<id>.xml. string. "));
            t.Fields.Add(new FieldDoc("BasisUserRestrictionMode", " Player join restriction mode. Allowed values: Normal | BanList | AllowList | RejoinOnly. RejoinOnly locks the server to the players connected when it was enabled (admins may still join) and resets to Normal on restart. "));
            t.Fields.Add(new FieldDoc("HowManyDuplicateAuthCanExist", " How many connections sharing the same auth identity may exist at once. int. "));
            t.Fields.Add(new FieldDoc("AuthValidationTimeOutMiliseconds", " Time a client has to complete auth validation before being dropped. int (ms). "));
            t.Fields.Add(new FieldDoc("EnableConsole", " Enable the interactive server console (CLI input). Set false for headless/daemon deployments. true|false. ", " ===== Console / persistence ===== "));
            t.Fields.Add(new FieldDoc("EnableAvatarBundleCompression", " Bundle per-receiver avatar messages and send them compressed (falls back to uncompressed per-message when not worthwhile); clients must support the matching decoder. true|false. ", " ===== Avatar bundle compression ===== "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinMessages", " Minimum queued avatar messages to one receiver before a bundle is attempted. int. "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinBytes", " Minimum uncompressed bundle size (bytes) before compression is attempted. int. "));
            t.Fields.Add(new FieldDoc("EnableAvatarBundleZstd", " Compress keyframe/full avatar bundles with Zstd against an embedded trained dictionary instead of LZ4 — measured 16.7-18.1% fewer bundle bytes at 250 clients for roughly 2x the compression CPU. Delta-only bundles stay on LZ4 regardless, because Zstd is a 2.8-4.5% loss on those. Inert unless the build embeds a dictionary (check bundles.zstd.dictGeneration on the health endpoint; 0 = none). Clients must support the matching decoder. true|false; default true. "));
            t.Fields.Add(new FieldDoc("AvatarBundleZstdDeltaBundles", " Also send delta-only bundles through Zstd. Measured a 2.8-4.5% loss against LZ4; exists to re-measure the traffic-class split without a rebuild. true|false; default false. "));
            t.Fields.Add(new FieldDoc("AvatarBundleZstdLevel", " Zstd compression level for avatar bundles. Negative levels trade ratio for speed. int; default -2, the measured sweet spot (~17.3% saving at ~2.3x LZ4 CPU). -3 costs meaningfully less CPU for ~15-16%. "));
            t.Fields.Add(new FieldDoc("AvatarBundleZstdMaxShedTier", " Highest BSR load-shed tier (0 healthy .. 2 maximum shedding) at which Zstd is still used; above it every bundle falls back to LZ4. Zstd buys bandwidth with CPU, which is the right trade while the tick has headroom and the wrong one once the server is already shedding avatar quality to keep up. int; default 1. Set 2 to keep Zstd on at every tier, -1 to disable it. "));
            t.Fields.Add(new FieldDoc("EnableAvatarDeltaCompression", " Send periodic full avatar keyframes with per-field deltas between them on DeltaAvatarChannel instead of all-keyframes; clients must support the delta decoder. true|false. ", " ===== Avatar delta compression ===== "));
            t.Fields.Add(new FieldDoc("AvatarDeltaKeyframeIntervalMs", " Base milliseconds between forced full avatar keyframes per sender. Lower = faster recovery from a lost keyframe, higher = less keyframe bandwidth. int; default 500. "));
            t.Fields.Add(new FieldDoc("AvatarDeltaKeyframeMaxIntervalMs", " Ceiling for the adaptive keyframe stretch: while a sender's deltas stay tiny (idle avatar) the keyframe interval doubles up to this value; motion snaps it back to the base. Receivers that miss a keyframe request one on demand. 0 or <= base disables. int (ms); default 2000. "));
            t.Fields.Add(new FieldDoc("StripAdditionalDataAtLowQuality", " Drop AdditionalAvatarData (face blendshapes, custom behaviour params) from the Low and VeryLow avatar tiers — unreadable at those distances; the reliable low-frequency behaviour channel still reaches everyone. High/Medium keep it. true|false; default true. "));
            t.Fields.Add(new FieldDoc("EnableUplinkAvatarDelta", " Accept client-to-server avatar deltas and advertise support: clients upload a full keyframe every ~500 ms plus small deltas in between instead of full frames every packet (60-90% less avatar ingress). false = clients upload full keyframes only. true|false; default true. "));
            t.Fields.Add(new FieldDoc("ImageShareEgressMegabitsPerSecond", " Server egress one sharing player may spend on image replication, in megabits per second. A shared image is relayed once per recipient who is not on a direct P2P link, so this budget divided by the fan-out is the rate the sharer actually uploads at - at the old client-side assumption of 4 Mb/s a twenty-player instance moved a picture at about 25 KB/s. Sized per sharer, so the worst case is this times the number of people sharing at once; divide it down on a small pipe and raise it on a large one. int (Mb/s); 0 leaves the client on its own conservative default; default 200. This figure is BOTH advertised to clients so they pace themselves and enforced server-side so a modified one cannot ignore it - see ImageShareEgressEnforcementPercent. Editable live from the admin panel. "));
            t.Fields.Add(new FieldDoc("ImageShareDownloadMegabitsPerSecond", " Rate the server replays cached images to ONE arriving player, in megabits per second. This is the download side of image sharing and the server's own send: when somebody joins, the cache hands them every image the room already holds so the original sharers do not have to send them again. Unpaced, that replay goes out in a single burst - an instance near the cache ceiling pushes hundreds of megabytes into one peer's queue the moment it connects, which is a bad first ten seconds for that player and a memory spike for everyone. Sized per arriving player, so a join burst after a restart costs this much each. int (Mb/s); 0 = unpaced (the old behaviour); default 200. Editable live from the admin panel. "));
            t.Fields.Add(new FieldDoc("ImageShareEgressEnforcementPercent", " How far over ImageShareEgressMegabitsPerSecond a client may go before the server starts dropping its image traffic, as a percentage. The advertised budget and the enforced one must not be the same number: a well-behaved client paces itself against the advertised figure but measures on its own clock, rounds chunks its own way and bursts across tick boundaries, so enforcing at exactly the advertised rate would break honest transfers on jitter alone - far worse than the abuse it is meant to stop. int; minimum 100, default 150 (drop only once a sender sustains half again what it was told it could have). Editable live from the admin panel. "));
            t.Fields.Add(new FieldDoc("ImagePickupRangeMeters", " Maximum distance in metres between an image pickup and a player for that player to be sent the image. Advertised to clients and applied by the sharing client, so this is a bandwidth budget rather than an access control - nothing on the server or the receiver rejects an out-of-range image. Players entering range later receive a catch-up transfer, from the owner while the server is not holding the image and from the server image cache once it is. The cache never learns where anybody is: it offers each held image's spawn header - tens of bytes - and the receiving client measures the distance itself and asks for the ones it wants. 0 is unlimited - every player receives every image, which is how it behaved before this setting. float (metres); default 64. "));
            t.Fields.Add(new FieldDoc("EnableBSRProfiling", " Emit Server Reduction System profiling output. true|false. "));
            t.Fields.Add(new FieldDoc("LogConnectionHandshake", " Log the per-connection authentication handshake ('Processing connection from peer N' and 'Sending out Writer with size : N'). Off by default: that is two lines per joiner, and a mass rejoin after a restart writes them for the whole population at once - four thousand lines at 2000 players, none of which a reader can act on. 'Peer connected: N' and every rejection are logged regardless, so the default still shows who got in and who did not. Turn this on only to trace a handshake failing between those two points. true|false; default false. "));
            t.Fields.Add(new FieldDoc("BSRMaxSliceCount", " Furthest the Server Reduction System may slice its roster under load. int; 0 = scale with player count, which is recommended. At slice N each tick serves only 1/N of the receivers, so everyone's update rate drops uniformly - it is the last-resort lever, used only after stretching the tick period and shedding distant players. This cap decides how far the server may degrade before it stops degrading and simply starts missing its tick instead. It used to be a fixed 32, chosen when 2000 was a large instance; at 8000 players a cap of 32 still leaves 250 receivers per tick, so a struggling server reaches the ceiling with nowhere left to go. Automatic keeps the per-tick fan-out roughly flat as population grows. Set a positive value only to pin it. "));
            t.Fields.Add(new FieldDoc("BSRMaxDegreeOfParallelism", " Worker cap for the Server Reduction System's parallel phases (send loop, message processing, distance sweep). int; 0 = automatic and recommended. Automatic scales the pool with the player count and caps it at the share of the machine the core allocator has granted this phase - a share whose ceiling is measured at runtime rather than assumed, so it already tracks the hardware. Setting a number here overrides all of that, including the measurement, and is clamped to the core count. The tick runs hundreds of times a second, so every worker costs dispatch and GC-poll traffic per tick; once the per-tick slice is large enough to keep them busy, extra workers cost more than they return. Set it only to hold the server down on a box shared with other services. Watch the 'send N/M workers' figures in the [CPU] log line to see what automatic is choosing. "));
            t.Fields.Add(new FieldDoc("BSRSendPhaseBudgetPercent", " Share of the Server Reduction System's tick period that the send pass is sized against, as a percentage. int; 0 = the fitted default of 60, clamped to 20..85. The width of the send pool is worked out from a throughput rate this host measures for itself - sender/receiver pairs one worker gets through per busy millisecond - so the only thing left to choose is how many of the period's milliseconds that pass may spend, and rate times milliseconds is a worker count. The remainder of the period is not spare: the queue drain, message processing, the distance slice and the transport kick all run in the same tick, and how much they cost is a property of the machine - which is why this is a setting and not a constant. Too high and the send pass comfortably fits its budget while the tick overruns regardless; the load controller then starts shedding players and nothing in the logs points here. Too low and the pool is sized wider than the box while the tick sits half idle. The process cannot fit this itself, because the send pass has no view of what the phases beside it cost, so BasisServerBenchmark measures the split under load and writes the value. By hand: from the [CPU/POP] line take the budget duty times this percentage to get the send pass's share of the tick, subtract it from tick-ms over interval-ms to get what everything else costs, and leave that plus about ten points of headroom out of this number. "));
            t.Fields.Add(new FieldDoc("DisallowHeadless", " Reject headless clients from connecting. true|false. "));
            t.Fields.Add(new FieldDoc("AvatarsLocked", " Block avatar loading for non-bypass users. true|false. ", " ===== Content lockouts (seed BasisGlobalLockManager at boot; each can also be toggled live from the admin panel). Users need the matching basis.resource.lockbypass.{avatar,prop,world} permission to load while locked. ===== "));
            t.Fields.Add(new FieldDoc("PropsLocked", " Block prop loading for non-bypass users. true|false. "));
            t.Fields.Add(new FieldDoc("WorldsLocked", " Block world loading for non-bypass users. true|false. "));
            t.Fields.Add(new FieldDoc("ServersLocked", " Block sharing of saved-server entries through the content-share system. true|false. "));
            t.Fields.Add(new FieldDoc("ThirdPersonDisabled", " Tell every client to hard-disable the desktop third-person camera. true|false. "));
            t.Fields.Add(new FieldDoc("AdditionalAvatarDataLock", " Strip AdditionalAvatarDatas (blendshapes, custom-behaviour params) from inbound avatar sync before relaying; muscle/position/rotation still sync. true|false. "));
            t.Fields.Add(new FieldDoc("CameraMetadataDisallowMask", " Bitmask of camera photo-metadata categories disallowed for all clients (set bit = disallowed). 0 = everything allowed. byte, range 0-255; the category-to-bit mapping is defined client-side. "));
            t.Fields.Add(new FieldDoc("CrashReportingEnabled", " Allow clients to send a one-shot report of each error/exception they hit to the server, stored under CrashReports/<uuid>.jsonl with their UUID and display name. true|false; default true. Set false to globally disable reporting (clients are told to stop sending). ", " ===== Diagnostics ===== "));
            t.Fields.Add(new FieldDoc("MaxMicrophoneRangeMeters", " Maximum microphone (voice transmit) range, in metres, a client may set. Clients clamp their Microphone Range slider and effective range to this ceiling; can also be changed live from the admin panel. float; default 25. ", " ===== Audio / voice range ===== "));
            t.Fields.Add(new FieldDoc("MaxHearingRangeMeters", " Maximum hearing (audio receive) range, in metres, a client may set. Clients clamp their Hearing Range slider and effective range to this ceiling. float; default 25. "));
            t.Fields.Add(new FieldDoc("VoiceFrameDurationMs", " Opus voice frame duration pushed to every client at join. 20 = low-latency default; 40 halves the voice packet rate (25/s vs 50/s) and its per-packet overhead for +20 ms voice latency — worth trying on bandwidth-constrained servers. Admins can still change it live. 20|40; default 20. "));
            t.Fields.Add(new FieldDoc("MinAvatarEyeHeightMeters", " Minimum avatar eye height, in metres, a non-admin player may scale to. Clients clamp their avatar scale to this floor. float; default 0.1 (effectively no minimum). Admins (basis.moderation.globallock) bypass it. ", " ===== Avatar scale + movement restrictions (seed at boot; toggle live from the admin panel). Admins with basis.moderation.globallock bypass these. ===== "));
            t.Fields.Add(new FieldDoc("MaxAvatarEyeHeightMeters", " Maximum avatar eye height, in metres, a non-admin player may scale to. Clients clamp their avatar scale to this ceiling. float; default 100 (effectively no maximum). "));
            t.Fields.Add(new FieldDoc("PlayspaceMoverLocked", " Stop non-admin players from using the playspace mover (grabbing/dragging/rotating/scaling their play space). true|false; default false. "));
            t.Fields.Add(new FieldDoc("DirectConnectLocked", " Refuse to broker direct (peer-to-peer) connections for non-admin players; clients also hide the direct-connect control. true|false; default false. "));
            t.Fields.Add(new FieldDoc("CilboxLocked", " Tell every client to block sandboxed Cilbox code on avatars from running (props/worlds keep their own). true|false; default false. "));
            t.Fields.Add(new FieldDoc("ImagesLocked", " Stop non-bypass clients from sharing new image pickups and from accepting inbound ones. Enforced client-side (image pickups ride the generic scene relay). true|false; default false. "));
            t.Fields.Add(new FieldDoc("EndEffectorIKDisabled", " Tell every client to stop two-bone-IK anchoring remote avatars' tracked hands/feet, falling back to pure-FK playback. true|false; default false (feature on). "));
            t.Fields.Add(new FieldDoc("TextChatLocked", " Drop text chat messages and typing state from peers lacking basis.chat.lockbypass. Enforced server-side, so a modified client cannot talk past it. true|false; default false. "));
            t.Fields.Add(new FieldDoc("VoiceChatLocked", " Drop voice (normal and announce) from peers lacking basis.voice.lockbypass. Enforced server-side, so a modified client cannot talk past it. true|false; default false. "));
            t.Fields.Add(new FieldDoc("MediaPlayerLocked", " Stop non-bypass clients from loading new media player URLs and from accepting inbound ones. Enforced client-side (media state rides the generic scene relay). Already-playing media keeps playing. true|false; default false. "));
            t.Fields.Add(new FieldDoc("CameraCaptureLocked", " Stop non-bypass clients from taking photos with the handheld camera. Enforced client-side (capture is entirely local). Separate from CameraMetadataDisallowMask, which only strips metadata. true|false; default false. "));
            t.Fields.Add(new FieldDoc("SafeDisplayNamesForced", " Render other players' display names with rich-text markup stripped and TMP rich text off. Enforced client-side. Stops name markup being used to draw over the screen. true|false; default false. "));
            t.Fields.Add(new FieldDoc("PropGrabbingLocked", " Stop non-bypass clients from picking up or grabbing props. Enforced client-side (grabbing is local interaction logic). Separate from PropsLocked, which blocks prop loading instead. true|false; default false. "));
            _docs[typeof(global::Configuration)] = t;
        }

        private static void RegisterLnlConfig()
        {
            var t = new TypeDoc
            {
                Header = " LiteNetLib transport tuning (sidecar for the 'litenetlib' network stack). Maps onto LiteNetLib's NetManager. Fields marked [NOT APPLIED] are serialized but not currently wired into the server's NetManager. Comments are emitted from code, so they survive restarts and saves. ",
            };
            t.Fields.Add(new FieldDoc("ConfigVersion", " Config schema version, managed automatically; new settings are added to this file on load — don't edit by hand. "));
            t.Fields.Add(new FieldDoc("UseNativeSockets", " Use OS-native socket calls instead of the managed path (lower overhead). true|false. "));
            t.Fields.Add(new FieldDoc("NatPunchEnabled", " Enable the NAT punch-through module for peer introduction. true|false. "));
            t.Fields.Add(new FieldDoc("NatPortPredictionRange", " Hard-NAT traversal: how many sequential ports above a peer's server-observed external port the OTHER peer also punches (port-prediction spray). Helps sequential symmetric/CGNAT mappings where the peer-to-peer port differs from the one the server sees. 0 disables the spray; sane range 0-128. int. "));
            t.Fields.Add(new FieldDoc("PingInterval", " Interval between keep-alive pings to each peer. int (ms). "));
            t.Fields.Add(new FieldDoc("DisconnectTimeout", " Time with no response from a peer before it is disconnected. int (ms). "));
            t.Fields.Add(new FieldDoc("SimulatePacketLoss", " Artificially drop outgoing packets. true|false. ", " ===== Debug network simulation (testing only) ===== "));
            t.Fields.Add(new FieldDoc("SimulateLatency", " Artificially delay packets. true|false. "));
            t.Fields.Add(new FieldDoc("SimulationPacketLossChance", " Packet-loss percentage when SimulatePacketLoss is true. int, range 0-100. "));
            t.Fields.Add(new FieldDoc("SimulationMinLatency", " Minimum added latency when SimulateLatency is true. int (ms). Keep Min <= Max. "));
            t.Fields.Add(new FieldDoc("SimulationMaxLatency", " Maximum added latency when SimulateLatency is true. int (ms). "));
            t.Fields.Add(new FieldDoc("ReconnectDelay", " [NOT APPLIED] Delay between reconnect attempts (client-side connect option). int (ms). "));
            t.Fields.Add(new FieldDoc("MaxConnectAttempts", " [NOT APPLIED] Connection attempts before giving up (client-side connect option). int. "));
            t.Fields.Add(new FieldDoc("ReuseAddresss", " [NOT APPLIED] Set the SO_REUSEADDR socket option (note the field name spelling). true|false. "));
            t.Fields.Add(new FieldDoc("DontRoute", " [NOT APPLIED] Set the SO_DONTROUTE socket option (bypass routing tables). true|false. "));
            t.Fields.Add(new FieldDoc("IPv6Enabled", " Enable IPv6 (dual-stack) socket support. true|false. "));
            t.Fields.Add(new FieldDoc("MtuOverride", " Force a fixed MTU instead of negotiating. int (bytes); 0 = auto/disabled. "));
            t.Fields.Add(new FieldDoc("MtuDiscovery", " Enable path-MTU discovery. true|false. "));
            t.Fields.Add(new FieldDoc("DisconnectOnUnreachable", " [NOT APPLIED] Disconnect a peer when an ICMP 'unreachable' is received. true|false. "));
            t.Fields.Add(new FieldDoc("AllowPeerAddressChange", " Allow a peer's remote endpoint (IP/port) to change mid-session, e.g. mobile network roaming. true|false. "));
            t.Fields.Add(new FieldDoc("MergeHoldMs", " How long, in milliseconds, a partly-filled packet-merge buffer may wait for more data before being sent. float; 0 = send on every logic pass (legacy). The logic loop runs hundreds of times a second, so flushing every pass emits many half-empty datagrams and the server pays full per-packet cost for each; holding a partial buffer briefly lets consecutive passes coalesce. A buffer that fills the MTU is always sent immediately, so this caps added latency rather than adding it — only small sends ever wait, and never longer than this value. 2-5 is a reasonable range; raise it to cut packet rate further, lower it if voice latency matters more than CPU. "));
            t.Fields.Add(new FieldDoc("CompactMerged", " Frame merged unreliable traffic with the compact per-entry format. true|false; true is recommended. A merged message used to carry four bytes of framing (a two-byte nested length plus its own property and channel bytes); the compact form drops the property byte, since the datagram already says everything inside it is unreliable, and uses a one-byte length for payloads up to 255 - two bytes of framing, or three above 255. Different traffic still shares one MTU-sized datagram, so avatar updates and voice keep riding together. Measured at 500 players: 0.93% less total egress (~4.97 Mbit/s, about 2.24 GB/hour), 0.37% fewer UDP packets, no CPU change, no drops. This is a send-side setting and is safe to change on one end only, because both framings are always decoded; every client able to connect understands it, which the transport protocol id and the server version check together guarantee. Turn it off only to A/B the saving or to rule the framing out while diagnosing something else. "));
            t.Fields.Add(new FieldDoc("MaxUnreliableQueuePerPeer", " Maximum unreliable packets queued per peer before the oldest are dropped. int; 0 = size automatically from player count and available memory, which is recommended. This is the backstop that keeps an overloaded server alive: with no bound at all, a server that cannot drain its send queue grows the backlog instead of shedding, and at 2000 players that backlog reached ~40 GB before every peer timed out. Oldest are dropped first because a newer position update supersedes them. WARNING: this used to be a fixed 256, which is too small to be only a backstop - at 2000 players it discarded roughly half of every avatar update produced, and because discarding is cheaper than sending, the reduction system read the resulting fast ticks as spare capacity and produced even more. Raising it to 4096 on identical load measured zero drops, 22% more delivered bytes and 21% less CPU. Automatic sizes it per box; set a positive value only to pin it for a reproducible measurement. Applies to bulk state traffic only - voice has its own queue and its own bound, see MaxPriorityUnreliableQueuePerPeer. "));
            t.Fields.Add(new FieldDoc("MaxPriorityUnreliableQueuePerPeer", " Maximum voice packets queued per peer before the oldest are dropped. int; 0 = size automatically from player count and available memory, which is recommended. Voice is queued separately from bulk avatar traffic and drained first, so a backlog of position updates can neither delay it nor shed it. That separation is the fix for a real bug: the bulk queue drops oldest-first because a newer avatar update supersedes the one behind it, which is not true of audio, so voice sharing that queue was being destroyed at the bulk stream's drop rate and whatever survived arrived behind the backlog, too late to play. This queue is allowed to be DEEPER than the bulk one, which sounds backwards and is the whole point: bulk depth buys avatar frames that the next frame replaces anyway, while voice depth buys audio that has no replacement. Measured at 1000 clients on a deliberately starved server, moving budget from bulk to voice improved both at once - voice delivered 85.7% to 93.6%, peak RSS 7.8 GB to 4.6 GB. A flat 256 here delivered only 32.8%, because a receiver in a crowd takes a voice packet from every audible talker every frame period and 256 covers single-digit milliseconds of that. Watch droppedVoice on /health, which should stay flat. "));
            t.Fields.Add(new FieldDoc("PeerUpdatePeersPerWorker", " Peers each worker in the transport's per-peer update pass is expected to service. int; 0 = 128. Lower means more workers for the same player count. This is the setting that decides how much of a large machine the server can actually use: the default was fitted to a 32-thread host, and it sizes the pool by population rather than by the machine, so at 4000 peers it picks 31 workers however many cores exist - a 128-core host then sits near a quarter utilisation. Halve it to double the workers. Tune it against the pass time in the [CPU] log line: above 25 ms with cores to spare means this is too high. Machines with many slower cores want a lower value than few fast ones, because each worker gets through fewer peers per pass. "));
            t.Fields.Add(new FieldDoc("PeerUpdateParallelism"," Worker cap for the transport's per-peer update pass. int; 0 = automatic and recommended. Automatic sizes the pool from the peer count and holds it inside the share the core allocator has granted this pass, which moves with load - the pass widens while it is behind and gives the cores back when it recovers. Setting a number here pins the pool and opts out of that, and is clamped to the core count. The pass runs hundreds of times a second and does little work per peer, so letting it spread across every core costs more in thread wake-up and GC-poll traffic than it saves. Watch the 'peer-update N/M workers' figures and the pass time in the [CPU] log line before overriding. "));
            t.Fields.Add(new FieldDoc("MaxSendSockets", " Ceiling on sockets the server may add at runtime when the network path is what limits it. int; 0 = auto (half the CPU cores, floored at 4, never above the core count). Linux only - needs SO_REUSEPORT. Each socket is both an extra send path and an extra receive thread. Sends: the send loop gets SLOWER with more threads on one socket - measured at 1000 players, 8 to 16 to 32 send workers took the update phase from 6.1 to 12.9 to 15.4 ms per tick while throughput fell from 497 to 393 MB/s - so another socket, not another core, is what adds capacity. Receives: one receive thread is one core's worth of syscall throughput, and past that the kernel discards inbound datagrams; this never appears as high CPU because the thread is pinned either way, so it is detected from the kernel's RcvbufErrors counter. On machines with many weak cores the useful range runs to 64 sockets, which is where the auto derivation lands on a 128-core host. Growth needs sustained pressure, except on receive drops which act immediately - and a socket added for drops is then checked: if the drop rate does not fall, growth stops, because the cause is not something more receive threads can fix (raise sysctl net.core.rmem_max, or the link is full). Grow-only. "));
            t.Fields.Add(new FieldDoc("MultiSocketCount", " Number of UDP sockets to bind on the listen port using SO_REUSEPORT (Linux only). 1 = single socket / single receive thread (default). N>1 spawns N-1 extra sockets + receive threads so the Linux kernel RSS-hashes inbound 4-tuples across them; per-peer packet order is preserved because each peer's 4-tuple is stable. On Windows/macOS this falls back to 1 with a warning at Start(). Sensible range: 2-4 around 1k players, 4-8 around 2k. int. "));
            t.Fields.Add(new FieldDoc("PacketPoolSizePerPeer", " Recycled packets kept per connected peer, so the packet pool grows with the crowd instead of hitting a fixed wall. The pool absorbs the gap between a burst of sends draining it and those packets being returned, and that gap scales with peer count: measured at ~2,850 players a fixed 65,536 pool swung between full (recycled packets discarded) and empty (every send allocating), with ~36% of pool requests allocating. The effective ceiling is peers x this value, floored at PacketPoolSize and capped by PacketPoolSizeMax. 0 disables scaling. int. "));
            t.Fields.Add(new FieldDoc("PacketPoolSizeMax", " Hard ceiling on the scaled packet pool, so peer count cannot turn into unbounded memory. Each pooled packet retains its buffer (up to one MTU), so this is roughly the worst-case pool footprint in packets. int; 0 = size automatically from available memory, which is recommended. This used to be a fixed 262144, which is the wall from roughly 5400 peers upward: at 8000 peers the per-peer rule asks for 384000 and every recycle past the cap was discarded for the garbage collector to re-allocate. Set a positive value only to pin it. "));
            _docs[typeof(LNLTransportConfig)] = t;
        }
    }
}
