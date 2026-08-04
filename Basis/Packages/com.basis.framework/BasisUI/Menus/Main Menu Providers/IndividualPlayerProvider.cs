using System;
using System.Collections.Generic;
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
using P2PState = Basis.Scripts.Networking.BasisP2PManager.P2PSessionState;

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
        private const float UnmuteFallbackVolume = 1.0f;

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
                    confirmed => tcs.SetResult(confirmed));

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

            // ================= Player =================
            const string playerTabKey = "menu.individualPlayer.player";
            PanelTabPage playerPage = NewPage(playerTabKey, AddressableAssets.Sprites.Information);
            RectTransform root = playerPage.Descriptor.ContentParent;

            var infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            infoGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.player"));
            infoGroup.SetDescription(BasisLocalization.Get("menu.individualPlayer.player.description"));

            var Descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group,infoGroup.ContentParent);
            Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.name"));
            Descriptor.SetDescription(remotePlayer.DisplayName);

            var PlatformDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, infoGroup.ContentParent);
            PlatformDescriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.platform"));
            PlatformDescriptor.SetDescription(remotePlayer.PlayerPlatform);

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

            var uuidField = PanelTextField.CreateNewEntry(root);
            uuidField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.uuid"));
            uuidField.SetValueWithoutNotify(remotePlayer.UUID);
            uuidField._inputField.readOnly = true;

            AddPage(playerTabKey, playerPage);

            // ================= Audio =================
            const string audioTabKey = "settings.tab.audio";
            PanelTabPage audioPage = NewPage(audioTabKey, AddressableAssets.Sprites.Microphone);
            root = audioPage.Descriptor.ContentParent;

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
            muteBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.VolumeLevel <= 0f ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
            muteBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.mute.description"));

            muteBtn.OnClicked += async () =>
            {
                bool nowMuted = await ToggleMute(remotePlayer);
                muteBtn.Descriptor.SetTitle(BasisLocalization.Get(nowMuted ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));

                // Pull the resolved volume back so the slider mirrors the helper's restore-from-snapshot logic.
                var refreshed = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                volumeSlider.SetValueWithoutNotify(refreshed.VolumeLevel);
                RefreshBoostTint(refreshed.VolumeLevel, true);
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
                recordPolicy.AssignEntries(recordPolicyOptions);
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
            privateChatBtn.Descriptor.SetTitle(BasisLocalization.Get(
                hasPrivateChatTarget && Basis.Scripts.Networking.BasisTalkModeManager.IsPrivateMember(privateChatPlayerId)
                    ? "menu.individualPlayer.privateChat.remove"
                    : "menu.individualPlayer.privateChat.add"));
            privateChatBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.privateChat.add.description"));
            privateChatBtn.OnClicked += () =>
            {
                if (!hasPrivateChatTarget) return;
                bool nowMember = Basis.Scripts.Networking.BasisTalkModeManager.TogglePrivateMember(privateChatPlayerId);
                privateChatBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    nowMember ? "menu.individualPlayer.privateChat.remove" : "menu.individualPlayer.privateChat.add"));
            };

            PanelButton talkToOnlyBtn = PanelButton.CreateNew(privateChatGroup.ContentParent);
            talkToOnlyBtn.Descriptor.SetTitle(BasisLocalization.Get(
                hasPrivateChatTarget && Basis.Scripts.Networking.BasisTalkModeManager.IsTalkingOnlyTo(privateChatPlayerId)
                    ? "menu.individualPlayer.talkToOnly.stop"
                    : "menu.individualPlayer.talkToOnly"));
            talkToOnlyBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.talkToOnly.description"));
            talkToOnlyBtn.OnClicked += () =>
            {
                if (!hasPrivateChatTarget) return;
                if (Basis.Scripts.Networking.BasisTalkModeManager.IsTalkingOnlyTo(privateChatPlayerId))
                    Basis.Scripts.Networking.BasisTalkModeManager.StopThisPerson();
                else
                    Basis.Scripts.Networking.BasisTalkModeManager.SetThisPersonTarget(privateChatPlayerId);
                talkToOnlyBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    Basis.Scripts.Networking.BasisTalkModeManager.IsTalkingOnlyTo(privateChatPlayerId)
                        ? "menu.individualPlayer.talkToOnly.stop"
                        : "menu.individualPlayer.talkToOnly"));
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

            bool DirectConnBlocked(P2PState st)
            {
                // A lock applied after the fact doesn't tear down a session that is already up,
                // so a live link still reports itself and can still be cancelled.
                if (st == P2PState.Connected || st == P2PState.Reconnecting || st == P2PState.PartialConnection)
                {
                    return false;
                }
                return BasisSettingsDefaults.DisableDirectConnections.RawValue ||
                    (BasisNetworkModeration.GlobalDirectConnectLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass());
            }

            // What clicking the button does from here — the state itself is on the status card.
            string DirectConnActionKey(P2PState st)
            {
                if (DirectConnBlocked(st))
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
                if (DirectConnBlocked(st))
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

            directConnBtn.OnClicked += () =>
            {
                if (!hasDirectConnTarget) return;
                P2PState st = Basis.Scripts.Networking.BasisP2PManager.GetSessionState(directConnPlayerId);
                if (st == P2PState.Idle || st == P2PState.Failed)
                {
                    if (BasisNetworkModeration.GlobalDirectConnectLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
                    {
                        BasisMainMenu.Instance.OpenDialogue(
                            BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.title"),
                            BasisLocalization.Get("menu.individualPlayer.directConnection.serverLockedDialog.body"),
                            BasisLocalization.Get("ui.ok"),
                            _ => { },
                            severity: BasisPanelSeverity.Caution);
                        return;
                    }

                    if (BasisSettingsDefaults.DisableDirectConnections.RawValue)
                    {
                        BasisMainMenu.Instance.OpenDialogue(
                            BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.title"),
                            BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.body"),
                            BasisLocalization.Get("ui.ok"),
                            _ => { },
                            severity: BasisPanelSeverity.Caution);
                        return;
                    }

                    // Hot: confirming this hands both sides each other's IP address.
                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("menu.individualPlayer.directConnection.outgoingDialog.title"),
                        BasisLocalization.Get("menu.individualPlayer.directConnection.outgoingDialog.body", remotePlayer.DisplayName, remotePlayer.UUID),
                        BasisLocalization.Get("menu.individualPlayer.directConnection.request"),
                        BasisLocalization.Get("ui.cancel"),
                        confirmed =>
                        {
                            if (!confirmed) return;
                            Basis.Scripts.Networking.BasisP2PManager.SendRequest(directConnPlayerId);
                        },
                        severity: BasisPanelSeverity.Hot);
                }
                else
                {
                    Basis.Scripts.Networking.BasisP2PManager.CancelSession(directConnPlayerId);
                }
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
                    RefreshDirectConnLabel();
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
                directConnPolicy.AssignEntries(policyOptions);
                directConnPolicy.SetValueWithoutNotify(policyOptions[(int)BasisTrustedConnections.GetPolicy(remotePlayer.UUID)]);
                directConnPolicy.OnValueChanged = selected =>
                {
                    int idx = policyOptions.IndexOf(selected);
                    if (idx < 0) idx = 0;
                    BasisTrustedConnections.SetPolicy(remotePlayer.UUID, (BasisDirectConnectionPolicy)idx);
                };
            }

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

            networkGroup.IsolateAsCanvas();

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
            if (BasisHeightDriver.TryGetMatchedEyeHeightOverrideMeters(remotePlayer, out float initialRemoteEyeHeight))
            {
                matchEyeHeightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.description", initialRemoteEyeHeight));
            }
            else
            {
                matchEyeHeightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.unavailable"));
            }

            matchEyeHeightBtn.OnClicked += () =>
            {
                if (!BasisHeightDriver.TryGetMatchedEyeHeightOverrideMeters(remotePlayer, out float remoteEyeHeight))
                {
                    BasisDebug.LogWarning("Cannot match eye height because the selected remote avatar eye height is unavailable.", BasisDebug.LogTag.Avatar);
                    return;
                }

                if (!SMModuleCalibration.ApplyCustomScale)
                {
                    BasisSettingsDefaults.CustomScale.SetValue(true);
                    SMModuleCalibration.ApplyCustomScale = true;
                }

                BasisHeightDriver.ApplyRuntimeOscEyeHeightOverride(remoteEyeHeight);
                matchEyeHeightBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.description", remoteEyeHeight));
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
                    remotePlayer.OnAvatarFailedStateChanged?.Invoke();
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
                    remotePlayer.AvatarInteractionAllowed = s.AvatarInteraction;
                    if (!s.AvatarInteraction && BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer interactionsNet))
                    {
                        Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.RevokePlayer(interactionsNet.playerId);
                    }
                    remotePlayer.ReloadAvatar();
                }
            };

            PanelToggle jiggleGrabToggle = PanelToggle.CreateNewEntry(avatarGroup.ContentParent);
            jiggleGrabToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.jiggleGrab"));
            jiggleGrabToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.jiggleGrab.description"));
            jiggleGrabToggle.SetValueWithoutNotify(settings.JiggleGrabAllowed);
            jiggleGrabToggle.OnValueChanged += async on =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.JiggleGrabAllowed = on;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null)
                {
                    remotePlayer.JiggleGrabAllowed = on;
                    if (!on && BasisNetworkPlayers.PlayerToNetworkedPlayer(remotePlayer, out BasisNetworkPlayer jiggleGrabNet))
                    {
                        Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.RevokePlayer(jiggleGrabNet.playerId);
                    }
                }
            };

            PanelToggle alwaysShowAvatarToggle = PanelToggle.CreateNewEntry(avatarGroup.ContentParent);
            alwaysShowAvatarToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.alwaysShowAvatar"));
            alwaysShowAvatarToggle.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.alwaysShowAvatar.description"));
            alwaysShowAvatarToggle.SetValueWithoutNotify(settings.AlwaysShowAvatar);
            alwaysShowAvatarToggle.OnValueChanged += async on =>
            {
                var s = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                s.AlwaysShowAvatar = on;
                await BasisPlayerSettingsManager.SetPlayerSettings(s);

                if (remotePlayer != null)
                {
                    remotePlayer.AlwaysShowAvatar = on;
                    remotePlayer.ReloadAvatar();
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
            toggleBlockBtn.Descriptor.SetTitle(BasisLocalization.Get(settings.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
            toggleBlockBtn.Descriptor.SetDescription(BasisLocalization.Get("menu.individualPlayer.block.button.description"));

            toggleBlockBtn.OnClicked += async () =>
            {
                bool nowBlocked = await ToggleBlockWithConfirmation(remotePlayer);
                toggleBlockBtn.Descriptor.SetTitle(BasisLocalization.Get(nowBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
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

            panel.Descriptor.ForceRebuild();
            panel.Descriptor.ForceRebuild();
        }
    }
}
