using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Task = System.Threading.Tasks.Task;

namespace Basis.BasisUI
{
    [Serializable]
    public struct PlatformBadge
    {
        public string platformRegex;
        public string platformDisplayName;
        public Sprite platformIcon;
    }

    public struct UserButtonAction
    {
        public string title;
        public Action action;
        public string buttonStyle;
    }

    public struct PlayerListFilter
    {
        public string filterName;
        public Func<BasisNetworkPlayer, bool> filterFunction;
    }

    public struct PlayerBadge
    {
        public string badgeName;
        public Sprite badgeIcon;
    }

    public class PanelPlayerList : PanelSelectionGroup
    {
        public static class PlayerListStyles
        {
            public static string Default = "Packages/com.basis.sdk/Prefabs/Panel Elements/Player List Prefab.prefab";
        }

        public static PanelPlayerList CreateNew(Component parent)
            => CreateNew<PanelPlayerList>(PlayerListStyles.Default, parent);

        public PlatformBadge[] PlatformBadges = new PlatformBadge[]
        {
            new() { platformRegex = "Windows", platformDisplayName = "PC", platformIcon = null },
            new() { platformRegex = "iOS|iPhone|iPad", platformDisplayName = "iOS", platformIcon = null },
            new() { platformRegex = "Android", platformDisplayName = "Android", platformIcon = null },
            new() { platformRegex = "Macintosh|Mac OS X", platformDisplayName = "Mac", platformIcon = null },
            new() { platformRegex = "Linux", platformDisplayName = "Linux", platformIcon = null },
        };

        public RectTransform UserActionButtonParent;
        public PanelSlider UserVolumeSlider;
        public TMP_Text TitleText;
        public GameObject BadgeTemplate;
        public Button IndexButtonTemplate;
        public ScrollRect playerScrollRect;

        // --- Pools (instead of Destroy/Instantiate every refresh)
        private readonly List<Button> _indexButtonsActive = new();
        private readonly Stack<Button> _indexButtonsPool = new();

        private readonly List<GameObject> _badgeActive = new();
        private readonly Stack<GameObject> _badgePool = new();

        private readonly List<PanelButton> _actionButtonsActive = new();
        private readonly Stack<PanelButton> _actionButtonsPool = new();

        private readonly Stack<PanelButton> _playerButtonsPool = new(); // SelectionButtons are active list

        private BasisPlayerSettingsData _currentPlayerSettings;
        private PlayerListFilter? _activeFilter = null;
        // --- Regex cache (compile once)
        private struct CompiledPlatformBadge
        {
            public Regex regex;
            public string displayName;
            public Sprite icon;
        }
        private CompiledPlatformBadge[] _compiledPlatformBadges;

        // --- Sorting buffer (reused)
        private BasisNetworkReceiver[] _sortedBuffer = Array.Empty<BasisNetworkReceiver>();

        // --- Reused index char output
        private readonly List<string> _indexChars = new(64);

        public override void OnCreateEvent()
        {
            BadgeTemplate.SetActive(false);
            IndexButtonTemplate.gameObject.SetActive(false);

            base.OnCreateEvent();

            CompilePlatformBadges();

            ShowPlayer(null).Forget(); // extension below, avoids warning
            UserVolumeSlider.OnValueChanged += OnVolumeSliderChanged;

            UpdateUI();
        }
        public override void OnReleaseEvent()
        {
            // prevent dangling handlers
            if (UserVolumeSlider != null)
            {
                UserVolumeSlider.OnValueChanged -= OnVolumeSliderChanged;
            }

            // Also remove click listeners from pooled index buttons to avoid growth
            foreach (var b in _indexButtonsActive)
            {
                b.onClick.RemoveAllListeners();
            }

            while (_indexButtonsPool.Count > 0)
            {
                _indexButtonsPool.Pop().onClick.RemoveAllListeners();
            }
        }

