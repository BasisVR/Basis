using Unity.Scripting.LifecycleManagement;
using System;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Byte-rate governor for image replication. Two resources are metered separately because one packet
    /// spends them at different rates: uplink tokens cover what this client writes to sockets — one copy
    /// for the relay however many players it reaches, plus one copy per directly connected peer — while
    /// relay tokens cover what the server forwards on this client's behalf, which is the packet once per
    /// relayed recipient. Peers on a direct link never touch the relay bucket, so an instance that is
    /// fully P2P transfers at the full uplink rate.
    ///
    /// The uplink budget is whatever <see cref="BasisImagePickupLinkProbe"/> currently measures the link
    /// as having spare, reduced by <see cref="BasisImagePickupSettings.ShareBandwidthFraction"/> so pose,
    /// voice, and object sync keep the rest of a line nobody asked this feature to take over.
    ///
    /// The relay budget is whatever the server said it can afford to forward, and it is taken at face
    /// value: an operator who names a figure has already decided what their pipe is worth, so discounting
    /// it again would just mean half of what they asked for. Only the fallback used when a server says
    /// nothing is a guess, and it is a small one.
    /// </summary>
    [AutoStaticsCleanup]
    internal static partial class BasisImagePickupBandwidth
    {
        private static double _uplinkTokens;
        private static double _relayTokens;

        /// <summary>
        /// Server egress budget advertised for this client, in bytes per second, or 0 before a server has
        /// said anything. Refreshed from the join handshake rather than read here so this stays testable
        /// without a live connection.
        /// </summary>
        internal static long ServerRelayBudgetBytesPerSecond;

        internal static double UplinkBytesPerSecond =>
            BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond
            * BasisImagePickupSettings.ShareBandwidthFraction;

        internal static double RelayBytesPerSecond =>
            ServerRelayBudgetBytesPerSecond > 0
                ? ServerRelayBudgetBytesPerSecond
                : BasisImagePickupSettings.RelayEgressBudgetBytesPerSecond;

        internal static double UplinkCapacityBytes =>
            UplinkBytesPerSecond * BasisImagePickupSettings.ShareBandwidthBurstSeconds;

        internal static double RelayCapacityBytes =>
            RelayBytesPerSecond * BasisImagePickupSettings.ShareBandwidthBurstSeconds;

        internal static double UplinkTokens => _uplinkTokens;
        internal static double RelayTokens => _relayTokens;

        /// <summary>
        /// Returns both buckets to full. Runs at arm/teardown so a session starts with its burst. The
        /// advertised relay budget is dropped with them: it describes the server just left, and carrying a
        /// generous one into an instance that advertises nothing would spend a pipe that is not there.
        /// </summary>
        internal static void Reset()
        {
            ServerRelayBudgetBytesPerSecond = 0;
            _uplinkTokens = UplinkCapacityBytes;
            _relayTokens = RelayCapacityBytes;
        }

        /// <summary>
        /// Credits one tick of budget, clamped to <see cref="BasisImagePickupSettings.ShareBandwidthBurstSeconds"/>
        /// so a long stall — a level load, an alt-tab, a breakpoint — cannot bank seconds of credit and then
        /// release them as one spike.
        /// </summary>
        internal static void Refill(float deltaSeconds)
        {
            if (!(deltaSeconds > 0f))
                return;

            _uplinkTokens = Math.Min(
                UplinkCapacityBytes,
                _uplinkTokens + UplinkBytesPerSecond * deltaSeconds
            );
            _relayTokens = Math.Min(
                RelayCapacityBytes,
                _relayTokens + RelayBytesPerSecond * deltaSeconds
            );
        }

        /// <summary>
        /// Charges one packet against both buckets and reports whether it may be sent now. A packet costing
        /// more than the remaining budget still goes out while the buckets are positive and leaves them in
        /// deficit until the refill repays it, so a chunk that is larger than the burst capacity — a wide
        /// fan-out, a big instance — can never wedge a transfer permanently.
        /// </summary>
        internal static bool TryConsume(int wireBytes, int directRecipients, int relayRecipients)
        {
            if (wireBytes <= 0)
                return true;

            int direct = Math.Max(0, directRecipients);
            int relay = Math.Max(0, relayRecipients);
            if (direct == 0 && relay == 0)
                return true;
            if (_uplinkTokens <= 0d)
                return false;
            if (relay > 0 && _relayTokens <= 0d)
                return false;

            _uplinkTokens -= (double)wireBytes * direct + (relay > 0 ? wireBytes : 0d);
            _relayTokens -= (double)wireBytes * relay;
            return true;
        }
    }
}
