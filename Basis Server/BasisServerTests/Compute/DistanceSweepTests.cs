using System;
using System.Collections.Generic;
using System.Diagnostics;
using Basis.Network.Core;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using Vector3 = Basis.Scripts.Networking.Compression.Vector3;

namespace BasisServerTests;

/// <summary>
/// The CPU distance sweep, checked against the scalar math whose results it caches.
///
/// <para>The sweep runs its inner loop a vector at a time and encodes the interval byte without
/// calling the protocol encoder, so these tests pin both: the cache a full sweep leaves behind
/// against <c>BasisNetworkCommons</c> plus the documented tier thresholds, and the vector encoding
/// against the protocol encoder over its whole input domain.</para>
/// </summary>
public sealed class DistanceSweepTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(33)]
    [InlineData(64)]
    public void SweepCachesWhatTheScalarMathWouldProduce(int players)
    {
        (int id, PlayerState state)[] roster = BuildRoster(players,
            i => new Vector3(((i % 4) * 15f) + ((i / 4) * 0.75f), (i % 5) * 0.5f, (i % 7) * 1.3f));

        BasisServerReductionSystemEvents.TestOnly_RunDistanceSweep(roster);

        AssertCacheMatchesScalarMath(roster);
    }

    [Fact]
    public void QualityTiersAreInclusiveAtTheirBoundaries()
    {
        Assert.Equal(100f, BasisServerReductionSystemEvents.HighDistanceSq);
        Assert.Equal(900f, BasisServerReductionSystemEvents.MediumDistanceSq);
        Assert.Equal(2500f, BasisServerReductionSystemEvents.LowDistanceSq);

        float[] onBoundary = { 0f, 10f, 30f, 50f, 60f };
        (int id, PlayerState state)[] roster = BuildRoster(16,
            i => new Vector3(i < onBoundary.Length ? onBoundary[i] : 100f + i * 40f, 0f, 0f));

        BasisServerReductionSystemEvents.TestOnly_RunDistanceSweep(roster);

        PeerTrackingData[] fromOrigin = roster[0].state.PeerTracking;
        Assert.Equal(3, fromOrigin[roster[1].id].CachedQualityIndex);
        Assert.Equal(2, fromOrigin[roster[2].id].CachedQualityIndex);
        Assert.Equal(1, fromOrigin[roster[3].id].CachedQualityIndex);
        Assert.Equal(0, fromOrigin[roster[4].id].CachedQualityIndex);

        AssertCacheMatchesScalarMath(roster);
    }

    [Fact]
    public void VectorIntervalEncodingMatchesTheProtocol()
    {
        foreach (int baseIntervalMs in new[] { 20, 33, 50, 100 })
        {
            int limit = baseIntervalMs + BasisNetworkCommons.AvatarIntervalExtendedStart
                + (byte.MaxValue - BasisNetworkCommons.AvatarIntervalExtendedStart) * BasisNetworkCommons.AvatarIntervalExtendedStepMs
                + BasisNetworkCommons.AvatarIntervalExtendedStepMs;

            List<int> rawIntervals = new List<int> { int.MinValue, int.MinValue + 1, -1 };
            for (int ms = 0; ms <= limit; ms++) rawIntervals.Add(ms);
            rawIntervals.Add(int.MaxValue - 1);
            rawIntervals.Add(int.MaxValue);

            int[] raw = rawIntervals.ToArray();
            int[] encoded = new int[raw.Length];
            int[] actualMs = new int[raw.Length];
            BasisServerReductionSystemEvents.TestOnly_EncodeAvatarIntervals(raw, baseIntervalMs, encoded, actualMs);

            for (int i = 0; i < raw.Length; i++)
            {
                byte expectedByte = BasisNetworkCommons.EncodeAvatarIntervalByte(raw[i], baseIntervalMs);
                Assert.InRange(encoded[i], 0, byte.MaxValue);
                Assert.Equal(expectedByte, (byte)encoded[i]);
                Assert.Equal(BasisNetworkCommons.DecodeAvatarIntervalMs(expectedByte, baseIntervalMs), actualMs[i]);
            }
        }
    }

    private static void AssertCacheMatchesScalarMath((int id, PlayerState state)[] roster)
    {
        int baseIntervalMs = BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval;
        float baseMultiplier = BasisServerReductionSystemEvents.BSRBaseMultiplier;
        float increaseRate = BasisServerReductionSystemEvents.BSRSIncreaseRate;
        double msToTick = Stopwatch.Frequency / 1000.0;

        for (int i = 0; i < roster.Length; i++)
        {
            (int id, PlayerState state) = roster[i];
            for (int j = 0; j < roster.Length; j++)
            {
                (int otherId, PlayerState other) = roster[j];
                if (id == otherId) continue;

                float dx = state.Position.x - other.Position.x;
                float dy = state.Position.y - other.Position.y;
                float dz = state.Position.z - other.Position.z;
                float distSq = dx * dx + dy * dy + dz * dz;

                int rawInterval = (int)(baseIntervalMs * (baseMultiplier + (distSq * increaseRate)));
                byte expectedByte = BasisNetworkCommons.EncodeAvatarIntervalByte(rawInterval, baseIntervalMs);
                int expectedMs = BasisNetworkCommons.DecodeAvatarIntervalMs(expectedByte, baseIntervalMs);

                PeerTrackingData cached = state.PeerTracking[otherId];
                Assert.Equal(expectedByte, cached.CachedIntervalByte);
                Assert.Equal(ExpectedQuality(distSq), cached.CachedQualityIndex);
                Assert.True(cached.HasDistanceCache, "the sweep must mark the pair as cached, or the send loop paces it off the base interval");
                // The pair's tick count used to be a fourth field on the record. It was removed because
                // it was exactly this lookup, so this is the assertion that keeps that true: if the byte
                // ever stopped decoding to the interval the sweep measured, the send loop would pace
                // every pair off the wrong number and nothing else would notice.
                Assert.Equal((int)(expectedMs * msToTick),
                    BasisServerReductionSystemEvents.EnsureIntervalTickTable()[cached.CachedIntervalByte]);
            }
        }
    }

    private static byte ExpectedQuality(float distSq)
    {
        if (distSq <= BasisServerReductionSystemEvents.HighDistanceSq) return 3;
        if (distSq <= BasisServerReductionSystemEvents.MediumDistanceSq) return 2;
        if (distSq <= BasisServerReductionSystemEvents.LowDistanceSq) return 1;
        return 0;
    }

    private static (int id, PlayerState state)[] BuildRoster(int players, Func<int, Vector3> position)
    {
        (int id, PlayerState state)[] roster = new (int, PlayerState)[players];
        int maxId = 0;
        for (int i = 0; i < players; i++)
        {
            int id = (i * 3) + 1;
            if (id > maxId) maxId = id;
            roster[i] = (id, new PlayerState { IsActive = true, Position = position(i) });
        }

        foreach ((int _, PlayerState state) in roster)
        {
            state.PeerTracking = new PeerTrackingData[maxId + 1];
        }
        return roster;
    }
}
