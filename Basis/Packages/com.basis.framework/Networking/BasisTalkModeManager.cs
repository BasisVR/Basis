using Basis.Network.Core;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Basis.Scripts.Networking
{

    public static class BasisTalkModeManager
    {
        public static BasisTalkMode CurrentMode { get; private set; } = BasisTalkMode.Normal;

        public static event Action OnLocalTalkModeChanged;

        private static readonly HashSet<ushort> privateMembers = new HashSet<ushort>();
        private static ushort thisPersonTarget;
        private static bool hasThisPersonTarget;
        private static BasisTalkMode pendingAnnounceExitMode;
        private static bool hasPendingAnnounceExitMode;

        /// <summary>
        /// True while an admin put us in shout, as opposed to us picking it ourselves. A held
        /// shout can only be left through the server, which re-checks the permission — otherwise
        /// the target cycles straight back out of a mode a moderator just put them in.
        /// </summary>
        private static bool adminShoutHeld;

        [RuntimeInitializeOnLoadMethod]
        private static void Init()
        {
            BasisNetworkModeration.OnAnnounceModeChanged -= HandleAnnounceModeChanged;
            BasisNetworkModeration.OnAnnounceModeChanged += HandleAnnounceModeChanged;
            BasisNetworkPlayer.OnRemotePlayerJoined -= HandleRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerJoined += HandleRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerLeft -= HandleRemotePlayerLeft;
            BasisNetworkPlayer.OnRemotePlayerLeft += HandleRemotePlayerLeft;
            BasisP2PManager.OnSessionStateChanged -= HandleP2PSessionChanged;
            BasisP2PManager.OnSessionStateChanged += HandleP2PSessionChanged;
            BasisNetworkManagement.OnlocalPermissionsChanged -= HandlePermissionsChanged;
            BasisNetworkManagement.OnlocalPermissionsChanged += HandlePermissionsChanged;
            BasisSettingsDefaults.AnnounceShowOnMenuBar.OnChanged -= HandleAnnounceMenuBarPrefChanged;
            BasisSettingsDefaults.AnnounceShowOnMenuBar.OnChanged += HandleAnnounceMenuBarPrefChanged;
            BasisSettingsDefaults.TalkToNoOne.OnChanged -= HandleTalkToNoOnePrefChanged;
            BasisSettingsDefaults.TalkToNoOne.OnChanged += HandleTalkToNoOnePrefChanged;
            BasisSettingsDefaults.ShoutMode.OnChanged -= HandleShoutPrefChanged;
            BasisSettingsDefaults.ShoutMode.OnChanged += HandleShoutPrefChanged;
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction -= HandleLocalMuteChanged;
            BasisLocalMicrophoneDriver.OnPausedAction += HandleLocalMuteChanged;
#endif
        }

        public static bool LocalCanAnnounce()
        {
            return BasisNetworkManagement.LocalPermissions != null &&
                   BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView);
        }

        public static bool AnnounceAvailableOnMenuBar()
        {
            return LocalCanAnnounce() && BasisSettingsDefaults.AnnounceShowOnMenuBar.RawValue;
        }

        public static bool TalkToNoOneAvailable()
        {
            return BasisSettingsDefaults.TalkToNoOne.RawValue;
        }

        /// <summary>
        /// Shout's opt-in toggle lives on the Admin tab, which is itself gated on
        /// <see cref="PermNodes.PermissionsView"/>. Check the permission here too rather than
        /// trusting the UI to be the only way in: a persisted pref outlives the permission that
        /// let someone set it, and a revoked admin would otherwise keep shouting.
        /// </summary>
        public static bool LocalCanShout()
        {
            return BasisNetworkManagement.LocalPermissions != null &&
                   BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView);
        }

        public static bool ShoutAvailable()
        {
            // An admin-granted shout is available whether or not we opted into the menu-bar
            // toggle, so the mode we are actually in never reads as unavailable.
            return adminShoutHeld || (LocalCanShout() && BasisSettingsDefaults.ShoutMode.RawValue);
        }

        /// <summary>
        /// Server told us an admin granted or revoked shout for the local player. Enter or leave
        /// the mode, and hold it so <see cref="SetMode"/> routes any attempt to leave back through
        /// the server rather than applying locally.
        /// </summary>
        public static void OnAdminShoutChanged(bool enabled)
        {
            adminShoutHeld = enabled;
            if (enabled)
            {
                if (CurrentMode != BasisTalkMode.Shout) ApplyMode(BasisTalkMode.Shout);
                else OnLocalTalkModeChanged?.Invoke();
                return;
            }

            // Always Normal, never a mode the target asked for while held. Announce carries a
            // pending exit because its request is answered at once; a held shout's release can be
            // refused and then sit unanswered until a moderator lifts it minutes later, and
            // landing the player in whatever they last poked back then is a surprise, not a
            // courtesy.
            if (CurrentMode == BasisTalkMode.Shout)
            {
                ApplyMode(BasisTalkMode.Normal);
                return;
            }
            OnLocalTalkModeChanged?.Invoke();
        }

        /// <summary>
        /// True while the local player's voice should carry <see cref="BasisShout.RangeMultiplier"/>
        /// times as far as their microphone range. Read by the transmit tick when it decides who
        /// goes on the voice recipient list.
        /// </summary>
        public static bool LocalIsShouting => CurrentMode == BasisTalkMode.Shout;

        private static int localOnlyHolds;

        /// <summary>
        /// Scoped "nothing leaves this client" hold for local features that must not be heard by
        /// anyone, such as the microphone test recorder. Deliberately independent of CurrentMode and
        /// of the TalkToNoOne setting, so it neither changes nor persists the user's own choice, and
        /// releasing it restores whatever mode is active rather than a value captured earlier.
        /// </summary>
        public static void AddLocalOnlyHold()
        {
            if (Interlocked.Increment(ref localOnlyHolds) == 1) OnLocalTalkModeChanged?.Invoke();
        }

        public static void ReleaseLocalOnlyHold()
        {
            int remaining = Interlocked.Decrement(ref localOnlyHolds);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref localOnlyHolds, 0);
                return;
            }

            if (remaining == 0) OnLocalTalkModeChanged?.Invoke();
        }

        public static bool LocalOnlyHeld => Volatile.Read(ref localOnlyHolds) > 0;

        public static bool TransmitBlockedLocally => CurrentMode == BasisTalkMode.NoOne || LocalOnlyHeld;

        private static readonly BasisTalkMode[] CycleOrder =
        {
            BasisTalkMode.Normal,
            BasisTalkMode.Shout,
            BasisTalkMode.Private,
            BasisTalkMode.ThisPerson,
            BasisTalkMode.Direct,
            BasisTalkMode.Announce,
            BasisTalkMode.NoOne,
        };

        /// <summary>
        /// True only when there is a reason to expose the mic-mode button: we're already
        /// in a non-normal mode, shout or announce is enabled on the menu bar, talk-to-no-one is
        /// opted into, have a private set, a marked person, or at least one P2P-connected peer.
        /// </summary>
        public static bool ShouldShowModeButton()
        {
            if (CurrentMode != BasisTalkMode.Normal) return true;
            if (AnnounceAvailableOnMenuBar()) return true;
            if (ShoutAvailable()) return true;
            if (TalkToNoOneAvailable()) return true;
            if (privateMembers.Count > 0) return true;
            if (hasThisPersonTarget) return true;
            return BasisP2PManager.GetConnectedSessionCount() > 0;
        }

        public static bool ModeAvailable(BasisTalkMode mode)
        {
            switch (mode)
            {
                case BasisTalkMode.Normal: return true;
                case BasisTalkMode.Private: return privateMembers.Count > 0;
                case BasisTalkMode.ThisPerson: return hasThisPersonTarget;
                case BasisTalkMode.Direct: return BasisP2PManager.GetConnectedSessionCount() > 0;
                case BasisTalkMode.Announce: return AnnounceAvailableOnMenuBar();
                case BasisTalkMode.Shout: return ShoutAvailable();
                case BasisTalkMode.NoOne: return TalkToNoOneAvailable();
                default: return false;
            }
        }

        public static void CycleMode()
        {
            int start = Array.IndexOf(CycleOrder, CurrentMode);
            if (start < 0) start = 0;
            for (int step = 1; step <= CycleOrder.Length; step++)
            {
                BasisTalkMode candidate = CycleOrder[(start + step) % CycleOrder.Length];
                if (candidate == CurrentMode) continue;
                if (ModeAvailable(candidate))
                {
                    SetMode(candidate);
                    return;
                }
            }
        }

        public static void SetMode(BasisTalkMode mode)
        {
            if (mode == BasisTalkMode.Announce)
            {
                if (LocalCanAnnounce() && BasisNetworkPlayer.LocalPlayer != null)
                {
                    BasisNetworkModeration.EnableAnnounceMode(BasisNetworkPlayer.LocalPlayer.playerId);
                }
                return;
            }

            // A held shout is the server's to release, exactly as announce is. Asking rather than
            // applying means a non-admin target is refused by the same permission check that put
            // them here, while an admin's own request comes straight back and lands. The local
            // mode is left alone either way — dropping the hold because LocalPlayer happened to
            // be null would hand the target a way out that never reached the server at all.
            if (adminShoutHeld && CurrentMode == BasisTalkMode.Shout && mode != BasisTalkMode.Shout)
            {
                if (BasisNetworkPlayer.LocalPlayer != null)
                {
                    BasisNetworkModeration.DisableShoutMode(BasisNetworkPlayer.LocalPlayer.playerId);
                }
                return;
            }

            if (CurrentMode == BasisTalkMode.Announce && BasisNetworkPlayer.LocalPlayer != null)
            {
                pendingAnnounceExitMode = mode;
                hasPendingAnnounceExitMode = true;
                BasisNetworkModeration.DisableAnnounceMode(BasisNetworkPlayer.LocalPlayer.playerId);
                return;
            }

            ApplyMode(mode);
        }

        public static bool TogglePrivateMember(ushort playerId)
        {
            bool added;
            if (privateMembers.Contains(playerId))
            {
                privateMembers.Remove(playerId);
                added = false;
            }
            else
            {
                privateMembers.Add(playerId);
                added = true;
            }

            if (!added && privateMembers.Count == 0 && CurrentMode == BasisTalkMode.Private)
            {
                // Removed the last private member while in Private mode — fall back to Normal.
                SetMode(BasisTalkMode.Normal);
                return added;
            }

            if (CurrentMode == BasisTalkMode.Private)
            {
                BasisTransmissionResults.ForceVoiceRecipientResend = true;
            }
            OnLocalTalkModeChanged?.Invoke();
            return added;
        }

        public static bool IsPrivateMember(ushort playerId) => privateMembers.Contains(playerId);

        public static void SetThisPersonTarget(ushort playerId)
        {
            thisPersonTarget = playerId;
            hasThisPersonTarget = true;
            SetMode(BasisTalkMode.ThisPerson);
        }

        public static bool TryGetThisPersonTarget(out ushort playerId)
        {
            playerId = thisPersonTarget;
            return hasThisPersonTarget;
        }

        public static bool IsTalkingOnlyTo(ushort playerId)
        {
            return CurrentMode == BasisTalkMode.ThisPerson && hasThisPersonTarget && thisPersonTarget == playerId;
        }

        public static void StopThisPerson()
        {
            hasThisPersonTarget = false;
            if (CurrentMode == BasisTalkMode.ThisPerson)
            {
                ApplyMode(BasisTalkMode.Normal);
            }
            else
            {
                OnLocalTalkModeChanged?.Invoke();
            }
        }

        public static bool IsRecipient(ushort playerId)
        {
            switch (CurrentMode)
            {
                case BasisTalkMode.Private: return privateMembers.Contains(playerId);
                case BasisTalkMode.ThisPerson: return hasThisPersonTarget && playerId == thisPersonTarget;
                default: return false;
            }
        }

        public static void OnRemoteTalkModeReceived(ushort playerId, byte modeByte)
        {
            if (BasisNetworkPlayers.Players.TryGetValue(playerId, out BasisNetworkPlayer np) &&
                np != null && np.Player is BasisRemotePlayer remote)
            {
                remote.SetTalkMode((BasisTalkMode)modeByte);
            }
        }

        private static void ApplyMode(BasisTalkMode mode)
        {
            CurrentMode = mode;
            BasisTransmissionResults.ForceVoiceRecipientResend = true;
            BroadcastLocalMode();
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void BroadcastLocalMode()
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null) return;

            NetDataWriter writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.EventType_TalkModeChanged);
            writer.Put((byte)CurrentMode);
            BasisNetworkConnection.LocalPlayerPeer.Send(
                writer,
                BasisNetworkCommons.EventsChannel,
                DeliveryMethod.ReliableOrdered);
        }

        private static void HandleLocalMuteChanged(bool muted)
        {
            BroadcastLocalMute(muted);
        }

        private static void BroadcastLocalMute(bool muted)
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null) return;

            NetDataWriter writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.EventType_MuteStateChanged);
            writer.Put((byte)(muted ? 1 : 0));
            BasisNetworkConnection.LocalPlayerPeer.Send(
                writer,
                BasisNetworkCommons.EventsChannel,
                DeliveryMethod.ReliableOrdered);
        }

        public static void OnRemoteMuteReceived(ushort playerId, bool muted)
        {
            if (BasisNetworkPlayers.Players.TryGetValue(playerId, out BasisNetworkPlayer np) &&
                np != null && np.Player is BasisRemotePlayer remote)
            {
                remote.SetSelfMuted(muted);
            }
        }

        private static void HandleAnnounceModeChanged(ushort playerId, bool enabled)
        {
            if (BasisNetworkPlayer.LocalPlayer == null || playerId != BasisNetworkPlayer.LocalPlayer.playerId) return;

            if (enabled)
            {
                hasPendingAnnounceExitMode = false;
                ApplyMode(BasisTalkMode.Announce);
            }
            else if (CurrentMode == BasisTalkMode.Announce)
            {
                BasisTalkMode target = hasPendingAnnounceExitMode ? pendingAnnounceExitMode : BasisTalkMode.Normal;
                hasPendingAnnounceExitMode = false;
                ApplyMode(target);
            }
        }

        private static void HandleRemotePlayerJoined(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (CurrentMode != BasisTalkMode.Normal)
            {
                BroadcastLocalMode();
            }
#if !BASIS_DISABLE_MICROPHONE
            if (BasisLocalMicrophoneDriver.isPaused)
            {
                BroadcastLocalMute(true);
            }
#endif
        }

        private static void HandleRemotePlayerLeft(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (networkPlayer == null) return;
            ushort id = networkPlayer.playerId;

            if (hasThisPersonTarget && thisPersonTarget == id)
            {
                hasThisPersonTarget = false;
                if (CurrentMode == BasisTalkMode.ThisPerson)
                {
                    ApplyMode(BasisTalkMode.Normal);
                    return;
                }
            }

            if (privateMembers.Remove(id) && CurrentMode == BasisTalkMode.Private)
            {
                BasisTransmissionResults.ForceVoiceRecipientResend = true;
            }
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void HandleP2PSessionChanged(ushort otherPlayerId, BasisP2PManager.P2PSessionState state)
        {
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void HandlePermissionsChanged()
        {
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void HandleAnnounceMenuBarPrefChanged(bool _)
        {
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void HandleTalkToNoOnePrefChanged(bool enabled)
        {
            if (!enabled && CurrentMode == BasisTalkMode.NoOne)
            {
                SetMode(BasisTalkMode.Normal);
                return;
            }
            OnLocalTalkModeChanged?.Invoke();
        }

        private static void HandleShoutPrefChanged(bool enabled)
        {
            if (!enabled && CurrentMode == BasisTalkMode.Shout)
            {
                SetMode(BasisTalkMode.Normal);
                return;
            }
            OnLocalTalkModeChanged?.Invoke();
        }
    }
}
