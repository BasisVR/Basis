using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;
using UnityEngine.UI;
using P2PState = Basis.Scripts.Networking.BasisP2PManager.P2PSessionState;

namespace Basis.BasisUI
{
    public partial class IndividualPlayerProvider
    {
        /// <summary>
        /// One multicast hook per piece of per-player state the panel exposes in more than one
        /// place. The Actions tab and the detail tabs both subscribe to a hook and both fire it,
        /// so pressing a control on either side repaints the other without either side needing a
        /// reference to it. Subscribed handlers only paint — they must never raise change events
        /// or the two sides would ping-pong.
        /// </summary>
        private sealed class PlayerActionSync
        {
            public Action<BasisPlayerSettingsData> Volume;
            public Action<BasisPlayerSettingsData> Blocked;
            public Action<BasisPlayerSettingsData> AvatarVisible;
            public Action<BasisPlayerSettingsData> JiggleGrab;
            public Action Pin;
            public Action Highlight;
            public Action TalkModes;
            public Action DirectConnection;
            public Action Announce;
            public Action Shout;
            public Action EyeHeight;
        }

        /// <summary>
        /// True when the world beacon is currently pointing at this player. Only one beacon
        /// exists at a time, so a live beacon aimed at somebody else reads as "not highlighted".
        /// </summary>
        private static bool IsHighlighted(IBasisPlayer player)
        {
            return s_beaconGO != null && s_beaconTarget != null && s_beaconTarget.Player == player;
        }

        /// <summary>
        /// Whether the direct-connection button should refuse outright. A lock applied after the
        /// fact doesn't tear down a session that is already up, so a live link still reports
        /// itself and can still be cancelled.
        /// </summary>
        private static bool DirectConnectionBlocked(P2PState state)
        {
            if (state == P2PState.Connected || state == P2PState.Reconnecting || state == P2PState.PartialConnection)
            {
                return false;
            }
            return BasisSettingsDefaults.DisableDirectConnections.RawValue ||
                (BasisNetworkModeration.GlobalDirectConnectLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass());
        }

        /// <summary>
        /// Runs the direct-connection button's decision tree: cancel an in-flight or live session,
        /// refuse with an explanation when the feature is locked locally or server-side, and
        /// otherwise confirm before handing both sides each other's IP address. Shared so the
        /// Actions tab and the Network tab cannot drift apart on the gating.
        /// </summary>
        public static void ToggleDirectConnection(BasisRemotePlayer player)
        {
            if (player == null) return;
            if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(player, out BasisNetworkPlayer net)) return;

            ushort playerId = net.playerId;
            P2PState state = BasisP2PManager.GetSessionState(playerId);

            // Only Idle and Failed start a new request; anything else is already in flight.
            if (state != P2PState.Idle && state != P2PState.Failed)
            {
                BasisP2PManager.CancelSession(playerId);
                return;
            }

            if (BasisNetworkModeration.GlobalDirectConnectLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
            {
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.title"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.serverLockedDialog.body"),
                    BasisLocalization.Get("ui.ok"),
                    _ => { },
                    severity: BasisPanelSeverity.Caution,
                    category: BasisNotificationCategory.Player);
                return;
            }

