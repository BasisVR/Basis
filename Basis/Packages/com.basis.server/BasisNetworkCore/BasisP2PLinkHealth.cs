namespace Basis.Network.Core
{
    /// <summary>
    /// Pure decision logic for BasisP2PManager's direct-link (P2P) health watchdog — the part that
    /// decides when a Connected direct link has gone dead / one-way and must fall back to the server
    /// relay so voice and avatar don't freeze. Extracted from the client (which is Unity-only and can't
    /// be unit-tested headless) so the thresholds and their ordering can be pinned by tests.
    ///
    /// This is the mechanism that makes a direct-connected pair recover after one of them rejoins:
    /// the rejoiner's old socket goes silent, no inbound P2P arrives, and the link is demoted back to
    /// the server relay. If this logic (or the timer that drives it) is wrong, the pair stays routed
    /// over a dead direct link and can't hear each other — the reported symptom.
    /// </summary>
    public static class BasisP2PLinkHealth
    {
        public enum ConnectedVerdict : byte
        {
            /// <summary>Link looks healthy (or still inside the post-connect settle window); leave it Connected.</summary>
            Healthy,
            /// <summary>No inbound P2P traffic for too long — the path is dead/one-way; demote to the server relay.</summary>
            DemoteStale,
            /// <summary>Reached Connected but the server never confirmed the offload — the peer never fully came up; demote.</summary>
            DemoteUnconfirmed,
            /// <summary>Healthy for a long continuous dwell; clear the connect-&gt;die-&gt;reconnect flap counter.</summary>
            ClearFlapCounter,
        }

        /// <summary>
        /// Decide what to do with a Connected direct link this tick.
        /// A live peer P2P-broadcasts its avatar many times a second even when standing still, so an
        /// absence of inbound past <paramref name="staleMs"/> (after the settle grace) means the path
        /// is dead. Staleness is checked before the never-confirmed case so a demotion reports the more
        /// specific reason.
        /// </summary>
        /// <param name="connectedAgeMs">How long the session has been Connected.</param>
        /// <param name="sinceInboundMs">Time since the last inbound P2P packet on this link.</param>
        /// <param name="offloadConfirmed">Whether the server confirmed both sides are up (P2PSub_Offloaded).</param>
        /// <param name="hasFlapCounter">Whether a non-zero connect-&gt;die-&gt;reconnect flap counter is pending.</param>
        public static ConnectedVerdict EvaluateConnected(
            long connectedAgeMs,
            long sinceInboundMs,
            bool offloadConfirmed,
            bool hasFlapCounter,
            long graceMs,
            long staleMs,
            long confirmMs,
            long healthyDwellMs)
        {
            // Settle window right after connecting — never demote yet (a fresh link hasn't had time
            // to prove liveness or to receive the server's offload confirmation).
            if (connectedAgeMs < graceMs)
                return ConnectedVerdict.Healthy;

            if (sinceInboundMs > staleMs)
                return ConnectedVerdict.DemoteStale;

            if (!offloadConfirmed && connectedAgeMs > confirmMs)
                return ConnectedVerdict.DemoteUnconfirmed;

            if (connectedAgeMs > healthyDwellMs && hasFlapCounter)
                return ConnectedVerdict.ClearFlapCounter;

            return ConnectedVerdict.Healthy;
        }

        /// <summary>
        /// A punch / re-punch that has been in flight past <paramref name="punchTimeoutMs"/> is stuck and
        /// should be retried (re-armed) instead of hanging in Punching/Reconnecting forever.
        /// </summary>
        public static bool PunchStalled(long punchAgeMs, long punchTimeoutMs)
            => punchAgeMs > punchTimeoutMs;
    }
}
