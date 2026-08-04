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
    public const int CurrentConfigVersion = 4;
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
    public int BSRSMillisecondDefaultInterval = 50;
    public int BSRBaseMultiplier = 1;
    public float BSRSIncreaseRate = 0.005f;
    public float BSRSlowestSendRate = 2.55f;
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
    public bool EnableBSRProfiling = false;
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
    /// When true, the server refuses to relay voice (normal and shout) from peers lacking
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

    /// <summary>Field names whose values must never reach the log.</summary>
    private static bool IsSecretFieldName(string fieldName)
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