            if (BasisSettingsDefaults.DisableDirectConnections.RawValue)
            {
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.title"),
                    BasisLocalization.Get("menu.individualPlayer.directConnection.disabledDialog.body"),
                    BasisLocalization.Get("ui.ok"),
                    _ => { },
                    severity: BasisPanelSeverity.Caution,
                    category: BasisNotificationCategory.Player);
                return;
            }

            // Hot: confirming this hands both sides each other's IP address.
            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("menu.individualPlayer.directConnection.outgoingDialog.title"),
                BasisLocalization.Get("menu.individualPlayer.directConnection.outgoingDialog.body", player.DisplayName, player.UUID),
                BasisLocalization.Get("menu.individualPlayer.directConnection.request"),
                BasisLocalization.Get("ui.cancel"),
                confirmed =>
                {
                    if (!confirmed) return;
                    BasisP2PManager.SendRequest(playerId);
                },
                severity: BasisPanelSeverity.Hot,
                category: BasisNotificationCategory.Player);
        }

        /// <summary>
        /// Flips this player's avatar visibility and reloads them. Clearing the failed-load flags
        /// is what makes showing an avatar again actually re-attempt the download — without it a
        /// player who tripped <see cref="BasisRemotePlayer.HasFailedAvatarLoadGlobally"/> stays
        /// invisible no matter how often the button is pressed.
        /// </summary>
        private static async System.Threading.Tasks.Task<BasisPlayerSettingsData> ToggleAvatarVisible(BasisRemotePlayer player)
        {
            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);
            settings.AvatarVisible = !settings.AvatarVisible;
            await BasisPlayerSettingsManager.SetPlayerSettings(settings);

            if (player != null)
            {
                player.HasFailedAvatarLoadGlobally = false;
                player.AvatarLoadErrorMessage = null;
                player.OnAvatarFailedStateChanged?.Invoke();
                player.ReloadAvatar();
            }

            return settings;
        }

        /// <summary>
        /// Persists the jiggle-grab permission and pushes it onto the live player. Turning it off
        /// revokes any grab already in flight rather than waiting for the grabber to let go.
        /// </summary>
        private static async System.Threading.Tasks.Task<BasisPlayerSettingsData> SetJiggleGrabAllowed(BasisRemotePlayer player, bool allowed)
        {
            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID);
            settings.JiggleGrabAllowed = allowed;
            await BasisPlayerSettingsManager.SetPlayerSettings(settings);

            if (player != null)
            {
                player.JiggleGrabAllowed = allowed;
                if (!allowed && BasisNetworkPlayers.PlayerToNetworkedPlayer(player, out BasisNetworkPlayer net))
                {
                    Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.RevokePlayer(net.playerId);
                }
            }

            return settings;
        }

        /// <summary>
        /// Applies this player's eye height to the local avatar. Custom scale has to be on first
        /// or the applied height is ignored, so the toggle is flipped for the user rather than
        /// failing silently.
        /// </summary>
        private static bool ApplyMatchedEyeHeight(BasisRemotePlayer player, out float eyeHeight)
        {
            if (!BasisHeightDriver.TryGetMatchedEyeHeightOverrideMeters(player, out eyeHeight))
            {
                BasisDebug.LogWarning("Cannot match eye height because the selected remote avatar eye height is unavailable.", BasisDebug.LogTag.Avatar);
                return false;
            }

            if (!SMModuleCalibration.ApplyCustomScale)
            {
                BasisSettingsDefaults.CustomScale.SetValue(true);
                SMModuleCalibration.ApplyCustomScale = true;
            }

            BasisHeightDriver.ApplyRuntimeOscEyeHeightOverride(eyeHeight);
            return true;
        }

        // Tiles are title-only, so they need width for the longest label ("Remove from Private
        // Chat") rather than height. Flexible constraint means the grid reflows to one column on
        // a narrow panel instead of clipping.
        private static readonly Vector2 ActionTileSize = new Vector2(320f, 64f);

        /// <summary>
        /// Grid container for the action tiles, parented inside the group so the group keeps its
        /// header while its contents lay out in columns rather than one per row.
        /// </summary>
        private static RectTransform BuildActionGrid(RectTransform parent)
        {
            GameObject gridGO = new GameObject("ActionGrid", typeof(RectTransform));
            RectTransform gridRect = (RectTransform)gridGO.transform;
            gridRect.SetParent(parent, false);
            gridRect.anchorMin = new Vector2(0f, 1f);
            gridRect.anchorMax = new Vector2(1f, 1f);
            gridRect.pivot = new Vector2(0.5f, 1f);

            GridLayoutGroup grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = ActionTileSize;
            grid.spacing = new Vector2(10f, 10f);
            grid.padding = new RectOffset(10, 10, 10, 10);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;

            ContentSizeFitter fitter = gridGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = gridGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return gridRect;
        }

        /// <summary>
        /// Builds the Actions tab — the panel's landing page. Every tile is a single press for
        /// something that otherwise sits several tabs deep; nothing here is exclusive to this tab,
        /// so the detail tabs remain the place to go for the surrounding context and settings.
        /// Tiles carry no inline description so the whole set fits on one screen — the long-form
        /// text the detail tabs show moves onto the hover tooltip instead.
        /// </summary>
        private static void BuildActionsPage(PanelTabPage page, BasisRemotePlayer player, BasisPlayerSettingsData settings, PlayerActionSync sync)
        {
            RectTransform root = page.Descriptor.ContentParent;

            var group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            group.SetTitle(BasisLocalization.Get("menu.individualPlayer.actions"));
            group.SetDescription(BasisLocalization.Get("menu.individualPlayer.actions.description"));
            RectTransform content = BuildActionGrid(group.ContentParent);

            PanelButton NewAction(string tooltipKey)
            {
                PanelButton button = PanelButton.CreateNew(content);
                button.Descriptor.SetDescription(string.Empty);
                button.Descriptor.SetTooltip(BasisLocalization.Get(tooltipKey));
                return button;
            }

            // ---- Block ----
            PanelButton blockBtn = NewAction("menu.individualPlayer.block.button.description");
            void PaintBlock(BasisPlayerSettingsData s)
            {
                if (blockBtn == null || blockBtn.Descriptor == null) return;
                blockBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    s.IsBlocked ? "menu.individualPlayer.unblock" : "menu.individualPlayer.blockButton"));
            }
            PaintBlock(settings);
            sync.Blocked += PaintBlock;
            blockBtn.OnClicked += async () =>
            {
                await ToggleBlockWithConfirmation(player);
                sync.Blocked?.Invoke(await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID));
            };

            // ---- Mute ----
            PanelButton muteBtn = NewAction("menu.individualPlayer.mute.description");
            void PaintMute(BasisPlayerSettingsData s)
            {
                if (muteBtn == null || muteBtn.Descriptor == null) return;
                muteBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    s.VolumeLevel <= 0f ? "menu.individualPlayer.unmute" : "menu.individualPlayer.mute"));
            }
            PaintMute(settings);
            sync.Volume += PaintMute;
            muteBtn.OnClicked += async () =>
            {
                await ToggleMute(player);
                sync.Volume?.Invoke(await BasisPlayerSettingsManager.RequestPlayerSettings(player.UUID));
            };

            bool hasNetTarget = BasisNetworkPlayers.PlayerToNetworkedPlayer(player, out BasisNetworkPlayer netTarget);
            ushort netPlayerId = hasNetTarget ? netTarget.playerId : (ushort)0;

            // ---- Pin ----
            string uuid = player.UUID;
            PanelButton pinBtn = NewAction("menu.individualPlayer.pin.button.description");
            void PaintPin()
            {
                if (pinBtn == null || pinBtn.Descriptor == null) return;
                pinBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    PinnedPlayers.IsPinned(uuid) ? "menu.individualPlayer.unpin" : "menu.individualPlayer.pinButton"));
            }
            PaintPin();
            sync.Pin += PaintPin;
            pinBtn.OnClicked += () =>
            {
                PinnedPlayers.Toggle(uuid);
                sync.Pin?.Invoke();
            };

            // ---- Highlight ----
            PanelButton highlightBtn = NewAction("menu.individualPlayer.highlight.description");
            void PaintHighlight()
            {
                if (highlightBtn == null || highlightBtn.Descriptor == null) return;
                highlightBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    IsHighlighted(player) ? "menu.individualPlayer.removeHighlight" : "menu.individualPlayer.highlight"));
            }
            PaintHighlight();
            sync.Highlight += PaintHighlight;
            highlightBtn.OnClicked += () =>
            {
                if (hasNetTarget) SetHighlight(netTarget);
                sync.Highlight?.Invoke();
            };

            // ---- Group chat (the private voice set) and private chat (this person only) ----
            PanelButton groupChatBtn = NewAction("menu.individualPlayer.privateChat.add.description");
            PanelButton privateChatBtn = NewAction("menu.individualPlayer.talkToOnly.description");
            void PaintTalkModes()
            {
                if (groupChatBtn != null && groupChatBtn.Descriptor != null)
                {
                    groupChatBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasNetTarget && BasisTalkModeManager.IsPrivateMember(netPlayerId)
                            ? "menu.individualPlayer.privateChat.remove"
                            : "menu.individualPlayer.privateChat.add"));
                }
                if (privateChatBtn != null && privateChatBtn.Descriptor != null)
                {
                    privateChatBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        hasNetTarget && BasisTalkModeManager.IsTalkingOnlyTo(netPlayerId)
                            ? "menu.individualPlayer.talkToOnly.stop"
                            : "menu.individualPlayer.talkToOnly"));
                }
            }
            PaintTalkModes();
            sync.TalkModes += PaintTalkModes;

            groupChatBtn.OnClicked += () =>
            {
                if (!hasNetTarget) return;
                BasisTalkModeManager.TogglePrivateMember(netPlayerId);
                sync.TalkModes?.Invoke();
            };

            privateChatBtn.OnClicked += () =>
            {
                if (!hasNetTarget) return;
                if (BasisTalkModeManager.IsTalkingOnlyTo(netPlayerId))
                    BasisTalkModeManager.StopThisPerson();
                else
                    BasisTalkModeManager.SetThisPersonTarget(netPlayerId);
                sync.TalkModes?.Invoke();
            };

            // ---- Match eye height ----
            PanelButton eyeHeightBtn = NewAction("menu.individualPlayer.matchEyeHeight.unavailable");
            eyeHeightBtn.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.matchEyeHeight"));
            void PaintEyeHeight()
            {
                if (eyeHeightBtn == null || eyeHeightBtn.Descriptor == null) return;
                eyeHeightBtn.Descriptor.SetTooltip(
                    BasisHeightDriver.TryGetMatchedEyeHeightOverrideMeters(player, out float height)
                        ? BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.description", height)
                        : BasisLocalization.Get("menu.individualPlayer.matchEyeHeight.unavailable"));
            }
            PaintEyeHeight();
            sync.EyeHeight += PaintEyeHeight;
            eyeHeightBtn.OnClicked += () =>
            {
                ApplyMatchedEyeHeight(player, out _);
                sync.EyeHeight?.Invoke();
            };

            // ---- Hide / show avatar ----
            PanelButton avatarBtn = NewAction("menu.individualPlayer.toggleAvatar.description");
            void PaintAvatarVisible(BasisPlayerSettingsData s)
            {
                if (avatarBtn == null || avatarBtn.Descriptor == null) return;
                avatarBtn.Descriptor.SetTitle(BasisLocalization.Get(
                    s.AvatarVisible ? "menu.individualPlayer.hideAvatar" : "menu.individualPlayer.showAvatar"));
            }
            PaintAvatarVisible(settings);
            sync.AvatarVisible += PaintAvatarVisible;
            avatarBtn.OnClicked += async () =>
            {
                sync.AvatarVisible?.Invoke(await ToggleAvatarVisible(player));
            };

            // ---- Announce mode (admin only, same gate the Admin tab uses) ----
            if (hasNetTarget && BasisTalkModeManager.LocalCanAnnounce())
            {
                PanelButton announceBtn = NewAction("menu.individualPlayer.announce.description");
                void PaintAnnounce()
                {
                    if (announceBtn == null || announceBtn.Descriptor == null) return;
                    announceBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        BasisAnnounceAudioDriver.IsInAnnounceMode(netPlayerId)
                            ? "menu.individualPlayer.announce.disable"
                            : "menu.individualPlayer.announce.enable"));
                }
                PaintAnnounce();
                sync.Announce += PaintAnnounce;
                // No repaint here — announce is a server round trip, so RunAction repaints from
                // BasisNetworkModeration.OnAnnounceModeChanged once the change actually lands.
                announceBtn.OnClicked += () =>
                {
                    if (BasisAnnounceAudioDriver.IsInAnnounceMode(netPlayerId))
                        BasisNetworkModeration.DisableAnnounceMode(netPlayerId);
                    else
                        BasisNetworkModeration.EnableAnnounceMode(netPlayerId);
                };
            }

            // ---- Shout mode (admin only, same gate the Admin tab uses) ----
            if (hasNetTarget && BasisTalkModeManager.LocalCanShout())
            {
                PanelButton shoutBtn = NewAction("menu.individualPlayer.shout.description");
                void PaintShout()
                {
                    if (shoutBtn == null || shoutBtn.Descriptor == null) return;
                    shoutBtn.Descriptor.SetTitle(BasisLocalization.Get(
                        BasisNetworkModeration.IsInShoutMode(netPlayerId)
                            ? "menu.individualPlayer.shout.disable"
                            : "menu.individualPlayer.shout.enable"));
                }
                PaintShout();
                sync.Shout += PaintShout;
                // Same server round trip as announce, so the repaint comes from
                // BasisNetworkModeration.OnShoutModeChanged once the grant lands.
                shoutBtn.OnClicked += () =>
                {
                    if (BasisNetworkModeration.IsInShoutMode(netPlayerId))
                        BasisNetworkModeration.DisableShoutMode(netPlayerId);
                    else
                        BasisNetworkModeration.EnableShoutMode(netPlayerId);
                };
            }

            // Toggles sit under the tile grid rather than in it: a toggle row is a title, a
            // description and the switch laid out across the full panel width, and a 320-wide
            // grid cell clips the title to fit.
            RectTransform rows = group.ContentParent;

            // ---- Direct connection ----
            PanelToggle directConnToggle = PanelToggle.CreateNewEntry(rows);
            directConnToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.directConnection"));
            directConnToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.directConnection.description"));
            void PaintDirectConn()
            {
                if (directConnToggle == null || directConnToggle.Descriptor == null) return;
                P2PState state = BasisP2PManager.GetSessionState(netPlayerId);
                bool live = state != P2PState.Idle && state != P2PState.Failed;
                // Every paint reads the live session, so a link that drops, a request that is
                // refused and a cancelled confirmation all put the switch back to off by itself.
                directConnToggle.SetValueWithoutNotify(live);
                directConnToggle.Descriptor.SetDescription(
                    !live && DirectConnectionBlocked(state)
                        ? BasisLocalization.Get("menu.individualPlayer.directConnection.disabled")
                        : string.Empty);
            }
            PaintDirectConn();
            sync.DirectConnection += PaintDirectConn;
            directConnToggle.OnValueChanged += _ =>
            {
                ToggleDirectConnection(player);
                sync.DirectConnection?.Invoke();
            };

            // ---- Jiggle grabbing ----
            PanelToggle jiggleGrabToggle = PanelToggle.CreateNewEntry(rows);
            jiggleGrabToggle.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.jiggleGrab"));
            jiggleGrabToggle.Descriptor.SetDescription(string.Empty);
            jiggleGrabToggle.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.jiggleGrab.description"));
            jiggleGrabToggle.SetValueWithoutNotify(settings.JiggleGrabAllowed);
            sync.JiggleGrab += s =>
            {
                if (jiggleGrabToggle == null) return;
                jiggleGrabToggle.SetValueWithoutNotify(s.JiggleGrabAllowed);
            };
            jiggleGrabToggle.OnValueChanged += async allowed =>
            {
                sync.JiggleGrab?.Invoke(await SetJiggleGrabAllowed(player, allowed));
            };
        }
    }
}
