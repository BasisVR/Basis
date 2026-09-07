using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Regression coverage for a gap found alongside the memory-reclaim bug on 2026-09-08: production
/// logs show <c>RcvbufErrors</c> firing on four separate days and new sockets genuinely being
/// bound at runtime ("Added a send socket (N now)") as the auto-growth mitigation reacted to load
/// — yet the specific, actionable "OS clamped the socket buffers" line never appeared once in 18
/// days. A one-shot static latch meant it could only ever log at the very first socket bind of the
/// process's lifetime, long before anyone was watching. See <see cref="MemoryReclaimTests"/> for
/// the companion bug this same incident window uncovered.
/// </summary>
public class SocketBufferClampWarningTests
{
    private sealed class CapturingLogger : INetLogger
    {
        public List<string> Messages { get; } = new();
        public void WriteNet(NetLogLevel level, string str, params object[] args) =>
            Messages.Add(args.Length == 0 ? str : string.Format(str, args));
    }

    [Fact]
    public void WarnsOnEveryClampedSocketNotJustTheFirst()
    {
        var logger = new CapturingLogger();
        INetLogger previous = NetDebug.Logger;
        NetDebug.Logger = logger;
        try
        {
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            first.ReceiveBufferSize = 8192;
            first.SendBufferSize = 8192;
            NetManager.WarnIfSocketBuffersWereClamped(first);

            // A second socket bound later in the process's life — e.g. the multi-socket-growth
            // mitigation adding capacity mid-incident — is exactly as clamped and must warn again.
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            second.ReceiveBufferSize = 8192;
            second.SendBufferSize = 8192;
            NetManager.WarnIfSocketBuffersWereClamped(second);
        }
        finally
        {
            NetDebug.Logger = previous;
        }

        Assert.Equal(2, logger.Messages.Count(m => m.Contains("clamped the socket buffers")));
    }
}
