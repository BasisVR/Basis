using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
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
using P2PState = Basis.Scripts.Networking.BasisP2PManager.P2PSessionState;

namespace Basis.BasisUI
{
    public partial class IndividualPlayerProvider : BasisMenuActionProvider<BasisMainMenu>
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
        public override int Order => 80;
        public override bool Hidden => true;

        // ---- Context (who are we editing?) ----
        public static BasisRemotePlayer remotePlayer;

        // ======== Static Highlight Beacon (persists across UI open/close) ========

        private const float BeaconHeight = 20f;

        private static GameObject s_beaconGO;
        private static LineRenderer s_beaconLine;
        private static BasisNetworkPlayer s_beaconTarget;
        private static float s_beaconElapsed;

        // Per-UUID volume snapshot taken when the user mutes via the panel,
        // so unmute can restore the prior level instead of guessing 1.0.
        // Session-only — block/unblock and persisted volume already handle long-term state.
        private static readonly Dictionary<string, float> s_volumeBeforeMute = new Dictionary<string, float>();

        // Volume slider runs 0..1.5. Past 1.0 the player is being amplified beyond their own
        // level; past 1.25 it is heavy enough to warrant the hotter accent.
        private const float VolumeBoostTintThreshold = 1f;
        private const float VolumeBoostTintHotThreshold = 1.25f;

        // No override in force means the player is heard at their own level. Matches the volume a
        // fresh BasisPlayerSettingsData is created with, and is where a reset gesture returns to.
        private const float DefaultVolume = 1.0f;
        private const float UnmuteFallbackVolume = DefaultVolume;

        // ===== Shared player action helpers (used by this panel and UserListProvider rows) =====

        /// <summary>
        /// Toggle mute for the given player. When muting, the prior volume is snapshotted
        /// per-UUID so unmute restores it; if no snapshot exists (e.g. they were already at 0),
        /// unmute falls back to <see cref="UnmuteFallbackVolume"/>.
        /// Returns the new muted state (true = now muted).
        /// </summary>
        public static async Task<bool> ToggleMute(BasisRemotePlayer player)
        {
            if (player == null) return false;

            var s = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);

            float newVolume;
            if (s.VolumeLevel > 0f)
            {
                s_volumeBeforeMute[player.UUID] = s.VolumeLevel;
                newVolume = 0f;
            }
            else
            {
                newVolume = s_volumeBeforeMute.TryGetValue(player.UUID, out float prior) && prior > 0f
                    ? prior
                    : UnmuteFallbackVolume;
            }

            s.VolumeLevel = newVolume;
            await BasisPlayerSettingsManager.SetPlayerSettings(s);

            if (player.NetworkReceiver != null && player.NetworkReceiver.AudioReceiverModule != null)
            {
                player.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(
                    player.IsEffectivelyBlocked ? 0f : newVolume);
            }

            return newVolume <= 0f;
        }

        /// <summary>
        /// Synchronously checks whether the player's persisted volume is currently 0.
        /// Reads the settings cache without going through the async request — used to
        /// initialize button labels without waiting on disk I/O.
        /// </summary>
        public static async Task<bool> IsMutedAsync(BasisRemotePlayer player)
        {
            if (player == null) return false;
            var s = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);
            return s.VolumeLevel <= 0f;
        }

        /// <summary>
        /// Toggle the block state for the given player. If transitioning from unblocked → blocked,
        /// shows a "Do you want to block this player?" confirmation dialog; cancelling leaves state
        /// unchanged. Unblock has no confirmation (matches the toggle-off pattern used elsewhere).
        /// Returns the new IsBlocked value.
        /// </summary>
        public static async Task<bool> ToggleBlockWithConfirmation(BasisRemotePlayer player)
        {
            if (player == null) return false;

            var current = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);

            if (!current.IsBlocked)
            {
                var tcs = new TaskCompletionSource<bool>();
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("menu.individualPlayer.block.dialog.title"),
                    BasisLocalization.Get("menu.individualPlayer.block.dialog.body", player.DisplayName),
                    BasisLocalization.Get("menu.individualPlayer.blockButton"),
                    BasisLocalization.Get("ui.cancel"),
                    confirmed => tcs.SetResult(confirmed),
                    category: BasisNotificationCategory.Player);

                if (!await tcs.Task) return current.IsBlocked;
            }

            var s = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);
            s.IsBlocked = !s.IsBlocked;
            await BasisPlayerSettingsManager.SetPlayerSettings(s);

            player.IsBlocked = s.IsBlocked;

            if (player.NetworkReceiver != null && player.NetworkReceiver.AudioReceiverModule != null)
            {
                player.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(
                    player.IsEffectivelyBlocked ? 0f : s.VolumeLevel);
            }

            player.ReloadAvatar();

            if (player.IsEffectivelyBlocked)
            {
                player.IsChatTyping = false;
                player.OnChatTypingStateChanged?.Invoke(false);
                player.OnChatMessageReceived?.Invoke(string.Empty);
            }
            player.OnNamePlateActiveStateShouldRefresh?.Invoke();
#if !UNITY_SERVER
            BasisNetworkPIPCameraDriver.RefreshPipNamePlateVisibilityFromPlayerState();
