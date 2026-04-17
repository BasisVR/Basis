using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Threading.Tasks;

/// <summary>
/// Sends and receives session-scoped "temp block" notifications between peers on
/// <see cref="BasisNetworkCommons.EventsChannel"/> using
/// <see cref="BasisNetworkCommons.EventType_PlayerTempBlock"/>.
///
/// The block a user sets in their own UI is persisted locally via
/// <see cref="BasisPlayerSettingsManager"/>. This handler mirrors that block onto
/// the *other* party in the current session so the block is effective both ways —
/// when A blocks B, B also can't see/hear A. The mirrored state is runtime-only
/// (never written to B's settings file) so A unblocking reliably restores visibility
/// and B's settings aren't polluted by other users' choices.
/// </summary>
public static class BasisNetworkHandleTempBlock
{
    private static bool initialized;

    /// <summary>
    /// Subscribes to remote-player join so we can re-send our persisted block state to
    /// any player already marked blocked when they arrive in the session. Idempotent.
    /// </summary>
    public static void Initialize()
    {
        if (initialized) return;
        BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayerJoined;
        initialized = true;
    }

    public static void Shutdown()
    {
        if (!initialized) return;
        BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayerJoined;
        initialized = false;
    }

    /// <summary>
    /// Send the local player's block state (blocked or unblocked) about the given
    /// target player. The server routes this to the target peer only.
    /// </summary>
    public static void SendTempBlock(ushort targetPlayerId, bool isBlocked)
    {
        if (BasisNetworkConnection.LocalPlayerIsConnected == false) return;
        if (BasisNetworkConnection.LocalPlayerPeer == null) return;

        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.EventType_PlayerTempBlock);
        writer.Put(targetPlayerId);
        writer.Put(isBlocked);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Called on the main thread when a temp-block event arrives from the server.
    /// Applies the block mirror to the remote player representing the sender.
    /// </summary>
    public static async void OnRemoteTempBlockReceived(ushort senderPlayerId, bool isBlocked)
    {
        if (!BasisNetworkPlayers.Players.TryGetValue(senderPlayerId, out BasisNetworkPlayer networkPlayer))
        {
            return;
        }
        BasisRemotePlayer remotePlayer = networkPlayer.Player as BasisRemotePlayer;
        if (remotePlayer == null)
        {
            return;
        }

        remotePlayer.TempBlocked = isBlocked;

        // Reapply audio volume: if muted by either local or temp block, silence.
        if (remotePlayer.NetworkReceiver != null && remotePlayer.NetworkReceiver.AudioReceiverModule != null)
        {
            float volume = 0f;
            if (!remotePlayer.IsEffectivelyBlocked)
            {
                var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                volume = settings != null ? settings.VolumeLevel : 1f;
            }
            remotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(volume);
        }

        // Reload the avatar: CreateAvatar consults IsEffectivelyBlocked to pick visible/fallback.
        remotePlayer.ReloadAvatar();

        // Refresh the nameplate visibility + clear any chat bubble that may be mid-display.
        if (remotePlayer.RemoteNamePlate != null)
        {
            if (isBlocked)
            {
                remotePlayer.RemoteNamePlate.SetChatText(string.Empty);
            }
            remotePlayer.RemoteNamePlate.RefreshActiveState();
        }
    }

    /// <summary>
    /// When a remote player joins our session, check whether we already have them
    /// blocked locally. If so, notify them so their client mirrors the block.
    /// </summary>
    private static async void OnRemotePlayerJoined(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
    {
        if (remotePlayer == null || string.IsNullOrEmpty(remotePlayer.UUID)) return;

        BasisPlayerSettingsData settings;
        try
        {
            settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
        }
        catch (System.Exception ex)
        {
            BasisDebug.LogError($"TempBlock: failed to read settings for joining player: {ex}");
            return;
        }

        if (settings != null && settings.IsBlocked)
        {
            SendTempBlock(networkPlayer.playerId, true);
        }
    }
}
