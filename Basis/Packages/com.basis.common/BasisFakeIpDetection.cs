using Unity.Scripting.LifecycleManagement;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// Recognises a Fake-IP resolver on this machine. Clash and sing-box in TUN mode answer every
    /// DNS query with a synthetic address out of the RFC 2544 benchmarking range and map it back to
    /// the real domain inside the proxy, so an ordinary public CDN host looks non-global-unicast to
    /// <see cref="BasisUrlSecurity.ValidateResolvedHostAsync"/> and every download is refused.
    ///
    /// <para>This is a <b>diagnostic</b>, never a permission. Nothing here can open a range: the
    /// gate is opened only by <see cref="BasisUrlSecurity.AllowBenchmarkRangeFromDns"/>, which the
    /// player sets. All this decides is whether it is worth offering them that switch, so a false
    /// negative costs an unprompted player (who can still find the setting) and a false positive
    /// costs one declined dialog.</para>
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisFakeIpDetection
    {
        /// <summary>
        /// True for the RFC 2544 benchmarking range, 198.18.0.0/15 — Clash's and sing-box's default
        /// Fake-IP pool, and the only range the compatibility switch ever opens. Unwraps an
        /// IPv4-mapped IPv6 address so the same literal cannot walk in through the v6 family.
        /// </summary>
        public static bool IsBenchmarkRange(IPAddress ip)
        {
            if (ip == null) return false;
            byte[] b = ip.GetAddressBytes();

            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && b.Length == 16)
            {
                for (int i = 0; i < 10; i++) if (b[i] != 0) return false;
                if (b[10] != 0xFF || b[11] != 0xFF) return false;
                return b[12] == 198 && (b[13] & 0xFE) == 18;
            }

            return ip.AddressFamily == AddressFamily.InterNetwork && b.Length == 4
                && b[0] == 198 && (b[1] & 0xFE) == 18;
        }

        /// <summary>
        /// Detection is only consulted after a download has already been refused, so it can afford
        /// to be slow — but it must not stall the refusal. A resolver that will not answer inside
        /// this budget is not the fast local one a TUN proxy runs.
        /// </summary>
        private const int ProbeTimeoutMs = 2000;

        /// <summary>
        /// Re-probe cadence. Long enough that a burst of blocked downloads costs one probe, short
        /// enough that toggling the VPN mid-session is noticed without a restart.
        /// </summary>
        private static readonly long CacheTicks = Stopwatch.Frequency * 30;

        [NoAutoStaticsCleanup] private static readonly object Gate = new object();
        [NoAutoStaticsCleanup] private static Task<bool> Probe;
        private static long ProbeStamp;

        /// <summary>
        /// True when a Fake-IP resolver appears to be running on this machine. Cached briefly and
        /// shared between concurrent callers, so a stalled world load costs one probe, not one per
        /// refused file.
        /// </summary>
        public static Task<bool> IsResolverActiveAsync()
        {
            lock (Gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (Probe == null || now - ProbeStamp > CacheTicks)
                {
                    ProbeStamp = now;
                    Probe = RunDetectionAsync();
                }
                return Probe;
            }
        }

        private static async Task<bool> RunDetectionAsync()
        {
            // Clash's TUN adapter carries 198.18.0.1/16 by default, which settles it without any
            // DNS. sing-box numbers its TUN out of 172.19/16 while still allocating fake IPs from
            // 198.18/15, so a miss here is not an answer — fall through to the probe.
            if (HasLocalAddressInBenchmarkRange()) return true;

            // A Fake-IP resolver allocates an address for a name before anything resolves it for
            // real, so a name that cannot exist still gets an answer — out of the same pool real
            // names get. Two independent random names: a resolver that hands out a *different*
            // public address each time (NXDOMAIN hijacking) is not synthesising a pool.
            return await ProbeAsync() && await ProbeAsync();
        }

        private static bool HasLocalAddressInBenchmarkRange()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                        if (IsBenchmarkRange(info.Address)) return true;
                }
            }
            catch (Exception)
            {
                // Interface enumeration is not permitted on every platform (Android); the probe
                // still answers.
            }
            return false;
        }

        /// <summary>
        /// Resolves one name that cannot legitimately exist and reports whether the answer landed in
        /// the Fake-IP pool. A resolver that refuses it — the correct answer — reports false.
        /// </summary>
        private static async Task<bool> ProbeAsync()
        {
            // An unregistered label under a real TLD. The RFC 6761 names (.invalid, .test, .example)
            // would be the obvious pick, but Clash's default fake-ip-filter excludes them, which is
            // exactly the answer that must not come back.
            string name = Guid.NewGuid().ToString("N") + ".com";

            IPAddress[] addresses;
            try
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(name);
                if (await Task.WhenAny(lookup, BasisTasks.Delay(ProbeTimeoutMs)) != lookup) return false;
                addresses = await lookup;
            }
            catch (Exception)
            {
                return false;
            }

            if (addresses == null) return false;
            foreach (IPAddress ip in addresses)
                if (IsBenchmarkRange(ip)) return true;
            return false;
        }
    }
}