#endif

            // Mirror the block onto the other side so they also can't see/hear us
            // (session-scoped temp block).
            if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                    player, out BasisNetworkPlayer blockTargetNet))
            {
                BasisNetworkHandleTempBlock.SendTempBlock(blockTargetNet.playerId, s.IsBlocked);
            }

            return s.IsBlocked;
        }

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
                basePos = s_beaconTarget.Player.Transform.position + Vector3.up * 1.5f;
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
                BasisMainMenu.CloseActivePanel();
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

            TextMeshProUGUI titleLabel = panel.Descriptor.TitleLabel;
            if (titleLabel != null) titleLabel.text = target.DisplayName;

            // Each page is populated before it is handed to the group, so its rows are built while
            // the page is still active and their deferred Awake cannot overwrite the titles here.
            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);

            PanelTabPage NewPage(string tabKey, string iconAddress)
            {
                PanelTabPage page = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
                page.Descriptor.SetIcon(iconAddress);
                page.Descriptor.SetTitle(BasisLocalization.Get(tabKey));
                return page;
            }

            void AddPage(string tabKey, PanelTabPage page)
            {
                tabGroup.AddTab(BasisLocalization.Get(tabKey), () => { }, page);
            }

            void RebuildSection(PanelElementDescriptor group, PanelTabPage page)
            {
                if (group == null || page == null) return;
                PanelElementDescriptor.RebuildLayoutChain(group.ContentParent, page.Descriptor.ContentParent);
            }

            // Per-player settings are read once up front because the Actions tab needs them
            // before any of the detail tabs that own the same toggles have been built.
            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);

            // Every control the Actions tab duplicates registers its repaint here, so pressing
            // a row on either side leaves the other side showing the truth.
            PlayerActionSync sync = new PlayerActionSync();

            // ================= Actions =================
            // First tab, and therefore the landing page: the panel opens on the things people
            // came here to do rather than on a page of read-only metadata.
            const string actionsTabKey = "menu.individualPlayer.actions";
            PanelTabPage actionsPage = NewPage(actionsTabKey, AddressableAssets.Sprites.List);

            // Ahead of every action, because the usual reason for opening this panel off a
            // nameplate is "why can I not see this person?" — that answer leads instead of being
            // spread across the Avatar tab's load-error card, its performance card, and the range
            // readouts on Debug. Built before the action grid so it is the first row on the page,
            // and repainted by IndividualPlayerPanelUpdater because everything it reads (range,
            // a download finishing, the far avatar swapping in) moves without the panel being
            // touched.
            var avatarStatusField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, actionsPage.Descriptor.ContentParent);
            // Before the first paint: the card quotes AvatarLoadErrorMessage and
            // PerformanceBlockReason verbatim, and neither is ours to trust as markup.
            avatarStatusField.DisableRichText();
            BasisPanelTint.Handle avatarStatusTint = BasisPanelTint.Capture(avatarStatusField);
            PaintAvatarStatus(remotePlayer, avatarStatusField, avatarStatusTint, false);

            BuildActionsPage(actionsPage, remotePlayer, settings, sync);
            AddPage(actionsTabKey, actionsPage);

            // ================= Player =================
            const string playerTabKey = "menu.individualPlayer.player";
            PanelTabPage playerPage = NewPage(playerTabKey, AddressableAssets.Sprites.Information);
            RectTransform root = playerPage.Descriptor.ContentParent;

            var infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            infoGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.player"));
            infoGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.player.description"));

            // No Name row — the panel header already carries the display name.
            var PlatformDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, infoGroup.ContentParent);
            PlatformDescriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.platform"));
            PlatformDescriptor.SetDescription(remotePlayer.PlayerPlatform);

            // ---- Highlight beacon controls ----
            var locateGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            locateGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.locate"));
            locateGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.locate.description"));

            PanelButton highlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            highlightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.highlight.description"));

            PanelButton clearHighlightBtn = PanelButton.CreateNew(locateGroup.ContentParent);
            clearHighlightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.clearHighlights"));
            clearHighlightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.clearHighlights.description"));

            void PaintDetailHighlight()
            {
                if (highlightBtn == null || highlightBtn.Descriptor == null) return;
                highlightBtn.Descriptor.SetTitle(BasisLocalization.Get(IsHighlighted(remotePlayer)
                    ? "menu.individualPlayer.removeHighlight" : "menu.individualPlayer.highlight"));
            }
            PaintDetailHighlight();
            sync.Highlight += PaintDetailHighlight;

            highlightBtn.OnClicked += () =>
            {
                if (Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                    remotePlayer, out BasisNetworkPlayer netPlayer))
                {
                    SetHighlight(netPlayer);
                }
                sync.Highlight?.Invoke();
            };

            clearHighlightBtn.OnClicked += () =>
            {
                ClearHighlight();
                sync.Highlight?.Invoke();
            };

            // ---- Pin controls ----
            var pinGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            pinGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.pin"));
            pinGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.pin.description"));

            string pinUuid = remotePlayer.UUID;
            PanelButton pinBtn = PanelButton.CreateNew(pinGroup.ContentParent);
            pinBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.pin.button.description"));

            void PaintDetailPin()
            {
                if (pinBtn == null || pinBtn.Descriptor == null) return;
                pinBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    PinnedPlayers.IsPinned(pinUuid) ? "menu.individualPlayer.unpin" : "menu.individualPlayer.pinButton"));
            }
            PaintDetailPin();
            sync.Pin += PaintDetailPin;

            pinBtn.OnClicked += () =>
            {
                PinnedPlayers.Toggle(pinUuid);
                sync.Pin?.Invoke();
            };

            var uuidField = PanelTextField.CreateNewEntry(root);
            uuidField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.uuid"));
            uuidField.SetValueWithoutNotify(remotePlayer.UUID);
            uuidField._inputField.readOnly = true;

            AddPage(playerTabKey, playerPage);

            // ================= Audio =================
            const string audioTabKey = "settings.tab.audio";
            PanelTabPage audioPage = NewPage(audioTabKey, AddressableAssets.Sprites.Microphone);
            root = audioPage.Descriptor.ContentParent;

            var audioGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            audioGroup.SetTitle(BasisLocalization.Get("settings.tab.audio"));
            audioGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.audio.description"));

            // Deliberately unbound: this level belongs to one player, in their
            // BasisPlayerSettingsData record, and the OnValueChanged below is what persists it. A
            // settings binding would write every player's choice into one shared global key, and
            // would hand the reset gesture that key's default - zero, i.e. silence - instead of
            // DefaultVolume, so the explicit reset default is set here.
            PanelSlider volumeSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, audioGroup.ContentParent);
            volumeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("menu.individualPlayer.volumeOverride"), 0f, 1.5f, false, 2, ValueDisplayMode.percentageFromZero));
            volumeSlider.SetResetDefault(DefaultVolume);

            // Slider runs 0..1.5; place green at the "100%" mark (t = 1/1.5) and red at 150%.
            volumeSlider.FillColorGradient = new Gradient()
            {
                colorKeys = new[]
                {
                    new GradientColorKey(new Color(0.1f, 0.4f, 1.0f), 0f),
                    new GradientColorKey(new Color(0.0f, 1.0f, 0.3f), 1f / 1.5f),
                    new GradientColorKey(new Color(1.0f, 0.1f, 0.1f), 1f),
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                }
            };
            volumeSlider.UseFillColorGradient = true;

            volumeSlider.SetValueWithoutNotify(settings.VolumeLevel);

            // Boosting a player past their natural loudness is worth seeing at a glance, so the
            // row itself picks up the same accent tinting the Graphics page uses.
            BasisPanelTint.Handle boostTint = BasisPanelTint.Capture(volumeSlider.Descriptor);

            void RefreshBoostTint(float level, bool animate)
            {
                if (level <= VolumeBoostTintThreshold)
                {
                    BasisPanelTint.Clear(boostTint, animate);
                    return;
                }

                BasisPanelTint.Apply(boostTint,
                    level > VolumeBoostTintHotThreshold ? BasisPanelTint.Hot : BasisPanelTint.Caution,
                    animate);
            }

            RefreshBoostTint(settings.VolumeLevel, false);
            volumeSlider.OnValueChanged += level => RefreshBoostTint(level, true);

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

            sampler.Initialize(remotePlayer);

            // Wire slider -> apply to receiver -> save. Bumped and captured on every call so a
            // change that is still awaiting its disk write can tell it has been superseded by a
            // newer one and skip its tail-end repaint instead of snapping the slider/tint back to
            // a stale value once that write finally completes.
            int volumeChangeSeq = 0;
            volumeSlider.OnValueChanged += async raw =>
            {
                float value = Mathf.Clamp(raw, 0f, 1.5f);
                int mySeq = ++volumeChangeSeq;

                // Apply to the live audio path first and immediately - this is what the player
                // hears, so it must not wait on the settings file write below. Gated behind that
                // write, a burst of quick adjustments audibly lagged behind the slider.
                if (remotePlayer != null)
                {
                    remotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(
                        remotePlayer.IsEffectivelyBlocked ? 0f : value);
                }

                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.VolumeLevel = value;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                // Superseded by a newer adjustment while the write above was in flight - that
                // one owns the repaint now, so don't drag the slider/tint back to this stale value.
                if (mySeq != volumeChangeSeq) return;

                // Dragging to (or off) zero is a mute, so the Actions tab has to follow.
                sync.Volume?.Invoke(s);
            };

            // ---- Loudness normalisation (receive-side, per player) ----
            PanelToggle normalizeToggle = PanelToggle.CreateNewEntry(audioGroup.ContentParent);
            normalizeToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.normalizeLoudness"));
            normalizeToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.normalizeLoudness.description"));
            normalizeToggle.SetValueWithoutNotify(settings.NormalizeLoudness);

            normalizeToggle.OnValueChanged += async enabled =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.NormalizeLoudness = enabled;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null && remotePlayer.NetworkReceiver != null &&
                    remotePlayer.NetworkReceiver.AudioReceiverModule != null)
                {
                    remotePlayer.NetworkReceiver.AudioReceiverModule.NormalizeLoudness = enabled;
                }
            };

            // ---- Mute toggle (audio-only, separate from full block) ----
            PanelButton muteBtn = PanelButton.CreateNew(audioGroup.ContentParent);
            muteBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.mute.description"));

            // Also drags the slider back to whatever mute/unmute resolved to, so the helper's
            // restore-from-snapshot logic is visible rather than just audible.
            void PaintDetailVolume(BasisPlayerSettingsData s)
            {
                if (muteBtn != null && muteBtn.Descriptor != null)
                {
                    muteBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        s.VolumeLevel <= 0f ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
                }
                if (volumeSlider != null)
                {
                    volumeSlider.SetValueWithoutNotify(s.VolumeLevel);
                    RefreshBoostTint(s.VolumeLevel, true);
                }
            }
            // Initial paint only needs the button — the slider and its tint were already set
            // above, and re-setting them here would tween a row that has not been shown yet.
            muteBtn.Descriptor.SetTitle(BasisLocalization.Get(
                settings.VolumeLevel <= 0f ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
            sync.Volume += PaintDetailVolume;

            muteBtn.OnClicked += async () =>
            {
                await ToggleMute(remotePlayer);
                sync.Volume?.Invoke(await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID));
            };

            // ---- Voice recording (consent-gated capture / re-route) ----
            var voiceRecGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            voiceRecGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.voiceRecording"));
            voiceRecGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.voiceRecording.description"));

            ushort voiceRecPlayerId = 0;
            bool hasVoiceRecTarget = Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                remotePlayer, out BasisNetworkPlayer voiceRecNet);
            if (hasVoiceRecTarget) voiceRecPlayerId = voiceRecNet.playerId;

            Basis.Scripts.Networking.VoiceRecording.BasisRecordingHandle voiceRecHandle = null;
            PanelButton recordBtn = PanelButton.CreateNew(voiceRecGroup.ContentParent);
            recordBtn.Descriptor.SetTitle(BasisLocalization.Get(
                hasVoiceRecTarget && Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.IsRecording(voiceRecPlayerId)
                    ? "menu.individualPlayer.voiceRecording.stop"
                    : "menu.individualPlayer.voiceRecording.start"));
            recordBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.voiceRecording.start.description"));
            recordBtn.OnClicked += async () =>
            {
                if (!hasVoiceRecTarget) return;
                if (Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.IsRecording(voiceRecPlayerId))
                {
                    Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.StopRecording(voiceRecPlayerId);
                    voiceRecHandle = null;
                    recordBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.voiceRecording.start"));
                    return;
                }
                recordBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.voiceRecording.requesting"));
                voiceRecHandle = await Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.StartRecording(
                    remotePlayer, Basis.Scripts.Networking.VoiceRecording.BasisRecordingOptions.Default);
                if (recordBtn == null) return;
                recordBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    voiceRecHandle != null
                        ? "menu.individualPlayer.voiceRecording.stop"
                        : "menu.individualPlayer.voiceRecording.start"));
            };

            if (!string.IsNullOrEmpty(remotePlayer.UUID))
            {
                PanelDropdown recordPolicy = PanelDropdown.CreateNewEntry(voiceRecGroup.ContentParent);
                recordPolicy.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy"));
                recordPolicy.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.description"));

                // Order must match BasisRecordingConsentPolicy (Ask, AlwaysAllow, AlwaysDeny).
                List<string> recordPolicyOptions = new List<string>
                {
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.ask"),
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.allow"),
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.deny"),
                };
                recordPolicy.AssignEntries(recordPolicyOptions, null, new List<string>
                {
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.ask.tooltip"),
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.allow.tooltip"),
                    BasisLocalization.Get("menu.individualPlayer.voiceRecording.policy.deny.tooltip"),
                });
                recordPolicy.SetValueWithoutNotify(recordPolicyOptions[(int)BasisRecordingConsent.GetPolicy(remotePlayer.UUID)]);
                recordPolicy.OnValueChanged = selected =>
                {
                    int idx = recordPolicyOptions.IndexOf(selected);
                    if (idx < 0) idx = 0;
                    BasisRecordingConsent.SetPolicy(remotePlayer.UUID, (BasisRecordingConsentPolicy)idx);
                    if ((BasisRecordingConsentPolicy)idx == BasisRecordingConsentPolicy.AlwaysDeny && hasVoiceRecTarget)
                    {
                        Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.RevokeConsentToRecorder(voiceRecPlayerId);
                    }
                };
            }

            // ---- Private chat (who can hear me) ----
            var privateChatGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            privateChatGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.privateChat"));
            privateChatGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.privateChat.description"));

            ushort privateChatPlayerId = 0;
            bool hasPrivateChatTarget = Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                remotePlayer, out BasisNetworkPlayer privateChatNet);
            if (hasPrivateChatTarget) privateChatPlayerId = privateChatNet.playerId;

            PanelButton privateChatBtn = PanelButton.CreateNew(privateChatGroup.ContentParent);
            privateChatBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.privateChat.add.description"));

            PanelButton talkToOnlyBtn = PanelButton.CreateNew(privateChatGroup.ContentParent);
            talkToOnlyBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.talkToOnly.description"));

            void PaintDetailTalkModes()
            {
                if (privateChatBtn != null && privateChatBtn.Descriptor != null)
                {
                    privateChatBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasPrivateChatTarget && Basis.Scripts.Networking.BasisTalkModeManager.IsPrivateMember(privateChatPlayerId)
                            ? "menu.individualPlayer.privateChat.remove"
                            : "menu.individualPlayer.privateChat.add"));
                }
                if (talkToOnlyBtn != null && talkToOnlyBtn.Descriptor != null)
                {
                    talkToOnlyBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasPrivateChatTarget && Basis.Scripts.Networking.BasisTalkModeManager.IsTalkingOnlyTo(privateChatPlayerId)
                            ? "menu.individualPlayer.talkToOnly.stop"
                            : "menu.individualPlayer.talkToOnly"));
                }
            }
            PaintDetailTalkModes();
            sync.TalkModes += PaintDetailTalkModes;

            privateChatBtn.OnClicked += () =>
            {
                if (!hasPrivateChatTarget) return;
                Basis.Scripts.Networking.BasisTalkModeManager.TogglePrivateMember(privateChatPlayerId);
                sync.TalkModes?.Invoke();
            };

            talkToOnlyBtn.OnClicked += () =>
            {
                if (!hasPrivateChatTarget) return;
                if (Basis.Scripts.Networking.BasisTalkModeManager.IsTalkingOnlyTo(privateChatPlayerId))
                    Basis.Scripts.Networking.BasisTalkModeManager.StopThisPerson();
                else
                    Basis.Scripts.Networking.BasisTalkModeManager.SetThisPersonTarget(privateChatPlayerId);
                sync.TalkModes?.Invoke();
            };

            AddPage(audioTabKey, audioPage);

            // ================= Network =================
            const string networkTabKey = "menu.individualPlayer.network";
            PanelTabPage networkPage = NewPage(networkTabKey, AddressableAssets.Sprites.Network);
            root = networkPage.Descriptor.ContentParent;

            // ---- Direct Connection (P2P) controls ----
            var p2pGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            p2pGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.directConnection"));
            p2pGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.directConnection.description"));

            ushort directConnPlayerId = 0;
            bool hasDirectConnTarget = Basis.Scripts.Networking.BasisNetworkPlayers.PlayerToNetworkedPlayer(
                remotePlayer, out BasisNetworkPlayer p2pNetTarget);
            if (hasDirectConnTarget) directConnPlayerId = p2pNetTarget.playerId;

            PanelButton directConnBtn = PanelButton.CreateNew(p2pGroup.ContentParent);

            // Where the link actually is. The card carries the state and grades itself with the
            // Calm/Caution/Hot tint the frame bottleneck card uses, so the button next to it is
            // free to say what clicking does rather than doubling as the readout.
            var directConnStatus = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, p2pGroup.ContentParent);
            directConnStatus.SetDescription(BasisLocalization.Get("menu.individualPlayer.directConnection.partial.description"));
            directConnStatus.SetActive(false);
            BasisPanelTint.Handle directConnTint = BasisPanelTint.Capture(directConnStatus);

            // "Try Again" — visible only when the link is partial or failed. Re-punches the
            // existing session, or re-requests if it was already torn down.
            PanelButton directConnRetryBtn = PanelButton.CreateNew(p2pGroup.ContentParent);
            directConnRetryBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.directConnection.tryAgain"));
            directConnRetryBtn.Descriptor.SetActive(false);
            directConnRetryBtn.OnClicked += () =>
            {
                if (!hasDirectConnTarget) return;
                Basis.Scripts.Networking.BasisP2PManager.RetryDirect(directConnPlayerId);
            };

            // What clicking the button does from here — the state itself is on the status card.
            string DirectConnActionKey(P2PState st)
            {
                if (DirectConnectionBlocked(st))
                {
                    return "menu.individualPlayer.directConnection.disabled";
                }
                // Mirrors the click handler below: only Idle and Failed start a new request.
                return st == P2PState.Idle || st == P2PState.Failed
                    ? "menu.individualPlayer.directConnection.request"
                    : "menu.individualPlayer.directConnection.cancel";
            }

            // Null when there is nothing to report and the card stays hidden.
            string DirectConnStatusKey(P2PState st)
            {
                if (DirectConnectionBlocked(st))
                {
                    return null;
                }
                switch (st)
                {
                    case P2PState.OutgoingRequested:
                        return "menu.individualPlayer.directConnection.requesting";
                    case P2PState.OutgoingArmed:
                    case P2PState.IncomingPending:
                        return "menu.individualPlayer.directConnection.awaitingAccept";
                    case P2PState.Punching:
                        return "menu.individualPlayer.directConnection.punching";
                    case P2PState.Connected:
                        return Basis.Scripts.Networking.BasisP2PManager.IsP2PSessionLocal(directConnPlayerId)
                            ? "menu.individualPlayer.directConnection.connectedLan"
                            : "menu.individualPlayer.directConnection.connected";
                    case P2PState.Reconnecting:
                        return "menu.individualPlayer.directConnection.reconnecting";
                    case P2PState.PartialConnection:
                        return "menu.individualPlayer.directConnection.partial";
                    case P2PState.Failed:
                        return "menu.individualPlayer.directConnection.failed";
                    default:
                        return null;
                }
            }

            BasisPanelSeverity DirectConnSeverity(P2PState st)
            {
                switch (st)
                {
                    case P2PState.Connected:
                        return BasisPanelSeverity.Calm;
                    case P2PState.OutgoingRequested:
                    case P2PState.OutgoingArmed:
                    case P2PState.IncomingPending:
                    case P2PState.Punching:
                    case P2PState.Reconnecting:
                        return BasisPanelSeverity.Caution;
                    case P2PState.PartialConnection:
                    case P2PState.Failed:
                        return BasisPanelSeverity.Hot;
                    default:
                        return BasisPanelSeverity.None;
                }
            }

            void RefreshDirectConnLabel()
            {
                if (directConnBtn == null || directConnBtn.Descriptor == null) return;
                P2PState st = Basis.Scripts.Networking.BasisP2PManager.GetSessionState(directConnPlayerId);
                directConnBtn.Descriptor.SetTitle(BasisLocalization.Get(DirectConnActionKey(st)));

                string statusKey = DirectConnStatusKey(st);
                bool showStatus = statusKey != null;
                if (directConnStatus != null)
                {
                    if (showStatus)
                    {
                        string status = BasisLocalization.Get(statusKey);
                        // Partial is the one state that needs explaining: the server is carrying
                        // this player, and Try Again re-establishes the direct link.
                        if (st == P2PState.PartialConnection)
                        {
                            status += "\n" + BasisLocalization.Get("menu.individualPlayer.directConnection.partial.description");
                        }
                        directConnStatus.SetDescription(status);
                    }

                    directConnStatus.SetActive(showStatus);
                    // Animate only while the card is on screen — a tween started on a disabled
                    // object never ticks, which would leave the colour stuck part-way.
                    BasisPanelTint.Apply(directConnTint, DirectConnSeverity(st), showStatus);
                }

                // Partial/Failed: offer Try Again.
                bool canRetry = st == P2PState.PartialConnection || st == P2PState.Failed;
                if (directConnRetryBtn != null && directConnRetryBtn.Descriptor != null)
                    directConnRetryBtn.Descriptor.SetActive(canRetry);
                RebuildSection(p2pGroup, networkPage);
            }
            RefreshDirectConnLabel();
            sync.DirectConnection += RefreshDirectConnLabel;

            directConnBtn.OnClicked += () =>
            {
                ToggleDirectConnection(remotePlayer);
                sync.DirectConnection?.Invoke();
            };

            // Marshal to main thread — manager fires from the LiteNetLib I/O thread.
            Action<ushort, P2PState> p2pHandler = null;
            p2pHandler = (changedId, _) =>
            {
                if (changedId != directConnPlayerId) return;
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    if (directConnBtn == null || directConnBtn.Descriptor == null)
                    {
                        Basis.Scripts.Networking.BasisP2PManager.OnSessionStateChanged -= p2pHandler;
                        return;
                    }
                    sync.DirectConnection?.Invoke();
                });
            };
            Basis.Scripts.Networking.BasisP2PManager.OnSessionStateChanged += p2pHandler;

            var directConnPingField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, p2pGroup.ContentParent);
            directConnPingField.SetTitle(BasisLocalization.Get("menu.individualPlayer.directConnection.ping"));
            directConnPingField.SetDescription(BasisLocalization.Get("menu.individualPlayer.directConnection.ping.value", 0));
            // Hidden until the updater observes a Connected P2P session — see
            // IndividualPlayerPanelUpdater.UpdateDirectConnPingField, which also grades the
            // round trip onto the shared tint.
            directConnPingField.SetActive(false);
            BasisPanelTint.Handle directConnPingTint = BasisPanelTint.Capture(directConnPingField);

            // Per-person policy for *incoming* direct-connection requests from this
            // player. Saved to disc and consulted by BasisP2PIncomingDialog before it
            // prompts — "Always accept" trusts them, "Always decline" blocks silently.
            if (!string.IsNullOrEmpty(remotePlayer.UUID))
            {
                PanelDropdown directConnPolicy = PanelDropdown.CreateNewEntry(p2pGroup.ContentParent);
                directConnPolicy.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.directConnection.policy"));
                directConnPolicy.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.directConnection.policy.description"));

                // Order must match BasisDirectConnectionPolicy (Ask, AlwaysAccept, AlwaysDecline).
                List<string> policyOptions = new List<string>
                {
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.ask"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.accept"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.decline"),
                };
                directConnPolicy.AssignEntries(policyOptions, null, new List<string>
                {
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.ask.tooltip"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.accept.tooltip"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.policy.decline.tooltip"),
                });
                directConnPolicy.SetValueWithoutNotify(policyOptions[(int)BasisTrustedConnections.GetPolicy(remotePlayer.UUID)]);
                directConnPolicy.OnValueChanged = selected =>
                {
                    int idx = policyOptions.IndexOf(selected);
                    if (idx < 0) idx = 0;
                    BasisTrustedConnections.SetPolicy(remotePlayer.UUID, (BasisDirectConnectionPolicy)idx);
                };
            }

            // The live per-player network readouts that used to sit here now live on the Debug
            // tab, next to the other polled diagnostics. The Network tab is the place you come
            // to change how you are connected to this player, not to watch numbers move.
            AddPage(networkTabKey, networkPage);

            // ================= Avatar =================
            const string avatarTabKey = "menu.individualPlayer.avatar";
            PanelTabPage avatarPage = NewPage(avatarTabKey, AddressableAssets.Sprites.Avatars);
            root = avatarPage.Descriptor.ContentParent;

            var avatarGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            avatarGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatar"));
            avatarGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.avatar.description"));

            if (!string.IsNullOrEmpty(remotePlayer.AvatarLoadErrorMessage))
            {
                var avatarErrorField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarGroup.ContentParent);
                avatarErrorField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarLoadError"));
                avatarErrorField.SetDescription(remotePlayer.AvatarLoadErrorMessage);
                // Nothing to grade — the card only exists because the load failed, so it is hot
                // from the moment it is built.
                BasisPanelTint.Apply(BasisPanelTint.Capture(avatarErrorField), BasisPanelSeverity.Hot, false);
            }

            PanelButton matchEyeHeightBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            matchEyeHeightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.matchEyeHeight"));

            void PaintDetailEyeHeight()
            {
                if (matchEyeHeightBtn == null || matchEyeHeightBtn.Descriptor == null) return;
                matchEyeHeightBtn.Descriptor.SetDescription(
                    BasisHeightDriver.TryGetMatchedEyeHeightOverrideMeters(remotePlayer, out float height)
                        ? BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.description", height)
                        : BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.unavailable"));
            }
            PaintDetailEyeHeight();
            sync.EyeHeight += PaintDetailEyeHeight;

            matchEyeHeightBtn.OnClicked += () =>
            {
                ApplyMatchedEyeHeight(remotePlayer, out _);
                sync.EyeHeight?.Invoke();
            };

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
                        if (perf.CilboxBehavioursTrimmed > 0) parts.Add($"-{perf.CilboxBehavioursTrimmed} cilbox behaviours");
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
            toggleAvatarBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.toggleAvatar.description"));

            void PaintDetailAvatarVisible(BasisPlayerSettingsData s)
            {
                if (toggleAvatarBtn == null || toggleAvatarBtn.Descriptor == null) return;
                toggleAvatarBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    s.AvatarVisible ? "menu.individualPlayer.hideAvatar" : "menu.individualPlayer.showAvatar"));
            }
            PaintDetailAvatarVisible(settings);
            sync.AvatarVisible += PaintDetailAvatarVisible;

            PanelButton toggleInteractionsBtn = PanelButton.CreateNew(avatarGroup.ContentParent);
            toggleInteractionsBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.AvatarInteraction ? "menu.individualPlayer.disableInteractions" : "menu.individualPlayer.enableInteractions"));
            toggleInteractionsBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.toggleInteractions.description"));

            // Manual toggle is the only escape hatch from the global "bail on retries" state set
            // by BasisAvatarFactory.MarkRemoteLoadFailed — ToggleAvatarVisible clears it so
            // showing the avatar again actually re-attempts the download.
            toggleAvatarBtn.OnClicked += async () =>
            {
                sync.AvatarVisible?.Invoke(await ToggleAvatarVisible(remotePlayer));
            };

            toggleInteractionsBtn.OnClicked += async () =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AvatarInteraction = !s.AvatarInteraction;
               await BasisPlayerSettingsManager.SetPlayerSettings(s);

                toggleInteractionsBtn.Descriptor.SetTitle(BasisLocalization.Get(s.AvatarInteraction ? "menu.individualPlayer.disableInteractions" : "menu.individualPlayer.enableInteractions"));

                if (remotePlayer != null)
                {
                    remotePlayer.AvatarInteractionAllowed = s.AvatarInteraction;
                    if (!s.AvatarInteraction && BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer interactionsNet))
                    {
                        Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.RevokePlayer(interactionsNet.playerId);
                    }
                    remotePlayer.ReloadAvatar();
                }
            };

            // Explanation lives on the tooltip rather than inline — the paragraph is long enough
            // to push the rest of the tab off screen for a control whose title already says it.
            PanelToggle jiggleGrabToggle = PanelToggle.CreateNewEntry(avatarGroup.ContentParent);
            jiggleGrabToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.jiggleGrab"));
            jiggleGrabToggle.Descriptor.SetDescription(string.Empty);
            jiggleGrabToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.jiggleGrab.description"));
            jiggleGrabToggle.SetValueWithoutNotify(settings.JiggleGrabAllowed);
            sync.JiggleGrab += s =>
            {
                if (jiggleGrabToggle == null) return;
                jiggleGrabToggle.SetValueWithoutNotify(s.JiggleGrabAllowed);
            };
            jiggleGrabToggle.OnValueChanged += async on =>
            {
                sync.JiggleGrab?.Invoke(await SetJiggleGrabAllowed(remotePlayer, on));
            };

            PanelToggle alwaysShowAvatarToggle = PanelToggle.CreateNewEntry(avatarGroup.ContentParent);
            alwaysShowAvatarToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.alwaysShowAvatar"));
            alwaysShowAvatarToggle.Descriptor.SetDescription(string.Empty);
            alwaysShowAvatarToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.alwaysShowAvatar.description"));
            alwaysShowAvatarToggle.SetValueWithoutNotify(settings.AlwaysShowAvatar);
            alwaysShowAvatarToggle.OnValueChanged += async on =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AlwaysShowAvatar = on;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null)
                {
                    remotePlayer.AlwaysShowAvatar = on;
                    if (remotePlayer.IsConsideredFallBackAvatar == on)
                    {
                        remotePlayer.ReloadAvatar();
                    }
                }
            };

            AddPage(avatarTabKey, avatarPage);

            // ================= Block =================
            const string blockTabKey = "menu.individualPlayer.block";
            PanelTabPage blockPage = NewPage(blockTabKey, AddressableAssets.Sprites.Locked);
            root = blockPage.Descriptor.ContentParent;

            // ---- Block group ----
            var blockGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            blockGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.block"));
            blockGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.block.description"));

            PanelButton toggleBlockBtn = PanelButton.CreateNew(blockGroup.ContentParent);
            toggleBlockBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.block.button.description"));

            void PaintDetailBlock(BasisPlayerSettingsData s)
            {
                if (toggleBlockBtn == null || toggleBlockBtn.Descriptor == null) return;
                toggleBlockBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    s.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
            }
            PaintDetailBlock(settings);
            sync.Blocked += PaintDetailBlock;

            toggleBlockBtn.OnClicked += async () =>
            {
                await ToggleBlockWithConfirmation(remotePlayer);
                sync.Blocked?.Invoke(await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID));
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
                if (!s.ChatVisible && remotePlayer != null)
                {
                    remotePlayer.IsChatTyping = false;
                    remotePlayer.OnChatTypingStateChanged?.Invoke(false);
                    remotePlayer.OnChatMessageReceived?.Invoke(string.Empty);
                }
            };

            AddPage(blockTabKey, blockPage);

            // ---- Admin moderation section (only visible to admins) ----
            if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView))
            {
                // ================= Admin =================
                const string adminTabKey = "settings.tab.admin";
                PanelTabPage adminPage = NewPage(adminTabKey, AddressableAssets.Sprites.Admin);
                root = adminPage.Descriptor.ContentParent;

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
                        confirmed => { if (confirmed) BasisNetworkModeration.SendKick(targetUUID, ""); },
                        category: BasisNotificationCategory.Player);
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
                        confirmed => { if (confirmed) BasisNetworkModeration.SendBan(targetUUID, ""); },
                        category: BasisNotificationCategory.Player);
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
                        confirmed => { if (confirmed) BasisNetworkModeration.SendIPBan(targetUUID, ""); },
                        category: BasisNotificationCategory.Player);
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

                PanelButton announceBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                announceBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.announce.description"));
                bool hasAnnounceTarget = BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer announceNp);
                ushort announcePlayerId = hasAnnounceTarget ? announceNp.playerId : (ushort)0;

                void PaintDetailAnnounce()
                {
                    if (announceBtn == null || announceBtn.Descriptor == null) return;
                    announceBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasAnnounceTarget && BasisAnnounceAudioDriver.IsInAnnounceMode(announcePlayerId)
                            ? "menu.individualPlayer.announce.disable"
                            : "menu.individualPlayer.announce.enable"));
                }
                PaintDetailAnnounce();
                sync.Announce += PaintDetailAnnounce;

                announceBtn.OnClicked += () =>
                {
                    if (!hasAnnounceTarget) return;
                    if (BasisAnnounceAudioDriver.IsInAnnounceMode(announcePlayerId))
                        BasisNetworkModeration.DisableAnnounceMode(announcePlayerId);
                    else
                        BasisNetworkModeration.EnableAnnounceMode(announcePlayerId);
                };

                PanelButton shoutBtn = PanelButton.CreateNew(adminGroup.ContentParent);
                shoutBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.shout.description"));

                void PaintDetailShout()
                {
                    if (shoutBtn == null || shoutBtn.Descriptor == null) return;
                    shoutBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasAnnounceTarget && BasisNetworkModeration.IsInShoutMode(announcePlayerId)
                            ? "menu.individualPlayer.shout.disable"
                            : "menu.individualPlayer.shout.enable"));
                }
                PaintDetailShout();
                sync.Shout += PaintDetailShout;

                shoutBtn.OnClicked += () =>
                {
                    if (!hasAnnounceTarget) return;
                    if (BasisNetworkModeration.IsInShoutMode(announcePlayerId))
                        BasisNetworkModeration.DisableShoutMode(announcePlayerId);
                    else
                        BasisNetworkModeration.EnableShoutMode(announcePlayerId);
                };

                // Server-enforced mutes. The admin client doesn't track the target's current
                // state, so like the full-quality toggle these start off and send the explicit
                // state on change — the server's reply popup reports the authoritative result.
                PanelToggle voiceMuteToggle = PanelToggle.CreateNewEntry(adminGroup.ContentParent);
                voiceMuteToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.muteVoice"));
                voiceMuteToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.muteVoice.tooltip"));
                voiceMuteToggle.OnValueChanged += muted => BasisNetworkModeration.SetVoiceMute(targetUUID, muted);

                PanelToggle textMuteToggle = PanelToggle.CreateNewEntry(adminGroup.ContentParent);
                textMuteToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.muteText"));
                textMuteToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.muteText.tooltip"));
                textMuteToggle.OnValueChanged += muted => BasisNetworkModeration.SetTextMute(targetUUID, muted);

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

                // ---- Force avatar ----
                // Built once with the page: this panel is rebuilt each time it is opened, so the
                // list is as fresh as the rest of what's on screen.
                var forceAvatarGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, adminGroup.ContentParent);
                forceAvatarGroup.SetTitle(BasisLocalization.Get("settings.admin.forceAvatar"));
                forceAvatarGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.forceAvatar.description"));

                List<ForceAvatarCatalog.Entry> avatarEntries = ForceAvatarCatalog.Build();

                PanelDropdown avatarDropdown = PanelDropdown.CreateNewEntry(forceAvatarGroup.ContentParent);
                avatarDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.forceAvatar.pick"));
                avatarDropdown.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.forceAvatar.pick.tooltip"));
                ForceAvatarCatalog.Apply(avatarDropdown, avatarEntries);

                PanelButton forceAvatarBtn = PanelButton.CreateNew(forceAvatarGroup.ContentParent);
                forceAvatarBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.forceAvatar"));
                forceAvatarBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.forceAvatar.description"));
                forceAvatarBtn.OnClicked += () =>
                {
                    if (!ForceAvatarCatalog.TryResolve(avatarEntries, avatarDropdown.Value, out ForceAvatarCatalog.Entry entry))
                    {
                        BasisDebug.LogError("No avatar selected.");
                        return;
                    }
                    if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np)) return;

                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("menu.individualPlayer.forceAvatar.dialog.title"),
                        BasisLocalization.Get("menu.individualPlayer.forceAvatar.dialog.body", remotePlayer.DisplayName, entry.Label),
                        BasisLocalization.Get("menu.individualPlayer.forceAvatar"),
                        BasisLocalization.Get("ui.cancel"),
                        confirmed => { if (confirmed) BasisNetworkModeration.ForceAvatar(np.playerId, entry.Item); },
                        category: BasisNotificationCategory.Player);
                };

                // ---- Locomotion override ----
                RectTransform adminContent = adminGroup.ContentParent;
                PanelSectionToggle locomotionSection = PanelSectionToggle.CreateNewEntry(adminContent);
                locomotionSection.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion"));
                int locomotionStart = adminContent.childCount;

                var locomotionGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, adminContent);
                locomotionGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.locomotion.description"));

                PanelToggle jumpToggle = PanelToggle.CreateNew(locomotionGroup.ContentParent);
                jumpToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.jumpHeight.override"));
                PanelSlider jumpSlider = PanelSlider.CreateNew(locomotionGroup.ContentParent);
                jumpSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("menu.individualPlayer.locomotion.jumpHeight"), 0.1f, 5f, false, 2, ValueDisplayMode.Raw));
                jumpSlider.SetValueWithoutNotify(1f);

                PanelToggle walkToggle = PanelToggle.CreateNew(locomotionGroup.ContentParent);
                walkToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.walkSpeed.override"));
                PanelSlider walkSlider = PanelSlider.CreateNew(locomotionGroup.ContentParent);
                walkSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("menu.individualPlayer.locomotion.walkSpeed"), 0.1f, 15f, false, 2, ValueDisplayMode.Raw));
                walkSlider.SetValueWithoutNotify(2.5f);

                PanelToggle runToggle = PanelToggle.CreateNew(locomotionGroup.ContentParent);
                runToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.runSpeed.override"));
                PanelSlider runSlider = PanelSlider.CreateNew(locomotionGroup.ContentParent);
                runSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("menu.individualPlayer.locomotion.runSpeed"), 0.1f, 20f, false, 2, ValueDisplayMode.Raw));
                runSlider.SetValueWithoutNotify(4f);

                List<string> modeEntries = SettingsProviderModeratorTab.BuildLocomotionModeEntries();
                PanelDropdown modeDropdown = PanelDropdown.CreateNewEntry(locomotionGroup.ContentParent);
                modeDropdown.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.mode"));
                modeDropdown.AssignEntries(modeEntries);
                modeDropdown.SetValueWithoutNotify(modeEntries[0]);

                void ApplyLocomotionSliderVisibility()
                {
                    jumpSlider.Descriptor.SetActive(jumpToggle.Value);
                    walkSlider.Descriptor.SetActive(walkToggle.Value);
                    runSlider.Descriptor.SetActive(runToggle.Value);
                }

                ApplyLocomotionSliderVisibility();
                jumpToggle.OnValueChanged += _ => { ApplyLocomotionSliderVisibility(); locomotionGroup.ForceRebuild(); };
                walkToggle.OnValueChanged += _ => { ApplyLocomotionSliderVisibility(); locomotionGroup.ForceRebuild(); };
                runToggle.OnValueChanged += _ => { ApplyLocomotionSliderVisibility(); locomotionGroup.ForceRebuild(); };

                PanelButton locomotionApplyBtn = PanelButton.CreateNew(locomotionGroup.ContentParent);
                locomotionApplyBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.apply"));
                locomotionApplyBtn.OnClicked += () =>
                {
                    if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np)) return;

                    BasisLocomotionValues values = SettingsProviderModeratorTab.ComposeLocomotionValues(
                        jumpToggle.Value, jumpSlider.Value,
                        walkToggle.Value, walkSlider.Value,
                        runToggle.Value, runSlider.Value,
                        modeEntries.IndexOf(modeDropdown.Value));

                    if (values.Fields == BasisLocomotionField.None)
                    {
                        BasisDebug.LogError("No locomotion fields selected to override.");
                        return;
                    }

                    BasisNetworkModeration.SetLocomotionOverride(np.playerId, values);
                };

                PanelButton locomotionClearBtn = PanelButton.CreateNew(locomotionGroup.ContentParent);
                locomotionClearBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.locomotion.clear"));
                locomotionClearBtn.OnClicked += () =>
                {
                    if (BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer np))
                    {
                        BasisNetworkModeration.ClearLocomotionOverride(np.playerId);
                    }
                };

                PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(locomotionSection, adminContent, locomotionStart, false,
                    visible =>
                    {
                        if (visible) ApplyLocomotionSliderVisibility();
                        locomotionGroup.ForceRebuild();
                    });

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
                    PermNodes.ModerationAnnounce,
                    PermNodes.ModerationForceAvatar,
                    PermNodes.ModerationLocomotion,
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

                AddPage(adminTabKey, adminPage);
            }

            // ================= Debug =================
            const string debugTabKey = "menu.individualPlayer.debug";
            PanelTabPage debugPage = NewPage(debugTabKey, AddressableAssets.Sprites.Settings);
            root = debugPage.Descriptor.ContentParent;

            var debugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            debugGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.debug"));
            debugGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.debug.description"));

            var debugField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, debugGroup.ContentParent);
            debugField.SetTitle(BasisLocalization.Get("menu.individualPlayer.transmission"));
            debugField.SetDescription(BasisLocalization.Get("menu.individualPlayer.waitingForData"));

            debugGroup.IsolateAsCanvas();

            // ---- Live network state (moved off the Network tab; all of it is polled) ----
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

            networkGroup.IsolateAsCanvas();

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
            updater.PanelDescriptor = panel.Descriptor;
            updater.DebugField = debugField;
            updater.DistanceField = distanceField;
            updater.LodField = lodField;
            updater.RangesField = rangesField;
            updater.BufferField = bufferField;
            updater.DirectConnPingField = directConnPingField;
            updater.DirectConnPingTint = directConnPingTint;
            updater.DirectConnRebuildFrom = p2pGroup.ContentParent;
            updater.DirectConnRebuildStopAt = networkPage.Descriptor.ContentParent;
            updater.AvatarStatusField = avatarStatusField;
            updater.AvatarStatusTint = avatarStatusTint;

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
                // Destroy existing fields. Destroy is deferred to end of frame, so deactivate
                // first — otherwise the rebuild below still measures the doomed rows.
                if (audioSourceField != null) { audioSourceField.SetActive(false); UnityEngine.Object.Destroy(audioSourceField.gameObject); audioSourceField = null; }
                if (volumeChainField != null) { volumeChainField.SetActive(false); UnityEngine.Object.Destroy(volumeChainField.gameObject); volumeChainField = null; }
                if (decodedBufferField != null) { decodedBufferField.SetActive(false); UnityEngine.Object.Destroy(decodedBufferField.gameObject); decodedBufferField = null; }
                if (encodedBufferField != null) { encodedBufferField.SetActive(false); UnityEngine.Object.Destroy(encodedBufferField.gameObject); encodedBufferField = null; }
                if (silenceField != null) { silenceField.SetActive(false); UnityEngine.Object.Destroy(silenceField.gameObject); silenceField = null; }
                if (visemeField != null) { visemeField.SetActive(false); UnityEngine.Object.Destroy(visemeField.gameObject); visemeField = null; }

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

                RebuildSection(audioDebugGroup, debugPage);
            };

            // ---- Avatar Data Debug Section ----
            var avatarDataDebugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            avatarDataDebugGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug"));
            avatarDataDebugGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.description"));

            PanelToggle avatarDataDebugToggle = PanelToggle.CreateNewEntry(avatarDataDebugGroup.ContentParent);
            avatarDataDebugToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.showAvatarDataDebug"));
            avatarDataDebugToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.showAvatarDataDebug.description"));
            avatarDataDebugToggle.AssignBinding(BasisSettingsDefaults.AvatarDataDebugEnabled);

            PanelElementDescriptor avatarReceiveField = null;
            PanelElementDescriptor avatarStagingField = null;
            PanelElementDescriptor avatarInterpField = null;
            PanelElementDescriptor avatarMetaField = null;

            void CreateAvatarDataDebugFields()
            {
                if (BasisSettingsDefaults.AvatarDataDebugShowReceive.RawValue)
                {
                    avatarReceiveField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarDataDebugGroup.ContentParent);
                    avatarReceiveField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.receive"));
                    avatarReceiveField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AvatarDataDebugShowStaging.RawValue)
                {
                    avatarStagingField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarDataDebugGroup.ContentParent);
                    avatarStagingField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.staging"));
                    avatarStagingField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AvatarDataDebugShowInterp.RawValue)
                {
                    avatarInterpField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarDataDebugGroup.ContentParent);
                    avatarInterpField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.interp"));
                    avatarInterpField.SetDescription("...");
                }

                if (BasisSettingsDefaults.AvatarDataDebugShowMeta.RawValue)
                {
                    avatarMetaField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, avatarDataDebugGroup.ContentParent);
                    avatarMetaField.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.meta"));
                    avatarMetaField.SetDescription("...");
                }
            }

            if (BasisSettingsDefaults.AvatarDataDebugEnabled.RawValue)
            {
                CreateAvatarDataDebugFields();
            }

            updater.AvatarReceiveField = avatarReceiveField;
            updater.AvatarStagingField = avatarStagingField;
            updater.AvatarInterpField = avatarInterpField;
            updater.AvatarMetaField = avatarMetaField;

            avatarDataDebugToggle.OnValueChanged += enabled =>
            {
                if (avatarReceiveField != null) { avatarReceiveField.SetActive(false); UnityEngine.Object.Destroy(avatarReceiveField.gameObject); avatarReceiveField = null; }
                if (avatarStagingField != null) { avatarStagingField.SetActive(false); UnityEngine.Object.Destroy(avatarStagingField.gameObject); avatarStagingField = null; }
                if (avatarInterpField != null) { avatarInterpField.SetActive(false); UnityEngine.Object.Destroy(avatarInterpField.gameObject); avatarInterpField = null; }
                if (avatarMetaField != null) { avatarMetaField.SetActive(false); UnityEngine.Object.Destroy(avatarMetaField.gameObject); avatarMetaField = null; }

                if (enabled)
                {
                    CreateAvatarDataDebugFields();
                }

                updater.AvatarReceiveField = avatarReceiveField;
                updater.AvatarStagingField = avatarStagingField;
                updater.AvatarInterpField = avatarInterpField;
                updater.AvatarMetaField = avatarMetaField;

                RebuildSection(avatarDataDebugGroup, debugPage);
            };

            AddPage(debugTabKey, debugPage);

            // Announce is a server round trip, so the button cannot repaint from its own click —
            // the local driver still reports the old state at that point. Wait for the change
            // to come back instead, and drop the subscription once the panel is gone.
            Action<ushort, bool> announceHandler = null;
            announceHandler = (changedId, _) =>
            {
                if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer announceTarget)
                    || changedId != announceTarget.playerId)
                {
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    if (panel == null || panel.Descriptor == null)
                    {
                        BasisNetworkModeration.OnAnnounceModeChanged -= announceHandler;
                        return;
                    }
                    sync.Announce?.Invoke();
                });
            };
            BasisNetworkModeration.OnAnnounceModeChanged += announceHandler;

            Action<ushort, bool> shoutHandler = null;
            shoutHandler = (changedId, _) =>
            {
                if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer shoutTarget)
                    || changedId != shoutTarget.playerId)
                {
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    if (panel == null || panel.Descriptor == null)
                    {
                        BasisNetworkModeration.OnShoutModeChanged -= shoutHandler;
                        return;
                    }
                    sync.Shout?.Invoke();
                });
            };
            BasisNetworkModeration.OnShoutModeChanged += shoutHandler;

            panel.Descriptor.ForceRebuild();
            panel.Descriptor.ForceRebuild();
        }
    }
}