        private void CompilePlatformBadges()
        {
            _compiledPlatformBadges = new CompiledPlatformBadge[PlatformBadges.Length];
            for (int i = 0; i < PlatformBadges.Length; i++)
            {
                var pb = PlatformBadges[i];
                _compiledPlatformBadges[i] = new CompiledPlatformBadge
                {
                    regex = new Regex(pb.platformRegex, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                    displayName = pb.platformDisplayName,
                    icon = pb.platformIcon
                };
            }
        }

        public void UpdateUI()
        {
            var indexChars = UpdatePlayerListAndCollectIndexChars();
            SetIndexCharacters(indexChars);
        }

        // ---------- Index Buttons (pooled)
        private void SetIndexCharacters(List<string> indexChars)
        {
            // Return active to pool
            for (int i = 0; i < _indexButtonsActive.Count; i++)
            {
                var b = _indexButtonsActive[i];
                b.onClick.RemoveAllListeners();
                b.gameObject.SetActive(false);
                _indexButtonsPool.Push(b);
            }
            _indexButtonsActive.Clear();

            Transform parent = IndexButtonTemplate.transform.parent;

            // Create / activate deterministically
            for (int i = 0; i < indexChars.Count; i++)
            {
                string indexChar = indexChars[i];

                Button indexButton = GetIndexButton();
                indexButton.gameObject.SetActive(true);

                // 🔒 Hard reset hierarchy state (this is the magic)
                Transform t = indexButton.transform;
                if (t.parent != parent)
                {
                    t.SetParent(parent, false);
                }

                // Force visual order
                t.SetSiblingIndex(i);

                var tmp = indexButton.GetComponentInChildren<TMP_Text>();
                if (tmp != null)
                {
                    tmp.text = indexChar;
                }

                string capturedChar = indexChar;

                indexButton.onClick.AddListener(() =>
                {
                    string filterName = "Showing players starting with " + capturedChar;

                    if (_activeFilter.HasValue && _activeFilter.Value.filterName == filterName)
                    {
                        _activeFilter = null;
                        UpdateUI();
                        return;
                    }

                    _activeFilter = new PlayerListFilter
                    {
                        filterName = filterName,
                        filterFunction = (player) =>
                        {
                            string name = player.Player.SafeDisplayName;
                            return name.StartsWith(capturedChar, StringComparison.OrdinalIgnoreCase);
                        }
                    };

                    UpdateUI();
                });

                _indexButtonsActive.Add(indexButton);
            }
        }


        private Button GetIndexButton()
        {
            if (_indexButtonsPool.Count > 0) return _indexButtonsPool.Pop();
            return Instantiate(IndexButtonTemplate, IndexButtonTemplate.transform.parent);
        }

        // ---------- Player Buttons (pooled)
        private void ClearPlayerButtons()
        {
            // return active selection buttons to pool
            for (int i = 0; i < SelectionButtons.Count; i++)
            {
                var b = SelectionButtons[i];
                // IMPORTANT: remove listeners to prevent capturing old players
                b.OnClicked = null;
                b.gameObject.SetActive(false);
                _playerButtonsPool.Push(b);
            }
            SelectionButtons.Clear();
        }

        private PanelButton GetPlayerButton()
        {
            if (_playerButtonsPool.Count > 0) return _playerButtonsPool.Pop();
            return PanelButton.CreateNew(PanelButton.ButtonStyles.Avatar, TabButtonParent);
        }

        // ---------- Action Buttons (pooled)
        private void ClearActionButtons()
        {
            for (int i = 0; i < _actionButtonsActive.Count; i++)
            {
                var b = _actionButtonsActive[i];
                b.OnClicked = null;
                b.gameObject.SetActive(false);
                _actionButtonsPool.Push(b);
            }
            _actionButtonsActive.Clear();
        }

        private PanelButton GetActionButton()
        {
            if (_actionButtonsPool.Count > 0) return _actionButtonsPool.Pop();
            return PanelButton.CreateNew(PanelButton.ButtonStyles.Default, UserActionButtonParent);
        }

        // ---------- Badges (pooled)
        private void ClearBadges()
        {
            for (int i = 0; i < _badgeActive.Count; i++)
            {
                var go = _badgeActive[i];
                go.SetActive(false);
                _badgePool.Push(go);
            }
            _badgeActive.Clear();
        }

        private GameObject GetBadgeGO()
        {
            return _badgePool.Count > 0 ? _badgePool.Pop() : Instantiate(BadgeTemplate, BadgeTemplate.transform.parent);
        }

        // ---------- Settings/Volume
        private void OnVolumeSliderChanged(float value)
        {
            if (_currentPlayerSettings == null)
            {
                return;
            }

            _currentPlayerSettings.VolumeLevel = value;
            _ = BasisPlayerSettingsManager.SetPlayerSettings(_currentPlayerSettings);
        }
        // ---------- Platform badge (compiled regex)
        private PlayerBadge GetPlatformPlayerBadge(string platform)
        {
            for (int i = 0; i < _compiledPlatformBadges.Length; i++)
            {
                if (_compiledPlatformBadges[i].regex.IsMatch(platform))
                {
                    return new PlayerBadge
                    {
                        badgeName = _compiledPlatformBadges[i].displayName,
                        badgeIcon = _compiledPlatformBadges[i].icon
                    };
                }
            }
            return new PlayerBadge
            {
                badgeName = "*" + platform,
                badgeIcon = null
            };
        }

        private async Task<PlayerBadge[]> GetPlayerBadges(BasisNetworkPlayer player)
        {
            // small, predictable size; allocate once
            var badges = new List<PlayerBadge>(0);
          //lD Finish this!  string platform = (player.Player as BasisRemotePlayer)?.GetRuntimePlatform().ToString() ?? "Unknown";
          //  badges.Add(GetPlatformPlayerBadge(platform));

            return badges.ToArray();
        }

        // ---------- Show Player
        public async Task ShowPlayer(BasisNetworkPlayer player)
        {
            bool showRemoteControls = player != null;
            if (UserVolumeSlider != null) UserVolumeSlider.gameObject.SetActive(showRemoteControls);

            Descriptor.SetTitle(player?.Player.SafeDisplayName);
            Descriptor.SetDescription(player?.Player.UUID);

            var settings = await LoadPlayerSettings(player);
            CreateActionButtonsForPlayer(player, settings);

            var badges = await GetPlayerBadges(player);
            CreateBadges(badges);

            Descriptor.ForceRebuild();
        }

        private async Task<BasisPlayerSettingsData> LoadPlayerSettings(BasisNetworkPlayer player)
        {
            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(player?.Player.UUID);
            _currentPlayerSettings = settings;
            UserVolumeSlider.SetValueWithoutNotify(settings.VolumeLevel);
            return settings;
        }

        private void CreateBadges(PlayerBadge[] badges)
        {
            ClearBadges();
            for (int i = 0; i < badges.Length; i++)
            {
                var badge = badges[i];
                var go = GetBadgeGO();
                go.SetActive(true);
                _badgeActive.Add(go);

                var text = go.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    text.text = badge.badgeName;
                }

                var img = go.transform.Find("Icon")?.GetComponent<Image>();
                if (img != null)
                {
                    if (badge.badgeIcon != null)
                    {
                        img.enabled = true;
                        img.sprite = badge.badgeIcon;
                    }
                    else
                    {
                        // avoid showing stale sprites from pooled objects
                        img.enabled = false;
                        img.sprite = null;
                    }
                }
            }
        }

