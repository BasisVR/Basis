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

        public const string StaticTitleKey = "menu.provider.individualPlayer";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);
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
            s_beaconLine.startColor = GetBeaconColor(s_beaconTarget, pulse);
            s_beaconLine.endColor = GetBeaconColor(s_beaconTarget, 0f);
        }

        private static Color GetBeaconColor(BasisNetworkPlayer target, float alpha)
        {
            if (target?.Player is BasisRemotePlayer remote && remote.IsEffectivelyBlocked)
                return new Color(1f, 0.2f, 0.2f, alpha);
            return new Color(0.2f, 0.8f, 1f, alpha);
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

            s_beaconLine.startColor = GetBeaconColor(target, 0.9f);
            s_beaconLine.endColor = GetBeaconColor(target, 0f);
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
            descriptor.SetTitle(BasisLocalization.Get("settings.general.title"));

            TextMeshProUGUI titleLabel = panel.Descriptor.TitleLabel;
            if (titleLabel != null) titleLabel.text = target.DisplayName;

            var root = tab.Descriptor.ContentParent;
            var infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            infoGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.player"));
            infoGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.player.description"));

            var Descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group,infoGroup.ContentParent);
            Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.name"));
            Descriptor.SetDescription(remotePlayer.DisplayName);

            var PlatformDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, infoGroup.ContentParent);
            PlatformDescriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.platform"));
            PlatformDescriptor.SetDescription(remotePlayer.PlayerPlatform);

            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
            var audioGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            audioGroup.SetTitle(BasisLocalization.Get("settings.tab.audio"));
            audioGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.audio.description"));

            string indivdualusersettingsvolume = "indivdualusersettingsvolume";
            BasisSettingsBinding<float> Binding = new BasisSettingsBinding<float>(indivdualusersettingsvolume);

            PanelSlider volumeSlider = PanelSlider.CreateEntryAndBind(
                audioGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("menu.individualPlayer.volumeOverride"), 0f, 1.5f, false, 2, ValueDisplayMode.percentageFromZero),
                Binding);

            volumeSlider.SetValueWithoutNotify(settings.VolumeLevel);

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
                float value = Mathf.Clamp(raw, 0f, 1.5f);

                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.VolumeLevel = value;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null)
                {
                    remotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(
                        remotePlayer.IsEffectivelyBlocked ? 0f : value);
                }
            };

            // ---- Highlight beacon controls ----
            var locateGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            locateGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.locate"));
            locateGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.locate.description"));

            PanelButton highlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            highlightBtn.Descriptor.SetTitle(BasisLocalization.Get(HasHighlight && s_beaconTarget?.Player == remotePlayer
                ? "menu.individualPlayer.removeHighlight" : "menu.individualPlayer.highlight"));
            highlightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.highlight.description"));

            PanelButton clearHighlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            clearHighlightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.clearHighlights"));
            clearHighlightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.clearHighlights.description"));

            highlightBtn.OnClicked += () =>
            {
                if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                    remotePlayer, out BasisNetworkPlayer netPlayer))
                {
                    SetHighlight(netPlayer);
                    highlightBtn.Descriptor.SetTitle(BasisLocalization.Get(HasHighlight ? "menu.individualPlayer.removeHighlight" : "menu.individualPlayer.highlight"));
                }
            };

            clearHighlightBtn.OnClicked += () =>
            {
                ClearHighlight();
                highlightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.highlight"));
            };

            // ---- Pin controls ----
            var pinGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            pinGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.pin"));
            pinGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.pin.description"));

            string pinUuid = remotePlayer.UUID;
            PanelButton pinBtn = PanelButton.CreateNew(pinGroup.ContentParent);
            pinBtn.Descriptor.SetTitle(BasisLocalization.Get(PinnedPlayers.IsPinned(pinUuid) ? "menu.individualPlayer.unpin" : "menu.individualPlayer.pinButton"));
            pinBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.pin.button.description"));
            pinBtn.OnClicked += () =>
            {
                bool nowPinned = PinnedPlayers.Toggle(pinUuid);
                pinBtn.Descriptor.SetTitle(BasisLocalization.Get(nowPinned ? "menu.individualPlayer.unpin" : "menu.individualPlayer.pinButton"));
            };

            var avatarGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            avatarGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatar"));
            avatarGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.avatar.description"));

            if (!string.IsNullOrEmpty(remotePlayer.AvatarLoadErrorMessage))
            {
                var avatarErrorField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarGroup.ContentParent);
                avatarErrorField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarLoadError"));
                avatarErrorField.SetDescription(remotePlayer.AvatarLoadErrorMessage);
            }

            // Performance filter result — tells the local user why a specific remote
            // avatar was hard-blocked and/or what the trim pass removed from it,
            // and offers a per-player bypass toggle so they can see this one avatar
            // at full fidelity without editing the global caps. The section stays
            // visible when bypass is on (even if nothing's filtered) so the user
            // can find the toggle again to turn it off.
            {
                var perf = remotePlayer.LastPerformanceInfo;
                bool hasAnyInfo = perf.Blocked || perf.AnythingTrimmed;
                bool bypassOn = remotePlayer.BypassPerformanceLimits;
                if (hasAnyInfo || bypassOn)
                {
                    var perfField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarGroup.ContentParent);
                    perfField.SetTitle(BasisLocalization.Get("menu.individualPlayer.perfFilter.title"));

                    string description;
                    if (bypassOn)
                    {
                        description = BasisLocalization.Get("menu.individualPlayer.perfFilter.bypassedDescription");
                    }
                    else if (perf.Blocked)
                    {
                        // Reason string is built by BasisAvatarPerformanceLimits in English —
                        // the prefix is the only localizable part. Full translation would
                        // require threading the metric + actual/limit through the table.
                        description = BasisLocalization.Get("menu.individualPlayer.perfFilter.blockedPrefix") + (perf.BlockReason ?? string.Empty);
                    }
                    else
                    {
                        var parts = new System.Collections.Generic.List<string>(9);
                        if (perf.AnimatorsTrimmed > 0) parts.Add($"-{perf.AnimatorsTrimmed} animators");
                        if (perf.LightsTrimmed > 0) parts.Add($"-{perf.LightsTrimmed} lights");
                        if (perf.ParticleSystemsTrimmed > 0) parts.Add($"-{perf.ParticleSystemsTrimmed} particle systems");
                        if (perf.TrailRenderersTrimmed > 0) parts.Add($"-{perf.TrailRenderersTrimmed} trail renderers");
                        if (perf.LineRenderersTrimmed > 0) parts.Add($"-{perf.LineRenderersTrimmed} line renderers");
                        if (perf.ClothTrimmed > 0) parts.Add($"-{perf.ClothTrimmed} cloth");
                        if (perf.CollidersTrimmed > 0) parts.Add($"-{perf.CollidersTrimmed} colliders");
                        if (perf.JiggleRigsTrimmed > 0) parts.Add($"-{perf.JiggleRigsTrimmed} jiggle rigs");
                        if (perf.JiggleCollidersTrimmed > 0) parts.Add($"-{perf.JiggleCollidersTrimmed} jiggle colliders");
                        description = BasisLocalization.Get("menu.individualPlayer.perfFilter.trimmedPrefix") + string.Join(", ", parts);
                    }
                    perfField.SetDescription(description);

                    PanelToggle bypassPlayerToggle = PanelToggle.CreateNewEntry(perfField.ContentParent);
                    bypassPlayerToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.perfFilter.bypassToggle"));
                    bypassPlayerToggle.SetValueWithoutNotify(bypassOn);
                    bypassPlayerToggle.OnValueChanged += on =>
                    {
                        if (remotePlayer == null) return;
                        remotePlayer.BypassPerformanceLimits = on;
                        // Full reload: turning on restores destroyed components
                        // (no in-place path exists for restore), turning off re-runs
                        // Evaluate and TrimExcessComponents with the real limits.
                        remotePlayer.ReloadAvatar();
                    };
                }
            }

            PanelButton toggleAvatarBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            toggleAvatarBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.AvatarVisible ? "menu.individualPlayer.hideAvatar" : "menu.individualPlayer.showAvatar"));
            toggleAvatarBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.toggleAvatar.description"));

            PanelButton toggleInteractionsBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            toggleInteractionsBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.AvatarInteraction ? "menu.individualPlayer.disableInteractions" : "menu.individualPlayer.enableInteractions"));
            toggleInteractionsBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.toggleInteractions.description"));

            toggleAvatarBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AvatarVisible = !s.AvatarVisible;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleAvatarBtn.Descriptor.SetTitle(BasisLocalization.Get(s.AvatarVisible ? "menu.individualPlayer.hideAvatar" : "menu.individualPlayer.showAvatar"));

                if (remotePlayer != null)
                {
                    // Manual toggle is the only escape hatch from the global "bail on retries"
                    // state set by BasisAvatarFactory.MarkRemoteLoadFailed. Clear it here so
                    // showing the avatar again actually re-attempts the download.
                    remotePlayer.HasFailedAvatarLoadGlobally = false;
                    remotePlayer.AvatarLoadErrorMessage = null;
                    if (remotePlayer.RemoteNamePlate != null)
                    {
                        remotePlayer.RemoteNamePlate.RefreshFailedStateColor();
                    }
                    remotePlayer.ReloadAvatar();
                }
            };

            toggleInteractionsBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AvatarInteraction = !s.AvatarInteraction;
               await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleInteractionsBtn.Descriptor.SetTitle(BasisLocalization.Get(s.AvatarInteraction ? "menu.individualPlayer.disableInteractions" : "menu.individualPlayer.enableInteractions"));

                if (remotePlayer != null)
                {
                    remotePlayer.ReloadAvatar();
                }
            };

            // ---- Block group ----
            var blockGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            blockGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.block"));
            blockGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.block.description"));

            PanelButton toggleBlockBtn = PanelButton.CreateNew(blockGroup.ContentParent);
            toggleBlockBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
            toggleBlockBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.block.button.description"));

            toggleBlockBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.IsBlocked = !s.IsBlocked;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer == null) return;

                toggleBlockBtn.Descriptor.SetTitle(BasisLocalization.Get(s.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
                remotePlayer.IsBlocked = s.IsBlocked;

                if (remotePlayer.NetworkReceiver != null && remotePlayer.NetworkReceiver.AudioReceiverModule != null)
                {
                    remotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(
                        remotePlayer.IsEffectivelyBlocked ? 0f : s.VolumeLevel);
                }

                remotePlayer.ReloadAvatar();

                if (remotePlayer.RemoteNamePlate != null)
                {
                    if (remotePlayer.IsEffectivelyBlocked)
                    {
                        remotePlayer.RemoteNamePlate.SetChatText(string.Empty);
                    }
                    remotePlayer.RemoteNamePlate.RefreshActiveState();
                }

                // Mirror the block onto the other side of the pair so that when we
                // block them, they also can't see/hear us (session-scoped temp block).
                if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                        remotePlayer, out BasisNetworkPlayer blockTargetNet))
                {
                    BasisNetworkHandleTempBlock.SendTempBlock(blockTargetNet.playerId, s.IsBlocked);
                }
            };

            var chatGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            chatGroup.SetTitle(BasisLocalization.Get("settings.tab.chat"));
            chatGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.chat.description"));

            PanelButton toggleChatBtn = PanelButton.CreateNew(chatGroup.ContentParent);
            toggleChatBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.ChatVisible ? "menu.individualPlayer.hideChat" : "menu.individualPlayer.showChat"));
            toggleChatBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.toggleChat.description"));

            toggleChatBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.ChatVisible = !s.ChatVisible;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleChatBtn.Descriptor.SetTitle(BasisLocalization.Get(s.ChatVisible ? "menu.individualPlayer.hideChat" : "menu.individualPlayer.showChat"));

                // If chat was just hidden, clear any currently displayed message
                if (!s.ChatVisible && remotePlayer != null && remotePlayer.RemoteNamePlate != null)
                {
                    remotePlayer.RemoteNamePlate.SetChatText(string.Empty);
                }
            };

            // ---- Network metadata group ----
            var networkGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            networkGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.network"));
            networkGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.network.description"));

            var netIdField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            netIdField.SetTitle(BasisLocalization.Get("menu.individualPlayer.playerId"));
            if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                remotePlayer, out BasisNetworkPlayer netP))
            {
                netIdField.SetDescription(netP.playerId.ToString());
            }
            else
            {
                netIdField.SetDescription(BasisLocalization.Get("ui.unknown"));
            }

            var distanceField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            distanceField.SetTitle(BasisLocalization.Get("menu.individualPlayer.distance"));
            distanceField.SetDescription("...");

            var lodField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            lodField.SetTitle(BasisLocalization.Get("menu.individualPlayer.meshLod"));
            lodField.SetDescription("...");

            var rangesField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            rangesField.SetTitle(BasisLocalization.Get("menu.individualPlayer.ranges"));
            rangesField.SetDescription("...");

            var bufferField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, networkGroup.ContentParent);
            bufferField.SetTitle(BasisLocalization.Get("menu.individualPlayer.bufferState"));
            bufferField.SetDescription("...");

            // ---- Admin moderation section (only visible to admins) ----
            if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView))
            {
                string targetUUID = remotePlayer.UUID;

                var adminGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
                adminGroup.SetTitle(BasisLocalization.Get("settings.tab.admin"));
                adminGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.admin.description"));

                PanelButton kickBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                kickBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.kick"));
                kickBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.kick.description"));
                kickBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("menu.individualPlayer.kick.dialog.title"),
                        BasisLocalization.Get("menu.individualPlayer.kick.dialog.body", remotePlayer.DisplayName),
                        BasisLocalization.Get("menu.individualPlayer.kick"),
                        BasisLocalization.Get("ui.cancel"),
                        confirmed => { if (confirmed) BasisNetworkModeration.SendKick(targetUUID, ""); });
                };

                PanelButton banBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                banBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.ban"));
                banBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.ban.description"));
                banBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("menu.individualPlayer.ban.dialog.title"),
                        BasisLocalization.Get("menu.individualPlayer.ban.dialog.body", remotePlayer.DisplayName),
                        BasisLocalization.Get("menu.individualPlayer.ban"),
                        BasisLocalization.Get("ui.cancel"),
                        confirmed => { if (confirmed) BasisNetworkModeration.SendBan(targetUUID, ""); });
                };

                PanelButton ipBanBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                ipBanBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.ipBan"));
                ipBanBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.ipBan.description"));
                ipBanBtn.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("menu.individualPlayer.ipBan.dialog.title"),
                        BasisLocalization.Get("menu.individualPlayer.ipBan.dialog.body", remotePlayer.DisplayName),
                        BasisLocalization.Get("menu.individualPlayer.ipBan"),
                        BasisLocalization.Get("ui.cancel"),
                        confirmed => { if (confirmed) BasisNetworkModeration.SendIPBan(targetUUID, ""); });
                };

                PanelButton teleportToBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                teleportToBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.teleportTo"));
                teleportToBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.teleportTo.description"));
                teleportToBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                        BasisNetworkModeration.TryTeleportToPlayer(np.playerId);
                };

                PanelButton teleportHereBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                teleportHereBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.teleportHere"));
                teleportHereBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.teleportHere.description"));
                teleportHereBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                        BasisNetworkModeration.TeleportHere(np.playerId);
                };

                PanelButton shoutBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                bool isShouting = false;
                if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer shoutNp))
                    isShouting = BasisShoutAudioDriver.IsInShoutMode(shoutNp.playerId);
                shoutBtn.Descriptor.SetTitle(BasisLocalization.Get(isShouting ? "menu.individualPlayer.shout.disable" : "menu.individualPlayer.shout.enable"));
                shoutBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.shout.description"));
                shoutBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                    {
                        bool active = BasisShoutAudioDriver.IsInShoutMode(np.playerId);
                        if (active)
                            BasisNetworkModeration.DisableShoutMode(np.playerId);
                        else
                            BasisNetworkModeration.EnableShoutMode(np.playerId);
                        shoutBtn.Descriptor.SetTitle(BasisLocalization.Get(active ? "menu.individualPlayer.shout.enable" : "menu.individualPlayer.shout.disable"));
                    }
                };

                PanelTextField msgField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
                msgField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.message"));
                msgField.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.message.description"));

                PanelButton sendMsgBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                sendMsgBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.sendMessage"));
                sendMsgBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.sendMessage.description"));
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
                permGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.permissions"));
                permGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.permissions.description"));

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
                nodeDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.permissionNode"));
                nodeDropdown.AssignEntries(knownNodes);

                PanelTextField customNodeField = PanelTextField.CreateNewEntry(permGroup.ContentParent);
                customNodeField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.customNode"));
                customNodeField.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.customNode.description"));

                PanelButton addNodeBtn = PanelButton.CreateNew(permGroup.ContentParent);
                addNodeBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.grantPermission"));
                addNodeBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.grantPermission.description"));
                addNodeBtn.OnClicked += () =>
                {
                    string node = !string.IsNullOrWhiteSpace(customNodeField.Value)
                        ? customNodeField.Value
                        : nodeDropdown.SelectedString;
                    if (string.IsNullOrWhiteSpace(node)) return;
                    BasisNetworkModeration.SetUserNode(targetUUID, node, true);
                };

                PanelButton removeNodeBtn = PanelButton.CreateNew(permGroup.ContentParent);
                removeNodeBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.revokePermission"));
                removeNodeBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.revokePermission.description"));
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
                groupSection.SetTitle(BasisLocalization.Get("menu.individualPlayer.groups"));
                groupSection.SetDescription(BasisLocalization.Get("menu.individualPlayer.groups.description"));

                PanelTextField groupField = PanelTextField.CreateNewEntry(groupSection.ContentParent);
                groupField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.groupName"));
                groupField.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.groupName.description"));

                PanelButton addGroupBtn = PanelButton.CreateNew(groupSection.ContentParent);
                addGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.addToGroup"));
                addGroupBtn.OnClicked += () =>
                {
                    string group = groupField.Value;
                    if (string.IsNullOrWhiteSpace(group)) return;
                    BasisNetworkModeration.SetUserGroup(targetUUID, group, true);
                };

                PanelButton removeGroupBtn = PanelButton.CreateNew(groupSection.ContentParent);
                removeGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.removeFromGroup"));
                removeGroupBtn.OnClicked += () =>
                {
                    string group = groupField.Value;
                    if (string.IsNullOrWhiteSpace(group)) return;
                    BasisNetworkModeration.SetUserGroup(targetUUID, group, false);
                };
            }

            var debugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            debugGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.debug"));
            debugGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.debug.description"));

            var debugField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, debugGroup.ContentParent);
            debugField.SetTitle(BasisLocalization.Get("menu.individualPlayer.transmission"));
            debugField.SetDescription(BasisLocalization.Get("menu.individualPlayer.waitingForData"));

            // ---- Audio Debug Section ----
            var audioDebugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            audioDebugGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug"));
            audioDebugGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.audioDebug.description"));

            // Toggle to show/hide the audio debug fields for this player
            PanelToggle audioDebugToggle = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            audioDebugToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.showAudioDebug"));
            audioDebugToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.showAudioDebug.description"));
            audioDebugToggle.AssignBinding(BasisSettingsDefaults.AudioDebugEnabled);

            // Create all the audio debug fields
            PanelElementDescriptor audioSourceField = null;
            PanelElementDescriptor volumeChainField = null;
            PanelElementDescriptor decodedBufferField = null;
            PanelElementDescriptor encodedBufferField = null;
            PanelElementDescriptor silenceField = null;
            PanelElementDescriptor visemeField = null;

            void CreateAudioDebugFields()
            {
                // Clear existing children below the toggle (skip index 0 which is the toggle)
                // Create fresh fields based on current section toggles
                if (BasisSettingsDefaults.AudioDebugShowSource.RawValue)
                {
                    audioSourceField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    audioSourceField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.source"));
                    audioSourceField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AudioDebugShowVolume.RawValue)
                {
                    volumeChainField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    volumeChainField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.volumeChain"));
                    volumeChainField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AudioDebugShowRingBuffer.RawValue)
                {
                    decodedBufferField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    decodedBufferField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.decodedPcm"));
                    decodedBufferField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AudioDebugShowJitter.RawValue)
                {
                    encodedBufferField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    encodedBufferField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.encodedPackets"));
                    encodedBufferField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AudioDebugShowSilence.RawValue)
                {
                    silenceField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    silenceField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.silence"));
                    silenceField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AudioDebugShowViseme.RawValue)
                {
                    visemeField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, audioDebugGroup.ContentParent);
                    visemeField.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.viseme"));
                    visemeField.SetDescription("...");
                }
            }

            // Initially create fields if audio debug is enabled
            if (BasisSettingsDefaults.AudioDebugEnabled.RawValue)
            {
                CreateAudioDebugFields();
            }

            var updater = panel.gameObject.AddComponent<IndividualPlayerPanelUpdater>();
            updater.RemotePlayer = remotePlayer;
            updater.DebugField = debugField;
            updater.DistanceField = distanceField;
            updater.LodField = lodField;
            updater.RangesField = rangesField;
            updater.BufferField = bufferField;

            // Wire audio debug fields
            updater.AudioSourceField = audioSourceField;
            updater.VolumeChainField = volumeChainField;
            updater.DecodedBufferField = decodedBufferField;
            updater.EncodedBufferField = encodedBufferField;
            updater.SilenceField = silenceField;
            updater.VisemeField = visemeField;

            // When toggled on/off, show/hide the audio debug fields
            audioDebugToggle.OnValueChanged += enabled =>
            {
                // Destroy existing fields
                if (audioSourceField != null) { UnityEngine.Object.Destroy(audioSourceField.gameObject); audioSourceField = null; }
                if (volumeChainField != null) { UnityEngine.Object.Destroy(volumeChainField.gameObject); volumeChainField = null; }
                if (decodedBufferField != null) { UnityEngine.Object.Destroy(decodedBufferField.gameObject); decodedBufferField = null; }
                if (encodedBufferField != null) { UnityEngine.Object.Destroy(encodedBufferField.gameObject); encodedBufferField = null; }
                if (silenceField != null) { UnityEngine.Object.Destroy(silenceField.gameObject); silenceField = null; }
                if (visemeField != null) { UnityEngine.Object.Destroy(visemeField.gameObject); visemeField = null; }

                if (enabled)
                {
                    CreateAudioDebugFields();
                }

                // Re-wire updater references
                updater.AudioSourceField = audioSourceField;
                updater.VolumeChainField = volumeChainField;
                updater.DecodedBufferField = decodedBufferField;
                updater.EncodedBufferField = encodedBufferField;
                updater.SilenceField = silenceField;
                updater.VisemeField = visemeField;
            };

            var uuidField = PanelTextField.CreateNewEntry(root);
            uuidField.Descriptor.SetTitle("UUID");
            uuidField.SetValueWithoutNotify(remotePlayer.UUID);
            uuidField._inputField.readOnly = true;

            panel.Descriptor.ForceRebuild();
            panel.Descriptor.ForceRebuild();
        }
    }
}
