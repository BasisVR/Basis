using System.Xml.Linq;

namespace Basis.Config
{
    public static class ConfigManager
    {
        public static string Password = "default_password";
        public static string Ip = "localhost";
        public static int Port = 4296;
        public static int ClientCount = 250;

        public static string AvatarPassword = "default_avatar_password";
        public static string AvatarUrl = "http://localhost/avatar";
        public static int AvatarLoadMode = 1;

        public static bool UseRandomAvatarFromKeyStore = true;
        public static string AvatarKeyStorePath = "";

        // Report a mix of real client platforms instead of "Headless" for every simulated client.
        // Off by default: a load client honestly IS headless, and reporting otherwise makes the user
        // list lie about what is connected. Turn it on when the point of the run is measuring what
        // per-player metadata costs a real mixed crowd, since 1000 identical platform strings compress
        // away across the join fill in a way a real crowd does not.
        public static bool SimulateRealisticPlatforms = false;

        // On by default: real players are calibrated, so identity body-fit scales are the unrealistic
        // case. This also keeps the quantizer exercised across its range rather than at a single value.
        public static bool SimulateBodyFit = true;

        // Radius (metres) of the disc simulated clients spawn across. The server tiers avatar quality
        // and send interval by pair distance, so spawning everyone on one spot measures a worst case
        // no real instance hits. 0 keeps the legacy sub-metre cluster.
        public static float SpawnRadiusMeters = 40f;

        // Voice is a large share of what a real instance costs the server, and a silent crowd hides
        // it completely. Basis culls voice client-side, so each simulated client advertises the peers
        // inside VoiceRangeMeters and then transmits to that list.
        public static bool SimulateVoice = true;
        public static float VoiceRangeMeters = 20f;
        // Share of the crowd that ever transmits; the rest are listening or muted, which is normal.
        public static int VoiceParticipantPercent = 60;
        // Speech is bursty, so participants alternate talking and listening rather than holding the
        // mic open. These ranges set the instantaneous share of speakers: with the defaults a
        // participant talks roughly 2.3 s in every 24 s, so about 6% of the crowd is audible at once.
        public static int VoiceTalkBurstMinMs = 500;
        public static int VoiceTalkBurstMaxMs = 4000;
        public static int VoiceSilenceMinMs = 4000;
        public static int VoiceSilenceMaxMs = 40000;
        // Crowds are correlated: everyone sings happy birthday, cheers, or laughs at once. Independent
        // bursts alone never produce that spike, and the spike is the load worth surviving. A chorus
        // overrides the per-person burst clock for its duration.
        public static bool VoiceChorusEnabled = true;
        public static int VoiceChorusPercent = 85;
        public static int VoiceChorusDurationMinMs = 8000;
        public static int VoiceChorusDurationMaxMs = 25000;
        public static int VoiceChorusIntervalMinMs = 45000;
        public static int VoiceChorusIntervalMaxMs = 180000;
        // How often each client re-derives who can hear it and republishes the list. Real players
        // join and move, so a list built once at connect goes stale; this is the reaction time.
        public static int VoiceRecipientRefreshMs = 2000;
        // Drop a player from the audible set after this long without nearby avatar traffic from them.
        public static int VoiceAudibleTimeoutMs = 6000;
        public static int VoiceFrameMs = 20;
        // Matches the real client's encoder (BasisAudioClipPlayer sets 32000). Frame sizes come from
        // the encoder, so VoiceBytesPerFrame is only the fallback when native Opus is unavailable.
        public static int VoiceBitrate = 32000;
        public static int VoiceBytesPerFrame = 60;
        // Feed a real recording device into the crowd instead of the synthetic sweep, so a listener in
        // the Unity client can judge actual voice quality under load. Route audio into a virtual cable
        // and point VoiceMicrophoneDevice at its recording side.
        public static bool VoiceUseSystemMicrophone = false;
        public static string VoiceMicrophoneDevice = "CABLE Output";

        private static readonly object _lock = new();
        static XElement? Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        static string ReadString(XElement root, string name, string fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback.");
                return fallback;
            }

            var value = el.Value.Trim();
            BNL.Log($"Loaded {name}: [{value}]");
            return value;
        }

        static int ReadInt(XElement root, string name, int fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback {fallback}.");
                return fallback;
            }