        // ---------- Actions
        private Action WrapAction(BasisNetworkPlayer player, BasisPlayerSettingsData settings, Action action)
        {
            return () =>
            {
                action();
                CreateActionButtonsForPlayer(player, settings);
            };
        }

        private UserButtonAction[] GetActionsForPlayer(BasisNetworkPlayer player, BasisPlayerSettingsData settings)
        {
            if (player == null) return Array.Empty<UserButtonAction>();

            // fixed max count here keeps list from resizing
            var actions = new List<UserButtonAction>(6);

            if (settings.AvatarVisible)
            {
                actions.Add(new UserButtonAction
                {
                    title = "Avatar Shown",
                    action = WrapAction(player, settings, () =>
                    {
                        settings.AvatarVisible = false;
                        _ = BasisPlayerSettingsManager.SetPlayerSettings(settings);
                        (player.Player as BasisRemotePlayer)?.ReloadAvatar();
                    }),
                    buttonStyle = "Button Success"
                });
            }
            else
            {
                actions.Add(new UserButtonAction
                {
                    title = "Avatar Hidden",
                    action = WrapAction(player, settings, () =>
                    {
                        settings.AvatarVisible = true;
                        _ = BasisPlayerSettingsManager.SetPlayerSettings(settings);
                        (player.Player as BasisRemotePlayer)?.ReloadAvatar();
                    }),
                    buttonStyle = "Button Danger"
                });
            }

            if (settings.AvatarInteraction)
            {
                actions.Add(new UserButtonAction
                {
                    title = "Interactions Enabled",
                    action = WrapAction(player, settings, () =>
                    {
                        settings.AvatarInteraction = false;
                        _ = BasisPlayerSettingsManager.SetPlayerSettings(settings);
                        (player.Player as BasisRemotePlayer)?.ReloadAvatar();
                    }),
                    buttonStyle = "Button Success"
                });
            }
            else
            {
                actions.Add(new UserButtonAction
                {
                    title = "Interactions Disabled",
                    action = WrapAction(player, settings, () =>
                    {
                        settings.AvatarInteraction = true;
                        _ = BasisPlayerSettingsManager.SetPlayerSettings(settings);
                        (player.Player as BasisRemotePlayer)?.ReloadAvatar();
                    }),
                    buttonStyle = "Button Danger"
                });
            }

            actions.Add(new UserButtonAction
            {
                title = "Kick",
                action = WrapAction(player, settings, () =>
                {
                    BasisNetworkModeration.SendKick(player.Player.UUID, "Kicked by " + BasisLocalPlayer.Instance.UUID);
                }),
                buttonStyle = "Button Caution"
            });

            actions.Add(new UserButtonAction
            {
                title = "Teleport All To Player",
                action = WrapAction(player, settings, () =>
                {
                    BasisNetworkModeration.TeleportAll(player.playerId);
                }),
                buttonStyle = "Button Caution"
            });

            actions.Add(new UserButtonAction
            {
                title = "Teleport To Player",
                action = WrapAction(player, settings, () =>
                {
                    BasisNetworkModeration.TryTeleportToPlayer(player.playerId);
                }),
                buttonStyle = "Button Default"
            });

            return actions.ToArray();
        }

