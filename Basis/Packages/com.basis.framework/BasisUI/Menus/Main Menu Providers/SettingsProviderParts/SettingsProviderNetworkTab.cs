using Basis.Scripts.Networking;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderNetworkTab
    {
        [RuntimeInitializeOnLoadMethod]
        static void Init()
        {
            // Seed the static fields from persisted settings
            BasisNetworkManagement.MinCutoff = BasisSettingsDefaults.NetEuroMinCutoff.RawValue;
            BasisNetworkManagement.Beta = BasisSettingsDefaults.NetEuroBeta.RawValue;
            BasisNetworkManagement.DerivativeCutoff = BasisSettingsDefaults.NetEuroDerivativeCutoff.RawValue;

            // Keep them in sync when the user changes a setting
            BasisSettingsDefaults.NetEuroMinCutoff.OnChanged += v => BasisNetworkManagement.MinCutoff = v;
            BasisSettingsDefaults.NetEuroBeta.OnChanged += v => BasisNetworkManagement.Beta = v;
            BasisSettingsDefaults.NetEuroDerivativeCutoff.OnChanged += v => BasisNetworkManagement.DerivativeCutoff = v;
        }

        public static void BuildNetworkEuroFilterGroup(RectTransform container)
        {
            var euroGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            euroGroup.SetTitle(BasisLocalization.Get("settings.developer.euroFilter"));
            euroGroup.SetDescription(BasisLocalization.Get("settings.developer.euroFilter.description"));

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.network.euro.minCutoff"), 0.01f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroMinCutoff);

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.network.euro.beta"), 0f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroBeta);

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.network.euro.derivativeCutoff"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroDerivativeCutoff);
        }

        public static void BuildNetworkStatsGroup(RectTransform container, out NetworkStatsPanelUpdater updater)
        {
            var netGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            netGroup.SetTitle(BasisLocalization.Get("settings.developer.netStats"));
            netGroup.SetDescription(BasisLocalization.Get("settings.developer.netStats.description"));

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

            // Create a holder GO for the updater MonoBehaviour
            var holderGO = new GameObject("NetworkStatsUpdater");
            holderGO.transform.SetParent(netGroup.transform, false);
            updater = holderGO.AddComponent<NetworkStatsPanelUpdater>();
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