            if (!int.TryParse(el.Value, out var value))
            {
                BNL.Log($"Invalid <{name}> value '{el.Value}', using fallback {fallback}.");
                return fallback;
            }

            BNL.Log($"Loaded {name}: {value}");
            return value;
        }

        static bool ReadBool(XElement root, string name, bool fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback {fallback}.");
                return fallback;
            }

            if (!bool.TryParse(el.Value.Trim(), out var value))
            {
                BNL.Log($"Invalid <{name}> value '{el.Value}', using fallback {fallback}.");
                return fallback;
            }

            BNL.Log($"Loaded {name}: {value}");
            return value;
        }

        static float ReadFloat(XElement root, string name, float fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback {fallback}.");
                return fallback;
            }

            if (!float.TryParse(el.Value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                BNL.Log($"Invalid <{name}> value '{el.Value}', using fallback {fallback}.");
                return fallback;
            }

            BNL.Log($"Loaded {name}: {value}");
            return value;
        }

        // ---------------- MAIN ENTRY ----------------

        public static void LoadOrCreateConfigXml(string filePath)
        {
            lock (_lock)
            {
                filePath = Path.GetFullPath(filePath);
                BNL.Log($"Config path: {filePath}");

                if (!File.Exists(filePath))
                {
                    BNL.Log("Config file not found. Creating default.");

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                        var doc = new XDocument(
                            new XComment(" BasisNetworkClientConsole load-tester configuration. Spawns ClientCount fake clients that connect to a server for stress testing. "),
                            new XElement("Configuration",
                                new XComment(" Server connection password; must match the server's <Password>. string. "),
                                new XElement("Password", Password),
                                new XComment(" Server host to connect to: hostname or IP (e.g. localhost / 127.0.0.1). string. "),
                                new XElement("Ip", Ip),
                                new XComment(" Server UDP port; must match the server's <SetPort>. int, range 1-65535. "),
                                new XElement("Port", Port),
                                new XComment(" Number of simulated clients to spawn for load testing. int (>= 1); higher counts need more CPU, memory and sockets. "),
                                new XElement("ClientCount", ClientCount),
                                new XComment(" Avatar unlock password/key sent with the avatar; used to decrypt the (encrypted .BEE) bundle at <AvatarUrl>. string. "),
                                new XElement("AvatarPassword", AvatarPassword),
                                new XComment(" Avatar source each fake client advertises. For AvatarLoadMode 0 this is the (encrypted .BEE) bundle download URL. string. "),
                                new XElement("AvatarUrl", AvatarUrl),
                                new XComment(" How receiving clients load the avatar: 0 = AssetBundle (download from AvatarUrl), 1 = Addressables, 2 = In-scene. Allowed: 0, 1 or 2. "),
                                new XElement("AvatarLoadMode", AvatarLoadMode),
                                new XComment(" When true, each fake client advertises a random avatar from the Basis client's saved avatars (ItemKeyStore.json, Mode = Avatar) so load tests cover varied avatar types. When false, every client uses the single AvatarUrl/AvatarPassword/AvatarLoadMode above. bool: true or false. "),
                                new XElement("UseRandomAvatarFromKeyStore", UseRandomAvatarFromKeyStore),
                                new XComment(" Path to the avatar keystore (ItemKeyStore.json) read when UseRandomAvatarFromKeyStore is true. Leave empty to auto-detect the local Basis client's persistentDataPath. string. "),
                                new XElement("AvatarKeyStorePath", AvatarKeyStorePath),
                                new XComment(" Report a spread of real platforms (WindowsPlayer/Android/etc) instead of Headless. Off by default (a load client really is headless); turn on to measure what a real mixed crowd costs in per-player metadata. bool. "),
                                new XElement("SimulateRealisticPlatforms", SimulateRealisticPlatforms),
                                new XComment(" Send per-client body-fit scales instead of identity, so the avatar record and join fill carry realistic proportions. bool. "),
                                new XElement("SimulateBodyFit", SimulateBodyFit),
                                new XComment(" Radius in metres that simulated clients spawn across. The server reduces avatar quality and send rate by pair distance, so a spread-out crowd is what resting network usage actually looks like; 0 clusters everyone at spawn (worst case). float. "),
                                new XElement("SpawnRadiusMeters", SpawnRadiusMeters),
                                new XComment(" Simulate voice traffic. Each client advertises the peers within VoiceRangeMeters (Basis culls voice client-side) and transmits Opus-sized frames to them. Off = a silent crowd, which understates what a real instance costs. bool. "),
                                new XElement("SimulateVoice", SimulateVoice),
                                new XComment(" Audible radius in metres used to build each client's voice recipient list. float. "),
                                new XElement("VoiceRangeMeters", VoiceRangeMeters),
                                new XComment(" Percentage of clients that ever transmit; the rest listen or are muted. int, 0-100. "),
                                new XElement("VoiceParticipantPercent", VoiceParticipantPercent),
                                new XComment(" Talk-burst length range in ms. Participants alternate bursts and silence instead of holding the mic open, and a client with nobody inside VoiceRangeMeters transmits nothing at all. int. "),
                                new XElement("VoiceTalkBurstMinMs", VoiceTalkBurstMinMs),
                                new XElement("VoiceTalkBurstMaxMs", VoiceTalkBurstMaxMs),
                                new XComment(" Silence length range in ms between bursts. With the defaults roughly 6% of the crowd is audible at any moment. int. "),
                                new XElement("VoiceSilenceMinMs", VoiceSilenceMinMs),
                                new XElement("VoiceSilenceMaxMs", VoiceSilenceMaxMs),
                                new XComment(" Crowd chorus events: everyone singing happy birthday or cheering at once. Independent per-person bursts never produce that spike, and the spike is the peak the server has to survive. A chorus overrides the personal burst clock while it runs. bool. "),
                                new XElement("VoiceChorusEnabled", VoiceChorusEnabled),
                                new XComment(" Percentage of voice participants that join a chorus. int, 0-100. "),
                                new XElement("VoiceChorusPercent", VoiceChorusPercent),
                                new XComment(" How long a chorus lasts, in ms. Happy birthday is about 20 s. int. "),
                                new XElement("VoiceChorusDurationMinMs", VoiceChorusDurationMinMs),
                                new XElement("VoiceChorusDurationMaxMs", VoiceChorusDurationMaxMs),
                                new XComment(" Gap between chorus events, in ms. int. "),
                                new XElement("VoiceChorusIntervalMinMs", VoiceChorusIntervalMinMs),
                                new XElement("VoiceChorusIntervalMaxMs", VoiceChorusIntervalMaxMs),
                                new XComment(" Milliseconds between voice frames per talking client. 20 ms matches a standard Opus frame (50 packets/sec). int. "),
                                new XElement("VoiceFrameMs", VoiceFrameMs),
                                new XComment(" How often a client re-derives who can hear it and republishes the recipient list, in ms. Real players join and move, so this is the reaction time. int. "),
                                new XElement("VoiceRecipientRefreshMs", VoiceRecipientRefreshMs),
                                new XComment(" Drop a player from the audible set after this long with no nearby avatar traffic from them, in ms. int. "),
                                new XElement("VoiceAudibleTimeoutMs", VoiceAudibleTimeoutMs),
                                new XComment(" Opus bitrate in bits/sec, matching the real client's encoder. Frame sizes come from the encoder itself. int. "),
                                new XElement("VoiceBitrate", VoiceBitrate),
                                new XComment(" Fallback payload bytes per frame, used only when native Opus is unavailable on this platform. int. "),
                                new XElement("VoiceBytesPerFrame", VoiceBytesPerFrame),
                                new XComment(" Transmit a real system recording device instead of the synthetic sweep, so a listener can judge actual voice quality under load. One capture is shared by every voice participant; the burst clock and VoiceRangeMeters culling are unchanged, so you hear real audio from whichever clients are near you. bool. "),
                                new XElement("VoiceUseSystemMicrophone", VoiceUseSystemMicrophone),
                                new XComment(" Recording device to capture, matched case-insensitively as a substring (waveIn truncates names to 31 chars, so a prefix like \"CABLE Output\" is safest). Every device is listed at startup. string. "),
                                new XElement("VoiceMicrophoneDevice", VoiceMicrophoneDevice)
                            )
                        );

                        // atomic write
                        var temp = filePath + ".tmp";
                        doc.Save(temp);
                        File.Move(temp, filePath);

                        BNL.Log("Default config created successfully.");
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError("Failed to create config file." + ex.Message);
                    }

                    return;
                }

                XDocument docLoaded;
                try
                {
                    docLoaded = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
                }
                catch (Exception ex)
                {
                    BNL.LogError("Failed to load config XML (corrupt or in use)." + ex.Message);
                    return;
                }

                var root = docLoaded.Root;
                if (root == null)
                {
                    BNL.Log("Config XML has no root element.");
                    return;
                }

                BNL.Log($"Root element: {root.Name} | Namespace: '{root.Name.NamespaceName}'");

                try
                {
                    Password = ReadString(root, "Password", Password);
                    Ip = ReadString(root, "Ip", Ip);
                    Port = ReadInt(root, "Port", Port);
                    ClientCount = ReadInt(root, "ClientCount", ClientCount);

                    AvatarPassword = ReadString(root, "AvatarPassword", AvatarPassword);
                    AvatarUrl = ReadString(root, "AvatarUrl", AvatarUrl);
                    AvatarLoadMode = ReadInt(root, "AvatarLoadMode", AvatarLoadMode);
                    UseRandomAvatarFromKeyStore = ReadBool(root, "UseRandomAvatarFromKeyStore", UseRandomAvatarFromKeyStore);
                    AvatarKeyStorePath = ReadString(root, "AvatarKeyStorePath", AvatarKeyStorePath);
                    SimulateRealisticPlatforms = ReadBool(root, "SimulateRealisticPlatforms", SimulateRealisticPlatforms);
                    SimulateBodyFit = ReadBool(root, "SimulateBodyFit", SimulateBodyFit);
                    SpawnRadiusMeters = ReadFloat(root, "SpawnRadiusMeters", SpawnRadiusMeters);
                    SimulateVoice = ReadBool(root, "SimulateVoice", SimulateVoice);
                    VoiceRangeMeters = ReadFloat(root, "VoiceRangeMeters", VoiceRangeMeters);
                    VoiceParticipantPercent = ReadInt(root, "VoiceParticipantPercent", VoiceParticipantPercent);
                    VoiceTalkBurstMinMs = ReadInt(root, "VoiceTalkBurstMinMs", VoiceTalkBurstMinMs);
                    VoiceTalkBurstMaxMs = ReadInt(root, "VoiceTalkBurstMaxMs", VoiceTalkBurstMaxMs);
                    VoiceSilenceMinMs = ReadInt(root, "VoiceSilenceMinMs", VoiceSilenceMinMs);
                    VoiceSilenceMaxMs = ReadInt(root, "VoiceSilenceMaxMs", VoiceSilenceMaxMs);
                    VoiceChorusEnabled = ReadBool(root, "VoiceChorusEnabled", VoiceChorusEnabled);
                    VoiceChorusPercent = ReadInt(root, "VoiceChorusPercent", VoiceChorusPercent);
                    VoiceChorusDurationMinMs = ReadInt(root, "VoiceChorusDurationMinMs", VoiceChorusDurationMinMs);
                    VoiceChorusDurationMaxMs = ReadInt(root, "VoiceChorusDurationMaxMs", VoiceChorusDurationMaxMs);
                    VoiceChorusIntervalMinMs = ReadInt(root, "VoiceChorusIntervalMinMs", VoiceChorusIntervalMinMs);
                    VoiceChorusIntervalMaxMs = ReadInt(root, "VoiceChorusIntervalMaxMs", VoiceChorusIntervalMaxMs);
                    VoiceFrameMs = ReadInt(root, "VoiceFrameMs", VoiceFrameMs);
                    VoiceBitrate = ReadInt(root, "VoiceBitrate", VoiceBitrate);
                    VoiceRecipientRefreshMs = ReadInt(root, "VoiceRecipientRefreshMs", VoiceRecipientRefreshMs);
                    VoiceAudibleTimeoutMs = ReadInt(root, "VoiceAudibleTimeoutMs", VoiceAudibleTimeoutMs);
                    VoiceBytesPerFrame = ReadInt(root, "VoiceBytesPerFrame", VoiceBytesPerFrame);
                    VoiceUseSystemMicrophone = ReadBool(root, "VoiceUseSystemMicrophone", VoiceUseSystemMicrophone);
                    VoiceMicrophoneDevice = ReadString(root, "VoiceMicrophoneDevice", VoiceMicrophoneDevice);
                }
                catch (Exception ex)
                {
                    BNL.LogError("Unexpected error while parsing config." + ex.Message);
                }
            }
        }
    }
}