        private void CreateActionButtonsForPlayer(BasisNetworkPlayer player, BasisPlayerSettingsData settings)
        {
            ClearActionButtons();
            if (player == null) return;

            var actions = GetActionsForPlayer(player, settings);

            for (int i = 0; i < actions.Length; i++)
            {
                var a = actions[i];

                PanelButton button = GetActionButton();
                button.gameObject.SetActive(true);

                // 🔒 Hard reset hierarchy state (preserve visual order)
                Transform t = button.transform;
                if (t.parent != UserActionButtonParent)
                {
                    t.SetParent(UserActionButtonParent, false);
                }
                t.SetSiblingIndex(i);

                button.Descriptor.SetTitle(a.title);

                if (!string.IsNullOrEmpty(a.buttonStyle))
                {
                    button.ButtonStyling.SetStyle(a.buttonStyle);
                }

                button.OnClicked = () => a.action();

                _actionButtonsActive.Add(button);
            }
        }
        private bool PlayerPassesFilter(BasisNetworkPlayer player) => !_activeFilter.HasValue || _activeFilter.Value.filterFunction(player);
        private static readonly Comparison<BasisNetworkReceiver> ReceiverNameComparison = (a, b) => string.Compare(a.Player.SafeDisplayName, b.Player.SafeDisplayName, StringComparison.OrdinalIgnoreCase);

        private List<string> UpdatePlayerListAndCollectIndexChars()
        {
            ClearPlayerButtons();
            _indexChars.Clear();

            // Collect unique first letters fast.
            // bool[26] avoids HashSet allocs and is extremely cheap.
            Span<bool> seen = stackalloc bool[26];

            BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            int count = snapshot?.Length ?? 0;
            if (count == 0) return _indexChars;

            EnsureBufferSize(count);

            Array.Copy(snapshot, _sortedBuffer, count);
            Array.Sort(_sortedBuffer, 0, count, Comparer<BasisNetworkReceiver>.Create(ReceiverNameComparison));

            for (int i = 0; i < count; i++)
            {
                var receiver = _sortedBuffer[i];
                if (receiver == null) continue;

                string name = receiver.Player.SafeDisplayName;
                if (!string.IsNullOrEmpty(name))
                {
                    char c = char.ToUpperInvariant(name[0]);
                    if (c >= 'A' && c <= 'Z')
                    {
                        int idx = c - 'A';
                        if (!seen[idx])
                        {
                            seen[idx] = true;
                            _indexChars.Add(c.ToString());
                        }
                    }
                    else
                    {
                        // Non A-Z bucket (optional)
                        // You can choose to add "#" here if you want.
                    }
                }

                if (!PlayerPassesFilter(receiver)) continue;
                CreatePlayerButton(receiver, SelectionButtons.Count);
            }

            _indexChars.Sort(StringComparer.Ordinal);
            return _indexChars;
        }

        private void EnsureBufferSize(int count)
        {
            if (_sortedBuffer.Length >= count) return;
            // grow exponentially to reduce realloc frequency
            int newSize = Math.Max(count, _sortedBuffer.Length == 0 ? 64 : _sortedBuffer.Length * 2);
            _sortedBuffer = new BasisNetworkReceiver[newSize];
        }

        private void CreatePlayerButton(BasisNetworkPlayer player, int visualIndex)
        {
            PanelButton button = GetPlayerButton();
            button.gameObject.SetActive(true);

            // Ensure it is under the correct parent and placed deterministically.
            var t = button.transform;
            if (t.parent != TabButtonParent)
            {
                t.SetParent(TabButtonParent, false);
            }

            t.SetSiblingIndex(visualIndex);

            button.Descriptor.SetTitle(player.Player.SafeDisplayName);

            // Assign (don't +=) so pooled buttons don't accumulate handlers.
            button.OnClicked = async () => await ShowPlayer(player);

            SelectionButtons.Add(button);
        }
    }

    internal static class TaskExtensions
    {
        public static void Forget(this Task task) { /* intentionally empty */ }
    }
}
