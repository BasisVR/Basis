using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderNetworkTab
    {
        public static void BuildNetworkStatsGroup(RectTransform container, out NetworkStatsPanelUpdater updater)
        {
            var netGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            netGroup.SetBackgroundVisible(false);

            // Connection status
            var connectionField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            connectionField.SetTitle(BasisLocalization.Get("settings.network.connection"));
            connectionField.SetDescription("...");

            // Server info
            var serverField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            serverField.SetTitle(BasisLocalization.Get("settings.network.server"));
            serverField.SetDescription("...");

            // Ping / RTT
            var pingField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            pingField.SetTitle(BasisLocalization.Get("settings.network.ping"));
            pingField.SetDescription("...");

            // Players
            var playersField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            playersField.SetTitle(BasisLocalization.Get("menu.provider.players"));
            playersField.SetDescription("...");

            // Transmission
            var transmissionField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            transmissionField.SetTitle(BasisLocalization.Get("menu.individualPlayer.transmission"));
            transmissionField.SetDescription("...");

            // Bandwidth
            var bandwidthField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            bandwidthField.SetTitle(BasisLocalization.Get("settings.network.bandwidth"));
            bandwidthField.SetDescription("...");

            // Server Metadata
            var metaField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            metaField.SetTitle(BasisLocalization.Get("settings.network.serverMetadata"));
            metaField.SetDescription("...");

            netGroup.IsolateAsCanvas();

            // Create a holder GO for the updater MonoBehaviour
            var holderGO = new GameObject("NetworkStatsUpdater");
            holderGO.transform.SetParent(netGroup.transform, false);
            updater = holderGO.AddComponent<NetworkStatsPanelUpdater>();
            updater.ConnectionTint = BasisPanelTint.Capture(connectionField);
            updater.PingTint = BasisPanelTint.Capture(pingField);
            updater.BandwidthTint = BasisPanelTint.Capture(bandwidthField);
            updater.ConnectionField = connectionField;
            updater.ServerField = serverField;
            updater.PingField = pingField;
            updater.PlayersField = playersField;
            updater.TransmissionField = transmissionField;
            updater.BandwidthField = bandwidthField;
            updater.MetaField = metaField;
        }
    }
}
