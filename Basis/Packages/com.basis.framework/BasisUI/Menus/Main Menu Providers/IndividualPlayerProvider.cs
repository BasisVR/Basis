using System;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using BasisPermissions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Basis.Scripts.Device_Management;

namespace Basis.BasisUI
{
    public class IndividualPlayerProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new IndividualPlayerProvider());
        }

        public static string StaticTitle = "IndividualPlayer";
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Calibrate;
        public override int Order => 50;
        public override bool Hidden => true;

        // ---- Context (who are we editing?) ----
        public static BasisRemotePlayer remotePlayer;

        // ======== Static Highlight Beacon (persists across UI open/close) ========

        private const float BeaconHeight = 20f;

        private static GameObject s_beaconGO;
        private static LineRenderer s_beaconLine;
        private static BasisNetworkPlayer s_beaconTarget;
        private static float s_beaconElapsed;

        /// <summary>
        /// Called each frame from BasisEventDriver.LateUpdate.
        /// Updates the highlight beacon position using MouthTransform when available.
        /// </summary>
        public static void SimulateBeacon(float deltaTime)
        {
            if (s_beaconGO == null || s_beaconTarget == null) return;

            s_beaconElapsed += deltaTime;

            if (s_beaconTarget.Player == null)
            {
                ClearHighlight();
                return;
            }

            Vector3 basePos;
            if (s_beaconTarget.Player is BasisRemotePlayer remote && remote.MouthTransform != null)
            {
                basePos = remote.MouthTransform.position;
            }
            else
            {
                basePos = s_beaconTarget.Player.transform.position + Vector3.up * 1.5f;
            }

            Vector3 topPos = basePos + Vector3.up * BeaconHeight;
            s_beaconLine.SetPosition(0, basePos);
            s_beaconLine.SetPosition(1, topPos);

            float pulse = 0.6f + 0.4f * Mathf.Sin(s_beaconElapsed * 3f);
            s_beaconLine.startColor = new Color(0.2f, 0.8f, 1f, pulse);
            s_beaconLine.endColor = new Color(0.2f, 0.8f, 1f, 0f);
        }

        /// <summary>
        /// Sets or toggles the highlight beacon on the given player.
        /// </summary>
        public static void SetHighlight(BasisNetworkPlayer target)
        {
            if (s_beaconTarget != null && s_beaconTarget.playerId == target.playerId)
            {
                ClearHighlight();
                return;
            }

            ClearHighlight();

            s_beaconTarget = target;
            s_beaconElapsed = 0f;

            s_beaconGO = new GameObject("PlayerHighlightBeacon");
            if (BasisDeviceManagement.Instance != null)
            {
                s_beaconGO.transform.SetParent(BasisDeviceManagement.Instance.transform, true);
            }

            s_beaconLine = s_beaconGO.AddComponent<LineRenderer>();
            s_beaconLine.positionCount = 2;
            s_beaconLine.startWidth = 0.15f;
            s_beaconLine.endWidth = 0.02f;
            s_beaconLine.useWorldSpace = true;
            s_beaconLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_beaconLine.receiveShadows = false;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                s_beaconLine.material = new Material(shader);
            }

            s_beaconLine.startColor = new Color(0.2f, 0.8f, 1f, 0.9f);
            s_beaconLine.endColor = new Color(0.2f, 0.8f, 1f, 0f);
        }

        /// <summary>
        /// Destroys the active highlight beacon.
        /// </summary>
        public static void ClearHighlight()
        {
            s_beaconTarget = null;
            s_beaconElapsed = 0f;
            if (s_beaconGO != null)
            {
                UnityEngine.Object.Destroy(s_beaconGO);
                s_beaconGO = null;
                s_beaconLine = null;
            }
        }

        public static bool HasHighlight => s_beaconGO != null;

        // ========= Addressables Sprite (cached) =========
        private const string MeterSpriteAddress = "Packages/com.basis.sdk/Sprites/HalfCircle 512 Right.png";
        private static Sprite s_meterSprite;
        private static Task<Sprite> s_meterSpriteTask;

        private static Task<Sprite> GetMeterSpriteAsync()
        {
            if (s_meterSprite != null)
                return Task.FromResult(s_meterSprite);

            // Deduplicate concurrent loads
            if (s_meterSpriteTask != null)
                return s_meterSpriteTask;

            s_meterSpriteTask = LoadMeterSpriteInternalAsync();
            return s_meterSpriteTask;
        }

        private static async Task<Sprite> LoadMeterSpriteInternalAsync()
        {
            AsyncOperationHandle<Sprite> handle = Addressables.LoadAssetAsync<Sprite>(MeterSpriteAddress);
            await handle.Task;

            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                s_meterSprite = handle.Result;
                return s_meterSprite;
            }

            Debug.LogError($"[IndividualPlayerProvider] Failed to load meter sprite via Addressables: '{MeterSpriteAddress}'");
            return null;
        }

        // ========= Meter UI builder =========
        private struct MeterRefs
        {
            public GameObject Root;
            public Image Fill;
            public Image PeakTick;
            public Image BandRecommended;
            public Image BandOverdrive;
            public Image DefaultTick;
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchRect(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.anchoredPosition = Vector2.zero;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Image CreateImage(string name, Transform parent, Sprite sprite)
        {
            var go = CreateUIObject(name, parent);
            var img = go.AddComponent<Image>();

            img.sprite = sprite;            // IMPORTANT: Filled images need a sprite
            img.raycastTarget = false;
            img.preserveAspect = false;

            return img;
        }

        /// <summary>
        /// Creates the meter GameObject hierarchy and returns references.
        /// Uses Addressables sprite at Packages/com.basis.sdk/Sprites/HalfCircle 512 Right.png
        /// </summary>
        private static async Task<MeterRefs> CreateVolumeMeterUIAsync(Transform parent)
        {
            var sprite = await GetMeterSpriteAsync();

            // Root
            var root = CreateUIObject("VolumeMeter", parent);
            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0f, 0f);
            rootRt.anchorMax = new Vector2(1f, 0f);
            rootRt.pivot = new Vector2(0.5f, 0f);
            rootRt.sizeDelta = new Vector2(0f, 40f);

            // Background (behind everything)
            var bg = CreateImage("BG", root.transform, sprite);
            bg.color = new Color(0f, 0f, 0f, 0.35f);
            StretchRect(bg.rectTransform);

            // Recommended band (behind fill)
            var bandRecommended = CreateImage("BandRecommended", root.transform, sprite);
            bandRecommended.color = new Color(0f, 0.8f, 0.4f, 0.4f);
            StretchRect(bandRecommended.rectTransform);

            // Overdrive band (behind fill)
            var bandOverdrive = CreateImage("BandOverdrive", root.transform, sprite);
            bandOverdrive.type = Image.Type.Tiled;
            bandOverdrive.pixelsPerUnitMultiplier = 2;
            bandOverdrive.color = new Color(0.9f, 0f, 0f, 0.4f);
            StretchRect(bandOverdrive.rectTransform);

            // Fill bar (must have sprite for fillAmount to work)
            var fill = CreateImage("Fill", root.transform, sprite);
            StretchRect(fill.rectTransform);

            // NOTE:
            // If that sprite is truly a half-circle gauge, you probably want Radial fill,
            // not Horizontal. Leaving Horizontal here to match your existing sampler UI.
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;

            // Peak tick (thin vertical line, on top)
            var peakTick = CreateImage("PeakTick", root.transform, sprite);
            var peakRt = peakTick.rectTransform;
            peakRt.anchorMin = new Vector2(0f, 0f);
            peakRt.anchorMax = new Vector2(0f, 1f);
            peakRt.pivot = new Vector2(0.5f, 0.5f);
            peakRt.sizeDelta = new Vector2(2f, 0f);
            peakRt.anchoredPosition = Vector2.zero;
            peakTick.color = Color.white;

            // Default tick (thin vertical line, on top)
            var defaultTick = CreateImage("DefaultTick", root.transform, sprite);
            var defRt = defaultTick.rectTransform;
            defRt.anchorMin = new Vector2(0f, 0f);
            defRt.anchorMax = new Vector2(0f, 1f);
            defRt.pivot = new Vector2(0.5f, 0.5f);
            defRt.sizeDelta = new Vector2(2f, 0f);
            defRt.anchoredPosition = Vector2.zero;
            defaultTick.color = new Color(1f, 1f, 1f, 0.6f);

            return new MeterRefs
            {
                Root = root,
                Fill = fill,
                PeakTick = peakTick,
                BandRecommended = bandRecommended,
                BandOverdrive = bandOverdrive,
                DefaultTick = defaultTick
            };
        }

        public async override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
                return;
            }

            var target = remotePlayer;
            if (target == null)
            {
                BasisDebug.LogError("Missing Remote Player Assign Before Calling this Panels Creation!");
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("General Settings");

            TextMeshProUGUI titleLabel = panel.Descriptor.TitleLabel;
            if (titleLabel != null) titleLabel.text = target.DisplayName;

            var root = tab.Descriptor.ContentParent;
            var infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            infoGroup.SetTitle("Player");
            infoGroup.SetDescription("Per-player overrides (volume, avatar visibility, interactions).");

            var Descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group,infoGroup.ContentParent);
            Descriptor.SetTitle("Name");
            Descriptor.SetDescription(remotePlayer.DisplayName);

            var PlatformDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, infoGroup.ContentParent);
            PlatformDescriptor.SetTitle("Platform");
            PlatformDescriptor.SetDescription(remotePlayer.PlayerPlatform);

            var uuidField = PanelTextField.CreateNewEntry(infoGroup.ContentParent);
            uuidField.Descriptor.SetTitle("UUID");
            uuidField.SetValueWithoutNotify(remotePlayer.UUID);
            uuidField._inputField.readOnly = true;

            // ---- Highlight beacon controls ----
            var locateGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            locateGroup.SetTitle("Locate");
            locateGroup.SetDescription("Highlight this player with a visible beacon in the world.");

            PanelButton highlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            highlightBtn.Descriptor.SetTitle(HasHighlight && s_beaconTarget?.Player == remotePlayer
                ? "Remove Highlight" : "Highlight Player");
            highlightBtn.Descriptor.SetDescription("Toggle a vertical beacon above this player.");

            PanelButton clearHighlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            clearHighlightBtn.Descriptor.SetTitle("Clear Highlight");
            clearHighlightBtn.Descriptor.SetDescription("Remove any active beacon.");

            highlightBtn.OnClicked += () =>
            {
                if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                    remotePlayer, out BasisNetworkPlayer netPlayer))
                {
                    SetHighlight(netPlayer);
                    highlightBtn.Descriptor.SetTitle(HasHighlight ? "Remove Highlight" : "Highlight Player");
                }
            };

            clearHighlightBtn.OnClicked += () =>
            {
                ClearHighlight();
                highlightBtn.Descriptor.SetTitle("Highlight Player");
            };

            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
            var audioGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            audioGroup.SetTitle("Audio");
            audioGroup.SetDescription("Override this player’s voice volume just for you.");

            const float step = 0.05f;

            string indivdualusersettingsvolume = "indivdualusersettingsvolume";
            BasisSettingsBinding<float> Binding = new BasisSettingsBinding<float>(indivdualusersettingsvolume);

            PanelSlider volumeSlider = PanelSlider.CreateEntryAndBind(
                audioGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Player Volume Override", 0f, 1.5f, false, 2, ValueDisplayMode.percentageFromZero),
                Binding);

            volumeSlider.SetValueWithoutNotify(settings.VolumeLevel);

            var volumeNote = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioGroup.ContentParent);
            volumeNote.SetTitle("Note");

            void UpdateVolumeNote(float v)
            {
                bool over = v > 1.0f;
                volumeNote.SetDescription(over ? "Over 100% (may clip / distort)" : "Normal range");
            }
            UpdateVolumeNote(settings.VolumeLevel);

            // ---- Create meter UI (Addressables sprite) ----
            MeterRefs meter = await CreateVolumeMeterUIAsync(audioGroup.ContentParent);

            // ---- Add sampler and wire UI refs ----
            var sampler = meter.Root.AddComponent<BasisUIVolumeSampler>();

            sampler.RemotePlayer = remotePlayer;
            sampler.fill = meter.Fill;
            sampler.peakTick = meter.PeakTick;

            sampler.bandRecommended = meter.BandRecommended;
            sampler.bandOverdrive = meter.BandOverdrive;
            sampler.defaultTick = meter.DefaultTick;

            sampler.slider = volumeSlider.SliderComponent;

            // Level mapping / feel
            sampler.minDb = -60f;
            sampler.maxDb = 0f;
            sampler.gainDb = 0f;

            sampler.attack = 0.06f;
            sampler.release = 0.20f;
            sampler.peakHoldTime = 0.6f;
            sampler.peakFallPerSecond = 1.5f;

            sampler.inactiveColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            sampler.overdriveColor = Color.red;

            // Semantic bands (slider space)
            sampler.recommendedMin = 1.0f;
            sampler.defaultValue = 1.0f;

            // Gradient (green -> yellow -> red)
            sampler.colorByLevel = new Gradient()
            {
                colorKeys = new[]
                {
                    new GradientColorKey(new Color(0.0f, 1.0f, 0.3f), 0f),
                    new GradientColorKey(new Color(1.0f, 0.9f, 0.0f), 0.7f),
                    new GradientColorKey(new Color(1.0f, 0.1f, 0.1f), 1f),
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                }
            };

            sampler.Initalize(remotePlayer);

            // Wire slider -> save -> apply to receiver
            volumeSlider.OnValueChanged += async raw =>
            {
                float snapped = Mathf.Round(raw / step) * step;
                snapped = Mathf.Clamp(snapped, 0f, 1.5f);

                volumeSlider.SetValueWithoutNotify(snapped);
                UpdateVolumeNote(snapped);

                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.VolumeLevel = snapped;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null)
                {
                    remotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(snapped);
                }
            };
            var avatarGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            avatarGroup.SetTitle("Avatar");
            avatarGroup.SetDescription("Visibility and interaction toggles.");

            if (!string.IsNullOrEmpty(remotePlayer.AvatarLoadErrorMessage))
            {
                var avatarErrorField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarGroup.ContentParent);
                avatarErrorField.SetTitle("Avatar Load Error");
                avatarErrorField.SetDescription(remotePlayer.AvatarLoadErrorMessage);
            }

            PanelButton toggleAvatarBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            toggleAvatarBtn.Descriptor.SetTitle(settings.AvatarVisible ? "Hide Avatar" : "Show Avatar");
            toggleAvatarBtn.Descriptor.SetDescription("Toggles rendering of this player’s avatar on your client.");

            PanelButton toggleInteractionsBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            toggleInteractionsBtn.Descriptor.SetTitle(settings.AvatarInteraction ? "Disable Interactions" : "Enable Interactions");
            toggleInteractionsBtn.Descriptor.SetDescription("Toggles whether this avatar can interact with you.");

            toggleAvatarBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AvatarVisible = !s.AvatarVisible;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleAvatarBtn.Descriptor.SetTitle(s.AvatarVisible ? "Hide Avatar" : "Show Avatar");

                if (remotePlayer != null) remotePlayer.ReloadAvatar();
            };

            toggleInteractionsBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AvatarInteraction = !s.AvatarInteraction;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleInteractionsBtn.Descriptor.SetTitle(s.AvatarInteraction ? "Disable Interactions" : "Enable Interactions");

                if (remotePlayer != null) remotePlayer.ReloadAvatar();
            };

            var chatGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            chatGroup.SetTitle("Chat");
            chatGroup.SetDescription("Control chat message visibility for this player.");

            PanelButton toggleChatBtn = PanelButton.CreateNew(chatGroup.ContentParent);
            toggleChatBtn.Descriptor.SetTitle(settings.ChatVisible ? "Hide Chat" : "Show Chat");
            toggleChatBtn.Descriptor.SetDescription("Toggles whether chat messages from this player appear above their nameplate.");

            toggleChatBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.ChatVisible = !s.ChatVisible;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleChatBtn.Descriptor.SetTitle(s.ChatVisible ? "Hide Chat" : "Show Chat");

                // If chat was just hidden, clear any currently displayed message
                if (!s.ChatVisible && remotePlayer != null && remotePlayer.RemoteNamePlate != null)
                {
                    remotePlayer.RemoteNamePlate.SetChatText(string.Empty);
                }
            };

            // ---- Network metadata group ----
            var networkGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            networkGroup.SetTitle("Network");
            networkGroup.SetDescription("Live network state for this player.");

            var netIdField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            netIdField.SetTitle("Player ID");
            if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                remotePlayer, out BasisNetworkPlayer netP))
            {
                netIdField.SetDescription(netP.playerId.ToString());
            }
            else
            {
                netIdField.SetDescription("Unknown");
            }

            var distanceField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            distanceField.SetTitle("Distance");
            distanceField.SetDescription("...");

            var lodField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            lodField.SetTitle("Mesh LOD Level");
            lodField.SetDescription("...");

            var rangesField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            rangesField.SetTitle("Ranges");
            rangesField.SetDescription("...");

            var bufferField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            bufferField.SetTitle("Buffer State");
            bufferField.SetDescription("...");

            // ---- Admin moderation section (only visible to admins) ----
            if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView))
            {
                string targetUUID = remotePlayer.UUID;

                var adminGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
                adminGroup.SetTitle("Admin");
                adminGroup.SetDescription("Moderation actions for this player.");

                PanelButton kickBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                kickBtn.Descriptor.SetTitle("Kick");
                kickBtn.Descriptor.SetDescription("Disconnect this player from the server.");
                kickBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        "Kick player?",
                        $"Kick {remotePlayer.DisplayName}?",
                        "Kick", "Cancel",
                        confirmed => { if (confirmed) BasisNetworkModeration.SendKick(targetUUID, ""); });
                };

                PanelButton banBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                banBtn.Descriptor.SetTitle("Ban");
                banBtn.Descriptor.SetDescription("Ban this player by UUID.");
                banBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        "Ban player?",
                        $"Ban {remotePlayer.DisplayName}? This may be irreversible.",
                        "Ban", "Cancel",
                        confirmed => { if (confirmed) BasisNetworkModeration.SendBan(targetUUID, ""); });
                };

                PanelButton ipBanBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                ipBanBtn.Descriptor.SetTitle("IP Ban");
                ipBanBtn.Descriptor.SetDescription("IP-ban this player. Affects all accounts on their connection.");
                ipBanBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        "IP ban player?",
                        $"IP-ban {remotePlayer.DisplayName}? This can affect multiple accounts.",
                        "IP Ban", "Cancel",
                        confirmed => { if (confirmed) BasisNetworkModeration.SendIPBan(targetUUID, ""); });
                };

                PanelButton teleportToBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                teleportToBtn.Descriptor.SetTitle("Teleport To");
                teleportToBtn.Descriptor.SetDescription("Teleport yourself to this player's location.");
                teleportToBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                        BasisNetworkModeration.TryTeleportToPlayer(np.playerId);
                };

                PanelButton teleportHereBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                teleportHereBtn.Descriptor.SetTitle("Teleport Here");
                teleportHereBtn.Descriptor.SetDescription("Teleport this player to your location.");
                teleportHereBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                        BasisNetworkModeration.TeleportHere(np.playerId);
                };

                PanelButton shoutBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                bool isShouting = false;
                if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer shoutNp))
                    isShouting = BasisShoutAudioDriver.IsInShoutMode(shoutNp.playerId);
                shoutBtn.Descriptor.SetTitle(isShouting ? "Disable Shout Mode" : "Enable Shout Mode");
                shoutBtn.Descriptor.SetDescription("Toggle non-spatialized broadcast voice for this player.");
                shoutBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                    {
                        bool active = BasisShoutAudioDriver.IsInShoutMode(np.playerId);
                        if (active)
                            BasisNetworkModeration.DisableShoutMode(np.playerId);
                        else
                            BasisNetworkModeration.EnableShoutMode(np.playerId);
                        shoutBtn.Descriptor.SetTitle(active ? "Enable Shout Mode" : "Disable Shout Mode");
                    }
                };

                PanelTextField msgField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
                msgField.Descriptor.SetTitle("Message");
                msgField.Descriptor.SetDescription("Send a message directly to this player.");

                PanelButton sendMsgBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                sendMsgBtn.Descriptor.SetTitle("Send Message");
                sendMsgBtn.Descriptor.SetDescription("Delivers the message above to this player.");
                sendMsgBtn.OnClicked += () =>
                {
                    string msg = msgField.Value;
                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        BasisDebug.LogError("Message is empty.");
                        return;
                    }
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                    {
                        BasisNetworkModeration.SendMessage(np.playerId, msg);
                        msgField.SetValueWithoutNotify(string.Empty);
                    }
                };

                // ---- Per-user permissions ----
                var permGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, adminGroup.ContentParent);
                permGroup.SetTitle("Permissions");
                permGroup.SetDescription("Add or remove individual permission nodes for this player.");

                var knownNodes = new System.Collections.Generic.List<string>
                {
                    PermNodes.All,
                    PermNodes.protection,
                    PermNodes.ModerationKick,
                    PermNodes.ModerationBan,
                    PermNodes.ModerationIpBan,
                    PermNodes.ModerationUnban,
                    PermNodes.ModerationUnbanIp,
                    PermNodes.ModerationMessage,
                    PermNodes.ModerationMessageAll,
                    PermNodes.ModerationTeleport,
                    PermNodes.ModerationShout,
                    PermNodes.PlayerModeration,
                    PermNodes.PermissionsView,
                    PermNodes.PermissionsEdit,
                    PermNodes.ResourceLoadWorld,
                    PermNodes.ResourceUnloadWorld,
                    PermNodes.ResourceLoadProp,
                    PermNodes.ResourceUnloadProp,
                    PermNodes.ResourceLoadAvatar,
                    PermNodes.ResourceUnloadAvatar,
                    PermNodes.OwnershipTransfer,
                    PermNodes.OwnershipRemove,
                    PermNodes.OwnershipGet,
                    PermNodes.ContentShareCreate,
                    PermNodes.ContentShareDelete,
                    PermNodes.ConfigurationEditor,
                    PermNodes.ServerStats,
                };

                PanelDropdown nodeDropdown = PanelDropdown.CreateNewEntry(permGroup.ContentParent);
                nodeDropdown.Descriptor.SetTitle("Permission Node");
                nodeDropdown.AssignEntries(knownNodes);

                PanelTextField customNodeField = PanelTextField.CreateNewEntry(permGroup.ContentParent);
                customNodeField.Descriptor.SetTitle("Custom Node");
                customNodeField.Descriptor.SetDescription("Or type a custom node (overrides dropdown).");

                PanelButton addNodeBtn = PanelButton.CreateNew(permGroup.ContentParent);
                addNodeBtn.Descriptor.SetTitle("Grant Permission");
                addNodeBtn.Descriptor.SetDescription("Add the selected permission node to this player.");
                addNodeBtn.OnClicked += () =>
                {
                    string node = !string.IsNullOrWhiteSpace(customNodeField.Value)
                        ? customNodeField.Value
                        : nodeDropdown.SelectedString;
                    if (string.IsNullOrWhiteSpace(node)) return;
                    BasisNetworkModeration.SetUserNode(targetUUID, node, true);
                };

                PanelButton removeNodeBtn = PanelButton.CreateNew(permGroup.ContentParent);
                removeNodeBtn.Descriptor.SetTitle("Revoke Permission");
                removeNodeBtn.Descriptor.SetDescription("Remove the selected permission node from this player.");
                removeNodeBtn.OnClicked += () =>
                {
                    string node = !string.IsNullOrWhiteSpace(customNodeField.Value)
                        ? customNodeField.Value
                        : nodeDropdown.SelectedString;
                    if (string.IsNullOrWhiteSpace(node)) return;
                    BasisNetworkModeration.SetUserNode(targetUUID, node, false);
                };

                // ---- Group assignment ----
                var groupSection = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, adminGroup.ContentParent);
                groupSection.SetTitle("Groups");
                groupSection.SetDescription("Add or remove this player from permission groups.");

                PanelTextField groupField = PanelTextField.CreateNewEntry(groupSection.ContentParent);
                groupField.Descriptor.SetTitle("Group Name");
                groupField.Descriptor.SetDescription("Name of the permission group.");

                PanelButton addGroupBtn = PanelButton.CreateNew(groupSection.ContentParent);
                addGroupBtn.Descriptor.SetTitle("Add to Group");
                addGroupBtn.OnClicked += () =>
                {
                    string group = groupField.Value;
                    if (string.IsNullOrWhiteSpace(group)) return;
                    BasisNetworkModeration.SetUserGroup(targetUUID, group, true);
                };

                PanelButton removeGroupBtn = PanelButton.CreateNew(groupSection.ContentParent);
                removeGroupBtn.Descriptor.SetTitle("Remove from Group");
                removeGroupBtn.OnClicked += () =>
                {
                    string group = groupField.Value;
                    if (string.IsNullOrWhiteSpace(group)) return;
                    BasisNetworkModeration.SetUserGroup(targetUUID, group, false);
                };
            }

            var debugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            debugGroup.SetTitle("Debug");
            debugGroup.SetDescription("Live diagnostics for voice/range checks (optional).");

            var debugField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, debugGroup.ContentParent);
            debugField.SetTitle("Transmission");
            debugField.SetDescription("Waiting for data...");

            var updater = panel.gameObject.AddComponent<IndividualPlayerPanelUpdater>();
            updater.RemotePlayer = remotePlayer;
            updater.DebugField = debugField;
            updater.DistanceField = distanceField;
            updater.LodField = lodField;
            updater.RangesField = rangesField;
            updater.BufferField = bufferField;

            panel.Descriptor.ForceRebuild();
            panel.Descriptor.ForceRebuild();
        }
    }
}
