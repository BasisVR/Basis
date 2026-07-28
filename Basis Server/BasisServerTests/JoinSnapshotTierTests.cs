using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using Vector3 = Basis.Scripts.Networking.Compression.Vector3;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// The join fill used to hand every joiner a High payload for every player in the instance, however
/// far away. These pin that it now picks the same tier the steady-state send loop would — and, just as
/// important, that it falls back to High rather than sending an empty payload whenever the decision
/// cannot be made safely.
/// </summary>
[Collection("BasisServer shared network statics")]
public class JoinSnapshotTierTests
{
    static byte[] Payload(BitQuality q, byte fill)
    {
        byte[] a = new byte[BasisAvatarBitPacking.ConvertToSize(q)];
        for (int i = 0; i < a.Length; i++) a[i] = fill;
        return a;
    }

    /// <summary>Registers a subject at a position with all four tiers built, as the tick loop would.</summary>
    static PlayerState Subject(int id, Vector3 position, bool withLowerTiers = true, bool bypass = false)
    {
        var state = new PlayerState
        {
            IsActive = true,
            Position = position,
            BypassReduction = bypass,
            SyncMessage = new ServerSideSyncPlayerMessage
            {
                avatarSerialization = new LocalAvatarSyncMessage
                {
                    DataQualityLevel = (byte)BitQuality.High,
                    array = Payload(BitQuality.High, 0xAA),
                },
            },
        };

        if (withLowerTiers)
        {
            state.AvatarMedium = new LocalAvatarSyncMessage { DataQualityLevel = (byte)BitQuality.Medium, array = Payload(BitQuality.Medium, 0xBB) };
            state.AvatarLow = new LocalAvatarSyncMessage { DataQualityLevel = (byte)BitQuality.Low, array = Payload(BitQuality.Low, 0xCC) };
            state.AvatarVeryLow = new LocalAvatarSyncMessage { DataQualityLevel = (byte)BitQuality.VeryLow, array = Payload(BitQuality.VeryLow, 0xDD) };
        }

        BasisServerReductionSystemEvents.playerStates[id] = state;
        return state;
    }

    // 5m -> High(<=10), 20m -> Medium(<=30), 40m -> Low(<=50), 500m -> VeryLow
    [Theory]
    [InlineData(5f, BitQuality.High)]
    [InlineData(20f, BitQuality.Medium)]
    [InlineData(40f, BitQuality.Low)]
    [InlineData(500f, BitQuality.VeryLow)]
    public void Tier_MatchesTheSteadyStateDistanceThresholds(float metres, BitQuality expected)
    {
        const int id = 61001;
        try
        {
            Subject(id, new Vector3 { x = metres, y = 0f, z = 0f });

            Assert.True(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, id, out var snapshot));
            Assert.Equal((byte)expected, snapshot.DataQualityLevel);
            Assert.Equal(BasisAvatarBitPacking.ConvertToSize(expected), snapshot.array.Length);
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }

    /// <summary>
    /// The whole point: at crowd scale nearly everyone is past the VeryLow threshold, so the join
    /// payload should be less than half what it used to be for those players.
    /// </summary>
    [Fact]
    public void DistantPlayer_CostsFarLessThanTheOldHighPayload()
    {
        const int id = 61002;
        try
        {
            Subject(id, new Vector3 { x = 400f, y = 0f, z = 0f });

            Assert.True(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, id, out var snapshot));
            int high = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            Assert.True(snapshot.array.Length * 2 < high,
                $"expected well under half of {high}B, got {snapshot.array.Length}B");
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }

    /// <summary>
    /// Distance is measured between the two players, not from the origin — a joiner standing next to
    /// someone far from spawn must still get High for them.
    /// </summary>
    [Fact]
    public void TierIsRelativeToTheViewer_NotTheOrigin()
    {
        const int id = 61003;
        try
        {
            Subject(id, new Vector3 { x = 500f, y = 0f, z = 0f });

            var beside = new Vector3 { x = 502f, y = 0f, z = 0f };
            Assert.True(BasisServerReductionSystemEvents.TryGetJoinSnapshot(beside, id, out var snapshot));
            Assert.Equal((byte)BitQuality.High, snapshot.DataQualityLevel);
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }

    /// <summary>
    /// BuildAllLowerFromHighInto nulls the lower arrays when a repack fails, and the tick may not have
    /// built them yet for a player who just connected. Sending that as-is would be an empty avatar.
    /// </summary>
    [Fact]
    public void MissingLowerTier_FallsBackToHighRatherThanAnEmptyPayload()
    {
        const int id = 61004;
        try
        {
            Subject(id, new Vector3 { x = 500f, y = 0f, z = 0f }, withLowerTiers: false);

            Assert.True(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, id, out var snapshot));
            Assert.Equal((byte)BitQuality.High, snapshot.DataQualityLevel);
            Assert.NotNull(snapshot.array);
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }

    [Fact]
    public void BypassReduction_AlwaysGetsHigh()
    {
        const int id = 61005;
        try
        {
            Subject(id, new Vector3 { x = 5000f, y = 0f, z = 0f }, bypass: true);

            Assert.True(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, id, out var snapshot));
            Assert.Equal((byte)BitQuality.High, snapshot.DataQualityLevel);
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }

    [Fact]
    public void UnknownSubject_ReportsNoSnapshot()
    {
        Assert.False(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, 61999, out _));
    }

    [Fact]
    public void SubjectWithNoPoseYet_ReportsNoSnapshot()
    {
        const int id = 61006;
        try
        {
            BasisServerReductionSystemEvents.playerStates[id] = new PlayerState
            {
                IsActive = true,
                Position = default,
                SyncMessage = new ServerSideSyncPlayerMessage { avatarSerialization = default },
            };

            Assert.False(BasisServerReductionSystemEvents.TryGetJoinSnapshot(default, id, out _));
        }
        finally
        {
            BasisServerReductionSystemEvents.RemovePlayer(id);
        }
    }
}
