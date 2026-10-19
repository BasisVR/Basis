using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using Unity.Mathematics;

/// <summary>
/// BasisNetworkPlayers.ReceiversSnapshot is a ConcurrentDictionary enumeration, so a join or a leave
/// re-orders the surviving players. BasisRemoteFaceManagement keeps the idle look-around and blink
/// state in arrays indexed by that position, so before the driver-keyed remap a mass join/leave handed
/// every re-ordered remote somebody else's eye pose — a hard eye snap per membership change.
/// </summary>
[NoAutoStaticsCleanup]
public class BasisRemoteFaceSlotRemapTests
{
    BasisNetworkReceiver[] savedSnapshot;
    int savedCount;
    uint savedVersion;

    [SetUp]
    public void SetUp()
    {
        savedSnapshot = BasisNetworkPlayers.ReceiversSnapshot;
        savedCount = BasisNetworkPlayers.ReceiverCount;
        savedVersion = BasisNetworkPlayers.SnapshotVersion;
        BasisRemoteFaceManagement.Dispose();
        BasisRemoteFaceManagement.HasJob = false;
    }

    [TearDown]
    public void TearDown()
    {
        BasisRemoteFaceManagement.Dispose();
        BasisRemoteFaceManagement.HasJob = false;
        BasisNetworkPlayers.ReceiversSnapshot = savedSnapshot;
        BasisNetworkPlayers.ReceiverCount = savedCount;
        BasisNetworkPlayers.SnapshotVersion = savedVersion;
    }

    static ushort nextPlayerId;

    static BasisNetworkReceiver MakeReceiver(string name)
    {
        return new BasisNetworkReceiver(++nextPlayerId)
        {
            RemotePlayer = new BasisRemotePlayer
            {
                DisplayName = name,
                UUID = System.Guid.NewGuid().ToString("N"),
            },
        };
    }

    static void Publish(params BasisNetworkReceiver[] order)
    {
        BasisNetworkPlayers.ReceiversSnapshot = order;
        BasisNetworkPlayers.ReceiverCount = order.Length;
        BasisNetworkPlayers.SnapshotVersion++;
    }

    static void Tick(double time)
    {
        BasisRemoteFaceManagement.Simulate(time, 0.016f);
        BasisRemoteFaceManagement.Apply();
    }

    // Parks the slot so neither the look-around nor the blink timer fires during the next tick,
    // leaving the marker the only thing that can move.
    static void StampSlot(int slot, float marker)
    {
        BasisRemoteFaceManagement.EyeState eye = BasisRemoteFaceManagement.eyeStates[slot];
        eye.isLooking = 0;
        eye.nextLookAroundTime = 1000.0;
        eye.target = new float2(marker, marker);
        BasisRemoteFaceManagement.eyeStates[slot] = eye;

        BasisRemoteFaceManagement.BlinkState blink = BasisRemoteFaceManagement.blinkStates[slot];
        blink.nextBlinkTime = 1000.0;
        blink.isClosing = 0;
        blink.isOpening = 0;
        BasisRemoteFaceManagement.blinkStates[slot] = blink;

        BasisRemoteFaceManagement.eyeOut[slot] = new BasisRemoteFaceManagement.EyeOutput
        {
            vL = marker,
            hL = marker,
            vR = marker,
            hR = marker,
        };
    }

    static void AssertSlotMarker(int slot, float expected, string who)
    {
        Assert.AreEqual(expected, BasisRemoteFaceManagement.eyeOut[slot].hL, 1e-4f, $"{who}'s eye pose must follow it to slot {slot}");
        Assert.AreEqual(expected, BasisRemoteFaceManagement.eyeOut[slot].vR, 1e-4f, $"{who}'s eye pose must follow it to slot {slot}");
        Assert.AreEqual(expected, BasisRemoteFaceManagement.eyeStates[slot].target.x, 1e-4f, $"{who}'s look target must follow it to slot {slot}");
    }

    [Test]
    public void SnapshotReorder_CarriesEyeStateWithTheDriver()
    {
        BasisNetworkReceiver a = MakeReceiver("A");
        BasisNetworkReceiver b = MakeReceiver("B");
        BasisNetworkReceiver c = MakeReceiver("C");

        Publish(a, b, c);
        Tick(0.0);

        StampSlot(0, 0.11f);
        StampSlot(1, 0.22f);
        StampSlot(2, 0.33f);

        // The shape a join/leave produces: same players, different enumeration order.
        Publish(c, a, b);
        Tick(0.02);

        AssertSlotMarker(0, 0.33f, "C");
        AssertSlotMarker(1, 0.11f, "A");
        AssertSlotMarker(2, 0.22f, "B");
    }

    [Test]
    public void JoiningPlayer_DoesNotInheritTheDepartedOccupantsEyeState()
    {
        BasisNetworkReceiver a = MakeReceiver("A");
        BasisNetworkReceiver b = MakeReceiver("B");
        BasisNetworkReceiver c = MakeReceiver("C");

        Publish(a, b, c);
        Tick(0.0);

        StampSlot(0, 0.11f);
        StampSlot(1, 0.22f);
        StampSlot(2, 0.33f);

        Publish(a, b);
        Tick(0.02);

        BasisNetworkReceiver d = MakeReceiver("D");
        Publish(a, b, d);
        Tick(0.04);

        AssertSlotMarker(0, 0.11f, "A");
        AssertSlotMarker(1, 0.22f, "B");
        Assert.AreEqual(0f, BasisRemoteFaceManagement.eyeOut[2].hL, 1e-4f, "D must start from its own networked eye values, not C's leftovers");
        Assert.AreEqual(0f, BasisRemoteFaceManagement.eyeOut[2].vR, 1e-4f, "D must start from its own networked eye values, not C's leftovers");
    }

    [Test]
    public void SnapshotReorderWithAJoin_KeepsEverySurvivorsEyeState()
    {
        BasisNetworkReceiver a = MakeReceiver("A");
        BasisNetworkReceiver b = MakeReceiver("B");
        BasisNetworkReceiver c = MakeReceiver("C");

        Publish(a, b, c);
        Tick(0.0);

        StampSlot(0, 0.11f);
        StampSlot(1, 0.22f);
        StampSlot(2, 0.33f);

        BasisNetworkReceiver d = MakeReceiver("D");
        Publish(b, d, c, a);
        Tick(0.02);

        AssertSlotMarker(0, 0.22f, "B");
        Assert.AreEqual(0f, BasisRemoteFaceManagement.eyeOut[1].hL, 1e-4f, "the joining player is seeded, not inherited");
        AssertSlotMarker(2, 0.33f, "C");
        AssertSlotMarker(3, 0.11f, "A");
    }
}
