using System;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

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
            if (titleLabel != null) titleLabel.text = "Player Settings";

            var root = tab.Descriptor.ContentParent;
            var infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            infoGroup.SetTitle("Player");
            infoGroup.SetDescription("Per-player overrides (volume, avatar visibility, interactions).");

            var Descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group,infoGroup.ContentParent);
            Descriptor.SetTitle("Name");
            Descriptor.SetDescription(remotePlayer.DisplayName);

            var uuidField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, infoGroup.ContentParent);
            uuidField.SetTitle("UUID");
            uuidField.SetDescription(remotePlayer.UUID);
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
            var debugGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            debugGroup.SetTitle("Debug");
            debugGroup.SetDescription("Live diagnostics for voice/range checks (optional).");

            var debugField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, debugGroup.ContentParent);
            debugField.SetTitle("Transmission");
            debugField.SetDescription("Waiting for data...");

            var updater = panel.gameObject.AddComponent<IndividualPlayerPanelUpdater>();
            updater.RemotePlayer = remotePlayer;
            updater.DebugField = debugField;

            panel.Descriptor.ForceRebuild();
            panel.Descriptor.ForceRebuild();
        }
    }
}
