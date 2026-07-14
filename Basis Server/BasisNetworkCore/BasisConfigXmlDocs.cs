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

        private static int ReadVersion(object config)
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
            t.Fields.Add(new FieldDoc("AnnounceToLan", " Advertise this server to Basis clients on the local network using mDNS/DNS-SD. true|false. Default false. Applied on the next server start. "));
            t.Fields.Add(new FieldDoc("ServerName", " Display name shown as the row title in client server-list UIs (server-info query). string. "));
            t.Fields.Add(new FieldDoc("ServerMotd", " Short message-of-the-day returned alongside the server name. string; empty = none. "));
            t.Fields.Add(new FieldDoc("EnableStatistics", " Collect transport statistics (per-peer/packet counters) and run the stats worker; surfaced via the health endpoint. true|false. "));
            t.Fields.Add(new FieldDoc("HasFileSupport", " Master switch for writing data to disk: server logs, on-disc moderation lists, auth-identity persistence, chat file support. Set false for an in-memory/ephemeral server. true|false. "));
            t.Fields.Add(new FieldDoc("HealthCheckHost", " Host/interface the HTTP health endpoint binds. string (hostname or IP). ", " ===== Health-check HTTP endpoint ===== "));
            t.Fields.Add(new FieldDoc("HealthCheckPort", " Port for the HTTP health endpoint. ushort, range 1-65535. "));
            t.Fields.Add(new FieldDoc("HealthPath", " URL path served by the health endpoint, e.g. /health. string. "));
            t.Fields.Add(new FieldDoc("BSRSMillisecondDefaultInterval", " Base send interval in milliseconds at zero distance. 50 ms ~= 20 Hz. Lower = more frequent updates and more bandwidth. int (ms). ", " ===== Server Reduction System (avatar sync rate) =====  intervalMs = BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + distance^2 * BSRSIncreaseRate). Nearby players update fast, distant players progressively slower. "));
            t.Fields.Add(new FieldDoc("BSRBaseMultiplier", " Flat multiplier applied to the base interval before distance scaling. number. Default 1. "));
            t.Fields.Add(new FieldDoc("BSRSIncreaseRate", " How quickly the interval grows with squared distance; higher = distant players update much less often. float. Default 0.005. "));
            t.Fields.Add(new FieldDoc("BSRSlowestSendRate", " Slowest send-rate floor handed to clients for very distant peers. float; a value of 0 is treated as unset and replaced with 2.55. "));
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
            t.Fields.Add(new FieldDoc("DisableWriteUnlessAdminPersistentFlag", " Reject writes to the persistent key/value store unless the caller is an admin. true|false. "));
            t.Fields.Add(new FieldDoc("DisableReadUnlessAdminPersistentFlag", " Reject reads from the persistent key/value store unless the caller is an admin. true|false. "));
            t.Fields.Add(new FieldDoc("EnableAvatarBundleCompression", " Bundle per-receiver avatar messages and send them compressed (falls back to uncompressed per-message when not worthwhile); clients must support the matching decoder. true|false. ", " ===== Avatar bundle compression ===== "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinMessages", " Minimum queued avatar messages to one receiver before a bundle is attempted. int. "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinBytes", " Minimum uncompressed bundle size (bytes) before compression is attempted. int. "));
            t.Fields.Add(new FieldDoc("EnableAvatarDeltaCompression", " Send periodic full avatar keyframes with per-field deltas between them on DeltaAvatarChannel instead of all-keyframes; clients must support the delta decoder. true|false. ", " ===== Avatar delta compression ===== "));
            t.Fields.Add(new FieldDoc("AvatarDeltaKeyframeIntervalMs", " Base milliseconds between forced full avatar keyframes per sender. Lower = faster recovery from a lost keyframe, higher = less keyframe bandwidth. int; default 500. "));
            t.Fields.Add(new FieldDoc("AvatarDeltaKeyframeMaxIntervalMs", " Ceiling for the adaptive keyframe stretch: while a sender's deltas stay tiny (idle avatar) the keyframe interval doubles up to this value; motion snaps it back to the base. Receivers that miss a keyframe request one on demand. 0 or <= base disables. int (ms); default 2000. "));
            t.Fields.Add(new FieldDoc("StripAdditionalDataAtLowQuality", " Drop AdditionalAvatarData (face blendshapes, custom behaviour params) from the Low and VeryLow avatar tiers — unreadable at those distances; the reliable low-frequency behaviour channel still reaches everyone. High/Medium keep it. true|false; default true. "));
            t.Fields.Add(new FieldDoc("EnableUplinkAvatarDelta", " Accept client-to-server avatar deltas and advertise support: clients upload a full keyframe every ~500 ms plus small deltas in between instead of full frames every packet (60-90% less avatar ingress). false = clients upload full keyframes only. true|false; default true. "));
            t.Fields.Add(new FieldDoc("EnableBSRProfiling", " Emit Server Reduction System profiling output. true|false. "));
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
            t.Fields.Add(new FieldDoc("MultiSocketCount", " Number of UDP sockets to bind on the listen port using SO_REUSEPORT (Linux only). 1 = single socket / single receive thread (default). N>1 spawns N-1 extra sockets + receive threads so the Linux kernel RSS-hashes inbound 4-tuples across them; per-peer packet order is preserved because each peer's 4-tuple is stable. On Windows/macOS this falls back to 1 with a warning at Start(). Sensible range: 2-4 around 1k players, 4-8 around 2k. int. "));
            _docs[typeof(LNLTransportConfig)] = t;
        }
    }
}
