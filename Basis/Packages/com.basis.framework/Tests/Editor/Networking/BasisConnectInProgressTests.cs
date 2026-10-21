using System.Threading.Tasks;
using Basis.Scripts.Networking;
using NUnit.Framework;

public class BasisConnectInProgressTests
{
    private bool wasConnected;

    [SetUp]
    public void SetUp()
    {
        wasConnected = BasisNetworkConnection.LocalPlayerIsConnected;
        Assume.That(BasisNetworkConnectionWatchdog.Supported, Is.True);
        Assume.That(BasisNetworkConnection.LocalPlayerPeer, Is.Null);
        Assume.That(BasisNetworkConnectionWatchdog.Health, Is.EqualTo(BasisConnectionHealth.Idle));
        BasisNetworkConnection.LocalPlayerIsConnected = false;
    }

    [TearDown]
    public void TearDown()
    {
        BasisNetworkConnectionWatchdog.NotifyConnectAborted();
        BasisNetworkConnection.LocalPlayerIsConnected = wasConnected;
    }

    [Test]
    public void AHandshakeStillInFlightCountsAsAConnectInProgress()
    {
        Assert.That(BasisConnectionService.ConnectInProgress, Is.False);

        BasisNetworkConnectionWatchdog.NotifyHandshakeStarted();

        Assert.That(BasisConnectionService.ClientConnecting, Is.True);
        Assert.That(BasisConnectionService.ConnectInProgress, Is.True);

        BasisNetworkConnectionWatchdog.NotifyConnectAborted();

        Assert.That(BasisConnectionService.ConnectInProgress, Is.False);
    }

    [Test]
    public void ASecondConnectDuringTheHandshakeIsTurnedAway()
    {
        BasisNetworkConnectionWatchdog.NotifyHandshakeStarted();

        Task second = BasisConnectionService.ConnectAsync(null, "zz");

        Assert.That(second.IsCompleted, Is.True);
        Assert.That(second.IsFaulted, Is.False);
        Assert.That(BasisNetworkConnectionWatchdog.Health, Is.EqualTo(BasisConnectionHealth.Connecting), "the attempt already in flight is left alone");
    }

    [Test]
    public void AConnectedSessionIsNotAHandshake()
    {
        BasisNetworkConnection.LocalPlayerIsConnected = true;
        BasisNetworkConnectionWatchdog.NotifyHandshakeStarted();

        Assert.That(BasisConnectionService.ClientConnecting, Is.False);
    }
}
