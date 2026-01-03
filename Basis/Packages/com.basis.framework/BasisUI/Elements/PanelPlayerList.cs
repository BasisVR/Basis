using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;
using TMPro;
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
            new()
            {
                platformRegex = "Windows",
                platformDisplayName = "PC",
                platformIcon = null
            },
            new()
            {
                platformRegex = "iOS|iPhone|iPad",
                platformDisplayName = "iOS",
                platformIcon = null
            },
            new()
            {
                platformRegex = "Android",
                platformDisplayName = "Android",
                platformIcon = null
            },
            new()
            {
                platformRegex = "Macintosh|Mac OS X",
                platformDisplayName = "Mac",
                platformIcon = null
            },
            new()
            {
                platformRegex = "Linux",
                platformDisplayName = "Linux",
                platformIcon = null
            },

        };

        public RectTransform UserActionButtonParent;
        public PanelSlider UserVolumeSlider;
        public TMP_Text TitleText;
        public GameObject BadgeTemplate;
        public Button IndexButtonTemplate;
        public ScrollRect playerScrollRect;

        private List<Button> IndexButtons = new();
        private List<GameObject> BadgeObjects = new();
        private List<PanelButton> ActionButtons = new();
        private BasisPlayerSettingsData _currentPlayerSettings;

        private PlayerListFilter? _activeFilter = null;

        public override void OnCreateEvent()
        {
            BadgeTemplate.SetActive(false);
            IndexButtonTemplate.gameObject.SetActive(false);
            base.OnCreateEvent();
            ShowPlayer(null);
            UserVolumeSlider.OnValueChanged += OnVolumeSliderChanged;
            UpdateUI();
        }

        private int MaxPlayers => 1024;

        private void SetIndexCharacters(string[] indexChars)
        {
            foreach (Button button in IndexButtons)
            {
                GameObject.Destroy(button.gameObject);
            }
            IndexButtons.Clear();

            foreach (string indexChar in indexChars)
            {
                Button indexButton = Instantiate(IndexButtonTemplate, IndexButtonTemplate.transform.parent);
                indexButton.gameObject.SetActive(true);
                TMP_Text buttonText = indexButton.GetComponentInChildren<TMP_Text>();
                if (buttonText != null)
                {
                    buttonText.text = indexChar;
                }
                indexButton.onClick.AddListener(() =>
                {
                    string filterName = "Showing players starting with " + indexChar;
                    if (_activeFilter != null && _activeFilter.Value.filterName == filterName)
                    {
                        _activeFilter = null;
                        UpdateUI();
                        return;
                    }
                    PlayerListFilter filter = new PlayerListFilter
                    {
                        filterName = filterName,
                        filterFunction = (player) =>
                        {
                            string displayName = player.Player.SafeDisplayName;
                            return displayName.StartsWith(indexChar, StringComparison.OrdinalIgnoreCase);
                        }
                    };
                    _activeFilter = filter;
                    UpdateUI();
                });
                IndexButtons.Add(indexButton);
            }
        }
        public void UpdateUI()
        {
            string[] indexChars = UpdatePlayerList();
            int playerCount = BasisNetworkPlayers.Players.Count;
            string titleText = $"{playerCount} / {MaxPlayers}";
            if (_activeFilter.HasValue)
            {
                titleText += $" - {_activeFilter.Value.filterName}";
            }
            TitleText.text = titleText;
            SetIndexCharacters(indexChars);
        }

        private void ClearButtonList(List<PanelButton> buttons)
        {
            foreach (PanelButton button in buttons)
                button.ReleaseInstance();

            buttons.Clear();
        }
        private void ClearButtons()
        {
            ClearButtonList(SelectionButtons);
        }

        private void ClearActionButtons()
        {
            ClearButtonList(ActionButtons);
        }

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
            List<UserButtonAction> actions = new List<UserButtonAction>();
            if (player == null) return actions.ToArray();

            bool isLocalPlayer = IsLocalPlayer(player);

            if (isLocalPlayer)
            {
                return actions.ToArray();
            }
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
            } else
            {
                actions.Add(new UserButtonAction
                {
                    title = "Avatar Hidden",
                    action = WrapAction(player, settings,() =>
                    {
                        settings.AvatarVisible = true;
                        _ = BasisPlayerSettingsManager.SetPlayerSettings(settings);
                        (player.Player as BasisRemotePlayer)?.ReloadAvatar();
                    }),
                    buttonStyle = "Button Danger"
                });
            }

            if (settings.AvatarInteraction) {
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
            /*actions.Add(new UserButtonAction
            {
                title = "Teleport Player To Me",
                action = WrapAction(player, settings, () =>
                {
                    BasisNetworkModeration.TeleportHere(player.playerId);
                }),
                buttonStyle = "Button Default",
            });*/

            return actions.ToArray();
        }

        private void CreateActionButtonsForPlayer(BasisNetworkPlayer player, BasisPlayerSettingsData settings)
        {
            ClearActionButtons();
            if (player == null) return;
            UserButtonAction[] actions = GetActionsForPlayer(player, settings);
            foreach (UserButtonAction action in actions)
            {
                PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, UserActionButtonParent);
                ActionButtons.Add(button);
                button.Descriptor.SetTitle(action.title);
                if (!string.IsNullOrEmpty(action.buttonStyle)) button.ButtonStyling.SetStyle(action.buttonStyle);
                button.OnClicked += () => action.action();
            }
        }
        private async Task<BasisPlayerSettingsData> LoadPlayerSettings(BasisNetworkPlayer player)
        {
            BasisPlayerSettingsData settings = await BasisPlayerSettingsManager.RequestPlayerSettings(player?.Player.UUID);
            _currentPlayerSettings = settings;
            UserVolumeSlider.SetValueWithoutNotify(settings.VolumeLevel);
            return settings;
        }

        private void OnVolumeSliderChanged(float value)
        {
            if (_currentPlayerSettings == null) return;
            _currentPlayerSettings.VolumeLevel = value;
            _ = BasisPlayerSettingsManager.SetPlayerSettings(_currentPlayerSettings);
        }

        private bool IsLocalPlayer(BasisNetworkPlayer player)
        {
            return player.IsLocal;
        }

        private PlatformBadge GetPlatformBadge(string platform)
        {
            foreach (PlatformBadge platformBadge in PlatformBadges)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(platform, platformBadge.platformRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return platformBadge;
                }
            }
            return new PlatformBadge
            {
                platformRegex = "Unknown",
                platformDisplayName = "*"+platform,
                platformIcon = null
            };
        }
        private async Task<PlayerBadge[]> GetPlayerBadges(BasisNetworkPlayer player)
        {
            List<PlayerBadge> badges = new List<PlayerBadge>();
            if (IsLocalPlayer(player))
            {
                badges.Add(new PlayerBadge
                {
                    badgeName = "You",
                    badgeIcon = null
                });
            }

            string platform = (player.Player as BasisRemotePlayer)?.GetRuntimePlatform().ToString() ?? "Unknown";
            PlatformBadge platformBadge = GetPlatformBadge(platform);
            badges.Add(new PlayerBadge
            {
                badgeName = platformBadge.platformDisplayName,
                badgeIcon = platformBadge.platformIcon
            });

            return badges.ToArray();
        }

        private void ClearBadges()
        {
            foreach (GameObject badge in BadgeObjects)
            {
                GameObject.Destroy(badge);
            }
            BadgeObjects.Clear();
        }
        private void CreateBadges(PlayerBadge[] badges)
        {
            ClearBadges();
            foreach (PlayerBadge badge in badges)
            {
                GameObject badgeObj = Instantiate(BadgeTemplate, BadgeTemplate.transform.parent);
                badgeObj.SetActive(true);
                BadgeObjects.Add(badgeObj);
                TMP_Text badgeText = badgeObj.GetComponentInChildren<TMP_Text>();
                if (badgeText != null)
                {
                    badgeText.text = badge.badgeName;
                }
                Image badgeImage = badgeObj.transform.Find("Icon")?.GetComponent<Image>();
                if (badgeImage != null && badge.badgeIcon != null)
                {
                    badgeImage.sprite = badge.badgeIcon;
                }
            }

        }
        public async Task ShowPlayer(BasisNetworkPlayer player)
        {
            UserVolumeSlider.gameObject.SetActive(player != null && !IsLocalPlayer(player));
            Descriptor.SetTitle(player?.Player.SafeDisplayName);
            Descriptor.SetDescription(player?.Player.UUID);

            BasisPlayerSettingsData settings = await LoadPlayerSettings(player);
            CreateActionButtonsForPlayer(player, settings);

            PlayerBadge[] badges = await GetPlayerBadges(player);
            CreateBadges(badges);
            Descriptor.ForceRebuild();
        }

        private bool PlayerPassesFilter(BasisNetworkPlayer player)
        {
            return _activeFilter == null || _activeFilter.Value.filterFunction(player);
        }

        private string[] UpdatePlayerList()
        {
            ClearButtons();
            List<string> firstCharacters = new();
            List<BasisNetworkPlayer> sortedPlayers = new(BasisNetworkPlayers.Players.Values);
            sortedPlayers.Sort((a, b) => string.Compare(a.Player.SafeDisplayName, b.Player.SafeDisplayName, StringComparison.OrdinalIgnoreCase));

            foreach (BasisNetworkPlayer player in sortedPlayers)
            {
                string displayName = player.Player.SafeDisplayName;
                string firstChar = displayName.Length > 0 ? displayName[..1].ToUpper() : "";
                if (!firstCharacters.Contains(firstChar))
                {
                    firstCharacters.Add(firstChar);
                }
                if (!PlayerPassesFilter(player)) continue;
                CreatePlayerButton(player);
            }
            firstCharacters.Sort();
            return firstCharacters.ToArray();
        }

        private void CreatePlayerButton(BasisNetworkPlayer player)
        {
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Avatar, TabButtonParent);
            SelectionButtons.Add(button);
            button.Descriptor.SetTitle(player.Player.SafeDisplayName);
            button.OnClicked += () => ShowPlayer(player);
        }
    }

}
