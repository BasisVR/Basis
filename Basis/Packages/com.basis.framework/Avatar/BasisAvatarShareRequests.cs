using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
namespace Basis.Scripts.Avatar
{
    [AutoStaticsCleanup]
    public static partial class BasisAvatarShareRequests
    {
        private static readonly HashSet<(ushort Owner, string Url)> asked = new HashSet<(ushort Owner, string Url)>();
        private static readonly Dictionary<string, List<ushort>> waiting = new Dictionary<string, List<ushort>>();
        private static readonly HashSet<string> approved = new HashSet<string>(), declined = new HashSet<string>();
        public static void Reset()
        {
            asked.Clear();
            waiting.Clear();
            approved.Clear();
            declined.Clear();
        }
        public static void OnRemoteLoadFailed(BasisRemotePlayer player, BasisLoadableBundle bundle)
        {
            if (player == null || player.IsDestroyed || player.NetworkReceiver == null || bundle?.BasisRemoteBundleEncrypted == null) return;
            string url = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, downloaded = bundle.BasisLocalEncryptedBundle?.DownloadedBeeFileLocation;
            if (string.IsNullOrEmpty(url) || BasisServerHostedBee.IsHostedUrl(url) || (!string.IsNullOrEmpty(downloaded) && File.Exists(downloaded)) || BasisAvatarFactory.RefusedByDownloadLimit(player, bundle)) return;
            ushort owner = player.NetworkReceiver.playerId;
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (!BasisNetworkConnection.LocalPlayerIsConnected || !BasisAvatarHostClient.ServerSupportsHosting || !asked.Add((owner, url))) return;
                if (BasisAvatarHostClient.AskShare(owner, url)) BasisDebug.Log($"Avatar download for {player.SafeDisplayName} failed, asking them to share it through the server.", BasisDebug.LogTag.Networking);
            });
        }
        public static void OnAnswered(ushort owner, string url, string hostedUrl)
        {
            if (!BasisNetworkPlayers.RemotePlayers.TryGetValue(owner, out BasisRemotePlayer player) || player == null || player.IsDestroyed) return;
            if (string.IsNullOrEmpty(hostedUrl))
            {
                BasisDebug.Log($"{player.SafeDisplayName} did not share their avatar through the server.", BasisDebug.LogTag.Networking);
                return;
            }
            BasisLoadableBundle current = player.AlwaysRequestedAvatar;
            if (!player.HasFailedAvatarLoadGlobally || current?.BasisRemoteBundleEncrypted == null || current.BasisRemoteBundleEncrypted.RemoteBeeFileLocation != url) return;
            BasisRemoteEncyptedBundle remote = current.BasisRemoteBundleEncrypted.Clone();
            remote.RemoteBeeFileLocation = hostedUrl;
            remote.RemoteVersionTag = string.Empty;
            BasisLoadableBundle swapped = new BasisLoadableBundle { UnlockPassword = current.UnlockPassword, BasisRemoteBundleEncrypted = remote };
            player.HasFailedAvatarLoadGlobally = false;
            player.AvatarLoadErrorMessage = null;
            player.OnAvatarFailedStateChanged?.Invoke();
            _ = player.CreateAvatar(player.AlwaysRequestedMode, swapped);
        }
        public static void OnAsked(ushort requester, string url)
        {
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            BasisLoadableBundle worn = local != null ? local.AvatarMetaData : null;
            if (worn?.BasisRemoteBundleEncrypted == null || worn.BasisRemoteBundleEncrypted.RemoteBeeFileLocation != url || declined.Contains(url) || !BasisAvatarHostClient.SharingAllowed || (BasisVrmAvatarLoader.TryGetDocument(worn.BasisBundleConnector, out BasisVrmDocument vrm) && !vrm.RedistributionAllowed))
            {
                BasisAvatarHostClient.AnswerShare(requester, url, null);
                return;
            }
            if (approved.Contains(url))
            {
                _ = ServeAsync(url, new List<ushort> { requester });
                return;
            }
            if (waiting.TryGetValue(url, out List<ushort> list))
            {
                if (!list.Contains(requester)) list.Add(requester);
                return;
            }
            waiting[url] = new List<ushort> { requester };
            string name = BasisNetworkPlayers.RemotePlayers.TryGetValue(requester, out BasisRemotePlayer remote) && remote != null ? remote.SafeDisplayName : null;
            if (string.IsNullOrWhiteSpace(name)) name = BasisLocalization.Get("network.avatarRequest.someone");
            if (!BasisNotificationCenter.RouteToNotifications && !BasisMainMenu.Instance) BasisMainMenu.Open();
            BasisMenuDialoguePanel.CreateNew(BasisLocalization.Get("network.avatarRequest.title"), BasisLocalization.Get("network.avatarRequest.body", name), BasisLocalization.Get("network.avatarShare.accept"), BasisLocalization.Get("network.avatarShare.deny"), share =>
            {
                if (!waiting.TryGetValue(url, out List<ushort> requesters)) return;
                waiting.Remove(url);
                if (share)
                {
                    approved.Add(url);
                    _ = ServeAsync(url, requesters);
                    return;
                }
                declined.Add(url);
                foreach (ushort id in requesters) BasisAvatarHostClient.AnswerShare(id, url, null);
            }, true, BasisPanelSeverity.Caution);
        }
        private static async Task ServeAsync(string url, List<ushort> requesters)
        {
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            BasisLoadableBundle worn = local != null ? local.AvatarMetaData : null;
            BasisHostedBeeLayout layout = null;
            string error = "You are no longer wearing this avatar.";
            if (worn?.BasisRemoteBundleEncrypted != null && worn.BasisRemoteBundleEncrypted.RemoteBeeFileLocation == url)
            {
                string path = BasisIOManagement.TryResolveLocalBeePath(url, out string localPath) ? localPath : worn.BasisLocalEncryptedBundle?.DownloadedBeeFileLocation;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) error = "The avatar file is not on this computer.";
                else
                {
                    (bool shared, byte reason, string hostError, BasisHostedBeeLayout hostedLayout) = await BasisAvatarHostClient.HostAsync(url, path, worn.UnlockPassword, false);
                    layout = shared ? hostedLayout : null;
                    error = shared ? null : $"reason {reason}: {hostError}";
                }
            }
            if (error != null) BasisDebug.LogWarning($"Sharing avatar on request failed ({error})", BasisDebug.LogTag.Networking);
            foreach (ushort id in requesters) BasisAvatarHostClient.AnswerShare(id, url, layout);
        }
    }
}
