using Basis.BasisUI;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The same LOD chain, run at the population the client is actually built for. Every job here
    /// runs once per remote player per tick, so a rule that is merely inefficient at ten players is
    /// a frame-time problem at two thousand, and a rule that is subtly wrong is wrong two thousand
    /// times a frame.
    ///
    /// These check crowd-scale PROPERTIES rather than per-index expected values: farther players
    /// never get better detail, a settled crowd reports no changes at all, the visibility budget is
    /// honoured exactly, and the quickselect behind it still terminates when every player is stood
    /// on the same spawn point (its worst case, and a completely ordinary thing for a crowd to do).
    /// </summary>
    public class BasisCrowdScaleJobTests
    {
        const int Crowd = 2000;
        const float Hysteresis = 1.10f * 1.10f;

        static float3[] SpreadCrowd(int count, float radius, uint seed = 8271)
        {
            Unity.Mathematics.Random random = new Unity.Mathematics.Random(seed);
            float3[] positions = new float3[count];
            for (int index = 0; index < count; index++)
            {
                positions[index] = new float3(
                    random.NextFloat(-radius, radius), 0f, random.NextFloat(-radius, radius));
            }
            return positions;
        }

        sealed class DistancePass : System.IDisposable
        {
            public NativeArray<float3> Targets;
            public NativeArray<bool> PrevMic, PrevHearing, PrevAvatar;
            public NativeArray<short> PrevMeshLod;
            public NativeArray<float> DistanceSq, PerIndexMinD2;
            public NativeArray<short> MeshLod, PoseLod;
            public NativeArray<bool> Mic, Hearing, Avatar, MeshLodChanged, Shouting;
            public NativeArray<int> PerIndexMask;
            public readonly int Count;

            public DistancePass(float3[] positions)
            {
                Count = positions.Length;
                Targets = new NativeArray<float3>(positions, Allocator.Temp);
                PrevMic = new NativeArray<bool>(Count, Allocator.Temp);
                PrevHearing = new NativeArray<bool>(Count, Allocator.Temp);
                PrevAvatar = new NativeArray<bool>(Count, Allocator.Temp);
                PrevMeshLod = new NativeArray<short>(Count, Allocator.Temp);
                DistanceSq = new NativeArray<float>(Count, Allocator.Temp);
                PerIndexMinD2 = new NativeArray<float>(Count, Allocator.Temp);
                MeshLod = new NativeArray<short>(Count, Allocator.Temp);
                PoseLod = new NativeArray<short>(Count, Allocator.Temp);
                Mic = new NativeArray<bool>(Count, Allocator.Temp);
                Hearing = new NativeArray<bool>(Count, Allocator.Temp);
                Avatar = new NativeArray<bool>(Count, Allocator.Temp);
                MeshLodChanged = new NativeArray<bool>(Count, Allocator.Temp);
                Shouting = new NativeArray<bool>(Count, Allocator.Temp);
                PerIndexMask = new NativeArray<int>(Count, Allocator.Temp);
            }

            public void Run(float rangeSq, float reductionMultiplier)
            {
                BasisDistanceJobParallel job = new BasisDistanceJobParallel
                {
                    SquaredVoiceDistance = rangeSq,
                    SquaredHearingDistance = rangeSq,
                    SquaredAvatarDistance = rangeSq,
                    ShoutRangeMultiplierSquared = 1f,
                    RemoteIsShouting = Shouting,
                    HysteresisPercent = Hysteresis,
                    ReductionMultiplier = reductionMultiplier,
                    UseEyeGaze = false,
                    GazeForward = new float3(0f, 0f, 1f),
                    CosHalfGazeCone = 1f,
                    GazeBoostFactor = 1f,
                    referencePosition = float3.zero,
                    targetPositions = Targets,
                    PrevInMicrophoneRange = PrevMic,
                    PrevInHearingRange = PrevHearing,
                    PrevInAvatarRange = PrevAvatar,
                    PrevMeshLodLevel = PrevMeshLod,
                    distanceSq = DistanceSq,
                    MeshLodLevel = MeshLod,
                    PoseLodLevel = PoseLod,
                    MicrophoneRange = Mic,
                    hearingRange = Hearing,
                    AvatarRange = Avatar,
                    MeshLodRange = MeshLodChanged,
                    PerIndexMinD2 = PerIndexMinD2,
                    PerIndexMask = PerIndexMask,
                };
                for (int index = 0; index < Count; index++)
                {
                    job.Execute(index);
                }
            }

            /// <summary>Feeds this tick's outputs back in as next tick's previous state.</summary>
            public void CarryForward()
            {
                for (int index = 0; index < Count; index++)
                {
                    PrevMic[index] = Mic[index];
                    PrevHearing[index] = Hearing[index];
                    PrevAvatar[index] = Avatar[index];
                    PrevMeshLod[index] = MeshLod[index];
                }
            }

            public void Dispose()
            {
                Targets.Dispose();
                PrevMic.Dispose();
                PrevHearing.Dispose();
                PrevAvatar.Dispose();
                PrevMeshLod.Dispose();
                DistanceSq.Dispose();
                PerIndexMinD2.Dispose();
                MeshLod.Dispose();
                PoseLod.Dispose();
                Mic.Dispose();
                Hearing.Dispose();
                Avatar.Dispose();
                MeshLodChanged.Dispose();
                Shouting.Dispose();
                PerIndexMask.Dispose();
            }
        }

        // ── distance pass ─────────────────────────────────────────────────────

        [Test]
        public void DistancePass_AcrossACrowd_NeverGivesAFartherPlayerBetterDetail()
        {
            using DistancePass pass = new DistancePass(SpreadCrowd(Crowd, 150f));
            pass.Run(rangeSq: 60f * 60f, reductionMultiplier: 1f / (150f * 150f));

            for (int a = 0; a < pass.Count; a++)
            {
                for (int b = a + 1; b < pass.Count; b += 37)
                {
                    bool aIsCloser = pass.DistanceSq[a] < pass.DistanceSq[b];
                    if (!aIsCloser) continue;
                    Assert.That((int)pass.MeshLod[a], Is.LessThanOrEqualTo((int)pass.MeshLod[b]),
                        $"index {a} is closer than {b} but got a worse mesh band");
                    Assert.That((int)pass.PoseLod[a], Is.LessThanOrEqualTo((int)pass.PoseLod[b]),
                        $"index {a} is closer than {b} but got a worse pose band");
                }
            }
        }

        [Test]
        public void DistancePass_AcrossACrowd_RangeGatesAgreeWithTheDistances()
        {
            using DistancePass pass = new DistancePass(SpreadCrowd(Crowd, 150f));
            const float rangeSq = 60f * 60f;
            pass.Run(rangeSq, reductionMultiplier: 0f);

            // Nobody was in range last tick, so every gate uses the tight enter threshold.
            for (int index = 0; index < pass.Count; index++)
            {
                bool expected = pass.DistanceSq[index] < rangeSq;
                Assert.That(pass.Avatar[index], Is.EqualTo(expected), $"index {index}");
                Assert.That(pass.Hearing[index], Is.EqualTo(expected), $"index {index}");
                Assert.That(pass.Mic[index], Is.EqualTo(expected), $"index {index}");
            }
        }

        [Test]
        public void ASettledCrowd_ReportsNoChangesAtAll()
        {
            // The single most important scale property in this pass: a room full of people who
            // have not moved must produce an all-zero change mask, or every tick pays a full
            // instance-wide reload of avatars, audio sources and LOD levels.
            using DistancePass pass = new DistancePass(SpreadCrowd(Crowd, 150f));
            const float rangeSq = 60f * 60f;
            const float multiplier = 1f / (150f * 150f);

            pass.Run(rangeSq, multiplier);
            pass.CarryForward();
            pass.Run(rangeSq, multiplier);

            for (int index = 0; index < pass.Count; index++)
            {
                Assert.That(pass.PerIndexMask[index], Is.Zero, $"index {index} changed while standing still");
                Assert.That(pass.MeshLodChanged[index], Is.False, $"index {index} reported a LOD change while standing still");
            }
        }

        [Test]
        public void AtCrowdScale_MostOfTheRoomLandsInACheapBand()
        {
            // If the LOD bands did nothing at scale there would be no point paying for them. In a
            // 150 m instance the overwhelming majority of a crowd is far enough to be cheap.
            using DistancePass pass = new DistancePass(SpreadCrowd(Crowd, 150f));
            pass.Run(rangeSq: 60f * 60f, reductionMultiplier: 1f / (150f * 150f));

            int cheap = 0;
            for (int index = 0; index < pass.Count; index++)
            {
                if (pass.MeshLod[index] >= 2) cheap++;
            }
            Assert.That(cheap, Is.GreaterThan(pass.Count / 2),
                "most of a spread-out crowd should be past the halfway band");
        }

        [Test]
        public void TheNearestPlayerAcrossACrowd_IsFoundByTheReduction()
        {
            using DistancePass pass = new DistancePass(SpreadCrowd(Crowd, 150f));
            pass.Run(rangeSq: 60f * 60f, reductionMultiplier: 0f);

            float expected = float.PositiveInfinity;
            for (int index = 0; index < pass.Count; index++)
            {
                expected = math.min(expected, pass.DistanceSq[index]);
            }

            NativeArray<float> smallest = new NativeArray<float>(1, Allocator.Temp);
            NativeArray<int> changed = new NativeArray<int>(1, Allocator.Temp);
            try
            {
                new BasisDistanceReduceJob
                {
                    ReceiverCount = pass.Count,
                    PerIndexMinD2 = pass.PerIndexMinD2,
                    PerIndexMask = pass.PerIndexMask,
                    SmallestD2 = smallest,
                    ChangeMask = changed,
                }.Execute();

                Assert.That(smallest[0], Is.EqualTo(expected).Within(1e-3f));
            }
            finally
            {
                smallest.Dispose();
                changed.Dispose();
            }
        }

        // ── visibility budget ─────────────────────────────────────────────────

        static (int Kept, bool[] Survived) RunAvatarCap(float[] distances, int maxVisible, bool[] loaded = null)
        {
            NativeArray<float> distanceSq = new NativeArray<float>(distances, Allocator.Temp);
            NativeArray<bool> hasAvatar = new NativeArray<bool>(distances.Length, Allocator.Temp);
            NativeArray<bool> inRange = new NativeArray<bool>(distances.Length, Allocator.Temp);
            NativeArray<AvatarCapEntry> entries = new NativeArray<AvatarCapEntry>(distances.Length, Allocator.Temp);
            try
            {
                for (int index = 0; index < distances.Length; index++)
                {
                    inRange[index] = true;
                    hasAvatar[index] = loaded != null && loaded[index];
                }

                new BasisAvatarCapJob
                {
                    MaxVisible = maxVisible,
                    ReceiverCount = distances.Length,
                    StickinessBonus = 0.75f,
                    DistanceSq = distanceSq,
                    HasRealAvatarLoaded = hasAvatar,
                    AvatarRange = inRange,
                    Entries = entries,
                }.Execute();

                bool[] survived = new bool[distances.Length];
                int kept = 0;
                for (int index = 0; index < distances.Length; index++)
                {
                    survived[index] = inRange[index];
                    if (survived[index]) kept++;
                }
                return (kept, survived);
            }
            finally
            {
                distanceSq.Dispose();
                hasAvatar.Dispose();
                inRange.Dispose();
                entries.Dispose();
            }
        }

        [Test]
        public void VisibilityBudget_AcrossACrowd_KeepsExactlyTheClosestN()
        {
            float[] distances = new float[Crowd];
            for (int index = 0; index < Crowd; index++)
            {
                // A permutation of 1..Crowd, so "the closest 60" is unambiguous.
                distances[index] = ((index * 617) % Crowd) + 1f;
            }

            (int kept, bool[] survived) = RunAvatarCap(distances, maxVisible: 60);

            Assert.That(kept, Is.EqualTo(60));
            for (int index = 0; index < Crowd; index++)
            {
                Assert.That(survived[index], Is.EqualTo(distances[index] <= 60f), $"index {index}");
            }
        }

        [Test]
        public void VisibilityBudget_WhenTheWholeCrowdIsStackedOnTheSpawnPoint_StillHonoursTheBudget()
        {
            // Everyone at the same distance is quickselect's degenerate case, and it is exactly
            // what a spawn point looks like the moment an instance fills up. It has to terminate
            // and it has to keep the budget, whichever players it picks.
            float[] distances = new float[Crowd];
            for (int index = 0; index < Crowd; index++)
            {
                distances[index] = 25f;
            }

            (int kept, bool[] _) = RunAvatarCap(distances, maxVisible: 80);
            Assert.That(kept, Is.EqualTo(80));
        }

        [Test]
        public void VisibilityBudget_WithHeavyTies_StillHonoursTheBudget()
        {
            // The realistic version of the same thing: a few dense clumps rather than one point.
            float[] distances = new float[Crowd];
            for (int index = 0; index < Crowd; index++)
            {
                distances[index] = (index % 5) * 100f + 1f;
            }

            (int kept, bool[] survived) = RunAvatarCap(distances, maxVisible: 250);
            Assert.That(kept, Is.EqualTo(250));

            // The closest clump is 400 players, so every survivor has to come from it.
            for (int index = 0; index < Crowd; index++)
            {
                if (survived[index])
                {
                    Assert.That(distances[index], Is.EqualTo(1f), $"index {index} survived from a farther clump");
                }
            }
        }

        [Test]
        public void VisibilityBudget_AcrossACrowd_LetsLoadedAvatarsHoldTheBoundary()
        {
            // A player oscillating across the budget boundary would otherwise reload their avatar
            // every tick, which costs far more than drawing one extra body.
            float[] distances = new float[Crowd];
            bool[] loaded = new bool[Crowd];
            for (int index = 0; index < Crowd; index++)
            {
                distances[index] = index + 1f;
                // Just outside the budget, but already loaded.
                loaded[index] = index >= 100 && index < 110;
            }

            (int kept, bool[] survived) = RunAvatarCap(distances, maxVisible: 105, loaded: loaded);

            Assert.That(kept, Is.EqualTo(105));
            for (int index = 100; index < 110; index++)
            {
                Assert.That(survived[index], Is.True,
                    $"index {index} is already loaded and only just past the budget");
            }
        }

        [Test]
        public void AudioBudget_AcrossACrowd_KeepsExactlyTheClosestN()
        {
            const int maxAudio = 32;
            NativeArray<float> distanceSq = new NativeArray<float>(Crowd, Allocator.Temp);
            NativeArray<bool> active = new NativeArray<bool>(Crowd, Allocator.Temp);
            NativeArray<bool> hearing = new NativeArray<bool>(Crowd, Allocator.Temp);
            NativeArray<AudioCapEntry> entries = new NativeArray<AudioCapEntry>(Crowd, Allocator.Temp);
            try
            {
                for (int index = 0; index < Crowd; index++)
                {
                    distanceSq[index] = ((index * 617) % Crowd) + 1f;
                    hearing[index] = true;
                }

                new BasisAudioCapJob
                {
                    MaxAudio = maxAudio,
                    ReceiverCount = Crowd,
                    StickinessBonus = 0.75f,
                    DistanceSq = distanceSq,
                    HasActiveAudioSource = active,
                    HearingRange = hearing,
                    Entries = entries,
                }.Execute();

                int kept = 0;
                for (int index = 0; index < Crowd; index++)
                {
                    if (hearing[index])
                    {
                        kept++;
                        Assert.That(distanceSq[index], Is.LessThanOrEqualTo(maxAudio), $"index {index}");
                    }
                }
                Assert.That(kept, Is.EqualTo(maxAudio));
            }
            finally
            {
                distanceSq.Dispose();
                active.Dispose();
                hearing.Dispose();
                entries.Dispose();
            }
        }

        // ── jiggle ────────────────────────────────────────────────────────────

        [Test]
        public void JiggleLod_AcrossACrowd_StaysInStepWithTheManagedModules()
        {
            bool savedColliders = BasisJiggleColliderLOD.Enabled;
            bool savedSimulation = BasisJiggleSimulationLOD.Enabled;
            float savedNear = BasisJiggleColliderLOD._nearSqr;
            float savedMid = BasisJiggleColliderLOD._midSqr;
            float savedFar = BasisJiggleColliderLOD._farSqr;
            float savedCutoff = BasisJiggleSimulationLOD._cutoffSqr;
            try
            {
                BasisJiggleColliderLOD.Enabled = true;
                BasisJiggleColliderLOD._nearSqr = 25f * 25f;
                BasisJiggleColliderLOD._midSqr = 50f * 50f;
                BasisJiggleColliderLOD._farSqr = 100f * 100f;
                BasisJiggleSimulationLOD.Enabled = true;
                BasisJiggleSimulationLOD._cutoffSqr = 120f * 120f;

                NativeArray<float> distanceSq = new NativeArray<float>(Crowd, Allocator.Temp);
                NativeArray<bool> hasColliders = new NativeArray<bool>(Crowd, Allocator.Temp);
                NativeArray<BasisJiggleColliderTier> currentTier = new NativeArray<BasisJiggleColliderTier>(Crowd, Allocator.Temp);
                NativeArray<bool> hasRigs = new NativeArray<bool>(Crowd, Allocator.Temp);
                NativeArray<bool> simulating = new NativeArray<bool>(Crowd, Allocator.Temp);
                NativeArray<BasisJiggleColliderTier> targetTier = new NativeArray<BasisJiggleColliderTier>(Crowd, Allocator.Temp);
                NativeArray<bool> targetSimulate = new NativeArray<bool>(Crowd, Allocator.Temp);
                try
                {
                    Unity.Mathematics.Random random = new Unity.Mathematics.Random(991);
                    for (int index = 0; index < Crowd; index++)
                    {
                        float metres = random.NextFloat(0f, 200f);
                        distanceSq[index] = metres * metres;
                        hasColliders[index] = true;
                        hasRigs[index] = true;
                        currentTier[index] = (BasisJiggleColliderTier)(index % 4);
                        simulating[index] = (index % 2) == 0;
                    }

                    BasisJiggleLodJob job = new BasisJiggleLodJob
                    {
                        ColliderLodEnabled = BasisJiggleColliderLOD.Enabled,
                        NearSqr = BasisJiggleColliderLOD._nearSqr,
                        MidSqr = BasisJiggleColliderLOD._midSqr,
                        FarSqr = BasisJiggleColliderLOD._farSqr,
                        ColliderHysteresisSqr = BasisJiggleColliderLOD.HysteresisSqr,
                        SimulationLodEnabled = BasisJiggleSimulationLOD.Enabled,
                        SimCutoffSqr = BasisJiggleSimulationLOD._cutoffSqr,
                        SimHysteresisSqr = BasisJiggleSimulationLOD.HysteresisSqr,
                        distanceSq = distanceSq,
                        HasJiggleColliders = hasColliders,
                        CurrentColliderTier = currentTier,
                        HasJiggleRigs = hasRigs,
                        CurrentlySimulating = simulating,
                        TargetColliderTier = targetTier,
                        TargetShouldSimulate = targetSimulate,
                    };
                    for (int index = 0; index < Crowd; index++)
                    {
                        job.Execute(index);
                    }

                    for (int index = 0; index < Crowd; index++)
                    {
                        Assert.That(targetTier[index],
                            Is.EqualTo(BasisJiggleColliderLOD.ComputeTier(distanceSq[index], currentTier[index])),
                            $"index {index} at squared distance {distanceSq[index]}");
                        Assert.That(targetSimulate[index],
                            Is.EqualTo(BasisJiggleSimulationLOD.ShouldSimulate(distanceSq[index], simulating[index])),
                            $"index {index} at squared distance {distanceSq[index]}");
                    }
                }
                finally
                {
                    distanceSq.Dispose();
                    hasColliders.Dispose();
                    currentTier.Dispose();
                    hasRigs.Dispose();
                    simulating.Dispose();
                    targetTier.Dispose();
                    targetSimulate.Dispose();
                }
            }
            finally
            {
                BasisJiggleColliderLOD.Enabled = savedColliders;
                BasisJiggleColliderLOD._nearSqr = savedNear;
                BasisJiggleColliderLOD._midSqr = savedMid;
                BasisJiggleColliderLOD._farSqr = savedFar;
                BasisJiggleSimulationLOD.Enabled = savedSimulation;
                BasisJiggleSimulationLOD._cutoffSqr = savedCutoff;
            }
        }

        [Test]
        public void JiggleLod_AcrossACrowd_TrimsMostOfTheRoom()
        {
            // The reason the LOD exists: in a spread-out crowd almost nobody should be paying for
            // a full collider set, and a large share should not be simulating at all.
            bool savedColliders = BasisJiggleColliderLOD.Enabled;
            bool savedSimulation = BasisJiggleSimulationLOD.Enabled;
            try
            {
                BasisJiggleColliderLOD.Enabled = true;
                BasisJiggleSimulationLOD.Enabled = true;

                int full = 0, simulating = 0;
                Unity.Mathematics.Random random = new Unity.Mathematics.Random(4004);
                for (int index = 0; index < Crowd; index++)
                {
                    float metres = random.NextFloat(0f, 200f);
                    float distanceSq = metres * metres;
                    if (BasisJiggleColliderLOD.ComputeTier(distanceSq, BasisJiggleColliderTier.Full) == BasisJiggleColliderTier.Full) full++;
                    if (BasisJiggleSimulationLOD.ShouldSimulate(distanceSq, currentlySimulating: true)) simulating++;
                }

                Assert.That(full, Is.LessThan(Crowd / 4), "most of a 200 m crowd should have lost some colliders");
                Assert.That(simulating, Is.LessThan(Crowd), "some of a 200 m crowd should be past the simulation cutoff");
            }
            finally
            {
                BasisJiggleColliderLOD.Enabled = savedColliders;
                BasisJiggleSimulationLOD.Enabled = savedSimulation;
            }
        }

        // ── performance mode ──────────────────────────────────────────────────

        [Test]
        public void CrowdSizesArmTheExpectedPerformanceLevels()
        {
            Assert.That(BasisPerformanceMode.LevelForPopulation(Crowd),
                Is.EqualTo(BasisPerformanceLevel.Aggressive),
                "a 2000-player instance is past the heaviest threshold");
            Assert.That(BasisPerformanceMode.LevelForPopulation(4000),
                Is.EqualTo(BasisPerformanceLevel.Aggressive));
        }
    }
}
