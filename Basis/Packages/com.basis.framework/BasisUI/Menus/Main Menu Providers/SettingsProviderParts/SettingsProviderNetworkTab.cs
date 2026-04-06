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
            euroGroup.SetTitle("Network Euro Filter");
            euroGroup.SetDescription("One Euro filter parameters for remote player interpolation.");

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced("Min Cutoff", 0.01f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroMinCutoff);

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced("Beta", 0f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroBeta);

            PanelSlider.CreateEntryAndBind(
                euroGroup,
                PanelSlider.SliderSettings.Advanced("Derivative Cutoff", 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetEuroDerivativeCutoff);
        }

        public static void BuildNetworkStatsGroup(RectTransform container, out NetworkStatsPanelUpdater updater)
        {
            var netGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            netGroup.SetTitle("Network & Statistics");
            netGroup.SetDescription("Live connection and transmission diagnostics.");

            // Connection status
            var connectionField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            connectionField.SetTitle("Connection");
            connectionField.SetDescription("...");

            // Server info
            var serverField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            serverField.SetTitle("Server");
            serverField.SetDescription("...");

            // Ping / RTT
            var pingField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            pingField.SetTitle("Ping / RTT");
            pingField.SetDescription("...");

            // Players
            var playersField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            playersField.SetTitle("Players");
            playersField.SetDescription("...");

            // Transmission
            var transmissionField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            transmissionField.SetTitle("Transmission");
            transmissionField.SetDescription("...");

            // Bandwidth
            var bandwidthField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            bandwidthField.SetTitle("Bandwidth");
            bandwidthField.SetDescription("...");

            // Server Metadata
            var metaField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, netGroup.ContentParent);
            metaField.SetTitle("Server Metadata");
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
