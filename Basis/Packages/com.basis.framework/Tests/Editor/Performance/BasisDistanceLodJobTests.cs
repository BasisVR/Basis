using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The per-tick distance pass that decides, for every remote player, whether they are heard,
    /// drawn, and at what mesh and pose detail. Everything downstream that costs frame time hangs
    /// off these outputs, so a wrong band here is either a visible pop or milliseconds spent on a
    /// player nobody can see.
    ///
    /// Two behaviours are easy to break and invisible in a screenshot: the enter/exit hysteresis
    /// (without it a player standing on a boundary loads and unloads every tick), and the fact that
    /// pose LOD bands off AVATAR RANGE while mesh LOD bands off the mesh-LOD slider, so moving one
    /// slider must not silently move the other's thresholds.
    ///
    /// Jobs are driven through Execute directly, the way BasisVisibilityJobTests does, so the tests
    /// exercise the arithmetic without going through the scheduler.
    /// </summary>
    public class BasisDistanceLodJobTests
    {
        const float Hysteresis = 1.10f * 1.10f;

        /// <summary>Allocates every array the job needs and exposes the outputs; disposed by the using block.</summary>
        sealed class Harness : System.IDisposable
        {
            public NativeArray<float3> Targets;
            public NativeArray<bool> PrevMic, PrevHearing, PrevAvatar;
            public NativeArray<short> PrevMeshLod;
            public NativeArray<float> DistanceSq, PerIndexMinD2;
            public NativeArray<short> MeshLod, PoseLod;
            public NativeArray<bool> Mic, Hearing, Avatar, MeshLodChanged, Shouting;
            public NativeArray<int> PerIndexMask;
            public readonly int Count;

            public Harness(params float3[] targets)
            {
                Count = targets.Length;
                Targets = new NativeArray<float3>(targets, Allocator.Temp);
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

            public void Run(
                float voiceSq = 25f * 25f,
                float hearingSq = 25f * 25f,
                float avatarSq = 25f * 25f,
                float reductionMultiplier = 0f,
                bool useEyeGaze = false,
                float3 gazeForward = default,
                float cosHalfCone = 1f,
                float gazeBoost = 1f,
                float3 reference = default,
                float shoutRangeMultiplierSquared = 1f)
            {
                BasisDistanceJobParallel job = new BasisDistanceJobParallel
                {
                    SquaredVoiceDistance = voiceSq,
                    SquaredHearingDistance = hearingSq,
                    SquaredAvatarDistance = avatarSq,
                    ShoutRangeMultiplierSquared = shoutRangeMultiplierSquared,
                    RemoteIsShouting = Shouting,
                    HysteresisPercent = Hysteresis,
                    ReductionMultiplier = reductionMultiplier,
                    UseEyeGaze = useEyeGaze,
                    GazeForward = gazeForward,
                    CosHalfGazeCone = cosHalfCone,
                    GazeBoostFactor = gazeBoost,
                    referencePosition = reference,
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

        static float3 Ahead(float metres) => new float3(0f, 0f, metres);

        // ── distances ─────────────────────────────────────────────────────────

        [Test]
        public void ReportedDistanceIsTheRealSquaredDistance()
        {
            using Harness h = new Harness(new float3(3f, 4f, 0f));
            h.Run();

            Assert.That(h.DistanceSq[0], Is.EqualTo(25f).Within(1e-3f));
            Assert.That(h.PerIndexMinD2[0], Is.EqualTo(25f).Within(1e-3f),
                "the reduction reads this to find the nearest player, so it has to be the real distance.");
        }

        // ── range hysteresis ──────────────────────────────────────────────────

        [Test]
        public void EnteringRangeUsesTheTightThreshold()
        {
            // Sitting between the enter and exit radii while currently OUT stays out.
            const float enterSq = 100f;
            using Harness h = new Harness(Ahead(math.sqrt(enterSq * 1.05f)));
            h.Run(voiceSq: enterSq, hearingSq: enterSq, avatarSq: enterSq);

            Assert.That(h.Mic[0], Is.False);
            Assert.That(h.Hearing[0], Is.False);
            Assert.That(h.Avatar[0], Is.False);
        }

        [Test]
        public void LeavingRangeUsesTheWiderThreshold()
        {
            // The same distance, but currently IN: the exit radius holds them in.
            const float enterSq = 100f;
            using Harness h = new Harness(Ahead(math.sqrt(enterSq * 1.05f)));
            h.PrevMic[0] = true;
            h.PrevHearing[0] = true;
            h.PrevAvatar[0] = true;
            h.Run(voiceSq: enterSq, hearingSq: enterSq, avatarSq: enterSq);

            Assert.That(h.Mic[0], Is.True, "a player on the boundary must not flap in and out every tick.");
            Assert.That(h.Hearing[0], Is.True);
            Assert.That(h.Avatar[0], Is.True);
        }

        [Test]
        public void PastTheExitThreshold_EvenAHeldPlayerDrops()
        {
            const float enterSq = 100f;
            using Harness h = new Harness(Ahead(math.sqrt(enterSq * 1.5f)));
            h.PrevMic[0] = true;
            h.PrevHearing[0] = true;
            h.PrevAvatar[0] = true;
            h.Run(voiceSq: enterSq, hearingSq: enterSq, avatarSq: enterSq);

            Assert.That(h.Mic[0], Is.False, "hysteresis smooths the boundary, it does not pin someone in range forever.");
            Assert.That(h.Hearing[0], Is.False);
            Assert.That(h.Avatar[0], Is.False);
        }

        [Test]
        public void TheThreeRangesAreIndependent()
        {
            // Inside hearing range but outside avatar range: audible, not drawn.
            using Harness h = new Harness(Ahead(30f));
            h.Run(voiceSq: 10f * 10f, hearingSq: 50f * 50f, avatarSq: 20f * 20f);

            Assert.That(h.Mic[0], Is.False);
            Assert.That(h.Hearing[0], Is.True);
            Assert.That(h.Avatar[0], Is.False);
        }

        // ── shout ─────────────────────────────────────────────────────────────

        [Test]
        public void AShouterIsHeardBeyondTheOrdinaryHearingRange()
        {
            // 30 m out with a 25 m hearing range: silent normally, audible when shouting,
            // because shout multiplies the hearing test for that remote alone.
            using Harness h = new Harness(Ahead(30f));

            h.Run(voiceSq: 25f * 25f, hearingSq: 25f * 25f, avatarSq: 25f * 25f);
            Assert.That(h.Hearing[0], Is.False);

            h.Shouting[0] = true;
            h.Run(voiceSq: 25f * 25f, hearingSq: 25f * 25f, avatarSq: 25f * 25f, shoutRangeMultiplierSquared: 4f);
            Assert.That(h.Hearing[0], Is.True);
        }

        [Test]
        public void ShoutWidensHearingOnlyForTheShouter()
        {
            // Two players at the same distance, one shouting: the widening must not leak
            // into the shared range, and must not touch the avatar or microphone gates.
            using Harness h = new Harness(Ahead(30f), Ahead(30f));
            h.Shouting[1] = true;
            h.Run(voiceSq: 25f * 25f, hearingSq: 25f * 25f, avatarSq: 25f * 25f, shoutRangeMultiplierSquared: 4f);

            Assert.That(h.Hearing[0], Is.False, "a non-shouter at the same distance must stay out of range.");
            Assert.That(h.Hearing[1], Is.True);
            Assert.That(h.Avatar[1], Is.False, "shout carries voice, not draw distance.");
            Assert.That(h.Mic[1], Is.False, "the shouter's own microphone range is the talker's business, not the listener's.");
        }

        // ── change mask ───────────────────────────────────────────────────────

        [Test]
        public void SteadyStateProducesNoChangeMask()
        {
            using Harness h = new Harness(Ahead(5f));
            h.PrevMic[0] = true;
            h.PrevHearing[0] = true;
            h.PrevAvatar[0] = true;
            h.PrevMeshLod[0] = 0;
            h.Run(reductionMultiplier: 0f);

            Assert.That(h.PerIndexMask[0], Is.EqualTo(0), "an unchanged player must cost nothing downstream.");
            Assert.That(h.MeshLodChanged[0], Is.False);
        }

        [Test]
        public void EachTransitionSetsItsOwnMaskBit()
        {
            using Harness h = new Harness(Ahead(5f));
            h.PrevMeshLod[0] = 3;
            h.Run(reductionMultiplier: 0f);

            Assert.That(h.PerIndexMask[0] & 1, Is.EqualTo(1), "microphone range changed");
            Assert.That(h.PerIndexMask[0] & 2, Is.EqualTo(2), "hearing range changed");
            Assert.That(h.PerIndexMask[0] & 4, Is.EqualTo(4), "avatar range changed");
            Assert.That(h.PerIndexMask[0] & 8, Is.EqualTo(8), "mesh LOD changed");
            Assert.That(h.MeshLodChanged[0], Is.True);
        }

        [Test]
        public void OnlyTheRangeThatMovedIsFlagged()
        {
            using Harness h = new Harness(Ahead(30f));
            h.PrevHearing[0] = true;
            h.Run(voiceSq: 10f * 10f, hearingSq: 50f * 50f, avatarSq: 20f * 20f, reductionMultiplier: 0f);

            Assert.That(h.PerIndexMask[0], Is.EqualTo(0),
                "mic and avatar were already out, hearing was already in, and the LOD did not move.");
        }

        // ── mesh LOD ──────────────────────────────────────────────────────────

        [Test]
        public void MeshLodBandsIntoFourLevels()
        {
            using Harness h = new Harness(Ahead(1f), Ahead(2f), Ahead(3f), Ahead(4f));
            // normalized = d2 * multiplier, band = floor(normalized * 4).
            // 1/16, 4/16, 9/16, 16/16 of the way through the range.
            h.Run(reductionMultiplier: 1f / 16f);

            Assert.That((int)h.MeshLod[0], Is.EqualTo(0));
            Assert.That((int)h.MeshLod[1], Is.EqualTo(1));
            Assert.That((int)h.MeshLod[2], Is.EqualTo(2));
            Assert.That((int)h.MeshLod[3], Is.EqualTo(3));
        }

        [Test]
        public void MeshLodClampsAtTheFurthestBand()
        {
            using Harness h = new Harness(Ahead(1000f));
            h.Run(reductionMultiplier: 1f);
            Assert.That((int)h.MeshLod[0], Is.EqualTo(3), "there is no LOD 4 to fall off the end into.");
        }

        [Test]
        public void ZeroReductionMultiplier_PinsEveryoneToTheHighestDetail()
        {
            // The slider at zero means "no distance reduction", not "everyone is far away".
            using Harness h = new Harness(Ahead(1f), Ahead(500f));
            h.Run(reductionMultiplier: 0f);

            Assert.That((int)h.MeshLod[0], Is.EqualTo(0));
            Assert.That((int)h.MeshLod[1], Is.EqualTo(0));
        }

        // ── pose LOD ──────────────────────────────────────────────────────────

        [Test]
        public void PoseLodBandsOffAvatarRange()
        {
            // Quarters of the SQUARED avatar range: 0-25%, 25-50%, 50-75%, 75%+.
            const float avatarSq = 400f;
            using Harness h = new Harness(
                Ahead(math.sqrt(avatarSq * 0.1f)),
                Ahead(math.sqrt(avatarSq * 0.3f)),
                Ahead(math.sqrt(avatarSq * 0.6f)),
                Ahead(math.sqrt(avatarSq * 0.9f)));
            h.Run(avatarSq: avatarSq);

            Assert.That((int)h.PoseLod[0], Is.EqualTo(0));
            Assert.That((int)h.PoseLod[1], Is.EqualTo(1));
            Assert.That((int)h.PoseLod[2], Is.EqualTo(2));
            Assert.That((int)h.PoseLod[3], Is.EqualTo(3));
        }

        [Test]
        public void PoseLodIsIndependentOfTheMeshLodSlider()
        {
            // The whole point of banding pose LOD off avatar range: dragging the mesh LOD slider
            // must not silently change how often distant players' poses update.
            const float avatarSq = 400f;
            float3 target = Ahead(math.sqrt(avatarSq * 0.6f));

            using Harness low = new Harness(target);
            low.Run(avatarSq: avatarSq, reductionMultiplier: 0f);

            using Harness high = new Harness(target);
            high.Run(avatarSq: avatarSq, reductionMultiplier: 10f);

            Assert.That((int)low.MeshLod[0], Is.Not.EqualTo((int)high.MeshLod[0]), "the mesh LOD did move");
            Assert.That((int)low.PoseLod[0], Is.EqualTo((int)high.PoseLod[0]), "the pose LOD must not have");
        }

        [Test]
        public void PoseLodBeyondAvatarRangeClampsToTheFurthestBand()
        {
            using Harness h = new Harness(Ahead(1000f));
            h.Run(avatarSq: 100f);
            Assert.That((int)h.PoseLod[0], Is.EqualTo(3));
        }

        [Test]
        public void ZeroAvatarRange_DoesNotDivideByZero()
        {
            using Harness h = new Harness(Ahead(50f));
            h.Run(avatarSq: 0f);
            Assert.That((int)h.PoseLod[0], Is.EqualTo(0));
        }

        // ── gaze foveation ────────────────────────────────────────────────────

        [Test]
        public void PlayersAtTheCentreOfTheGazeConeGetABetterBand()
        {
            float3 forward = new float3(0f, 0f, 1f);
            float cosHalfCone = math.cos(math.radians(10f));

            using Harness plain = new Harness(Ahead(10f));
            plain.Run(reductionMultiplier: 1f / 128f);

            using Harness gazed = new Harness(Ahead(10f));
            gazed.Run(reductionMultiplier: 1f / 128f, useEyeGaze: true, gazeForward: forward,
                cosHalfCone: cosHalfCone, gazeBoost: 0.25f);

            Assert.That((int)gazed.MeshLod[0], Is.LessThan((int)plain.MeshLod[0]),
                "what the player is looking at is the last thing worth cutting detail on.");
        }

        [Test]
        public void TheBoostFadesToNothingAtTheConeEdge()
        {
            // dot == cosHalfCone puts the interpolation at t = 0, which is no boost at all.
            const float halfConeDegrees = 20f;
            float cosHalfCone = math.cos(math.radians(halfConeDegrees));
            const float distance = 10f;
            float3 onTheEdge = new float3(
                distance * math.sin(math.radians(halfConeDegrees)), 0f,
                distance * math.cos(math.radians(halfConeDegrees)));

            using Harness edge = new Harness(onTheEdge);
            edge.Run(reductionMultiplier: 1f / 256f, useEyeGaze: true, gazeForward: new float3(0f, 0f, 1f),
                cosHalfCone: cosHalfCone, gazeBoost: 0.25f);

            using Harness plain = new Harness(onTheEdge);
            plain.Run(reductionMultiplier: 1f / 256f);

            Assert.That((int)edge.MeshLod[0], Is.EqualTo((int)plain.MeshLod[0]),
                "a hard edge to the boost would show as a visible seam at the cone boundary.");
        }

        [Test]
        public void PlayersOutsideTheConeAreUntouched()
        {
            float3 behindTarget = new float3(0f, 0f, -10f);

            using Harness behind = new Harness(behindTarget);
            behind.Run(reductionMultiplier: 1f / 256f, useEyeGaze: true, gazeForward: new float3(0f, 0f, 1f),
                cosHalfCone: math.cos(math.radians(10f)), gazeBoost: 0.25f);

            using Harness plain = new Harness(behindTarget);
            plain.Run(reductionMultiplier: 1f / 256f);

            Assert.That((int)behind.MeshLod[0], Is.EqualTo((int)plain.MeshLod[0]));
        }

        [Test]
        public void TheBoostDoesNotAlterTheReportedDistances()
        {
            // Audio and the nearest-player reduction both read these; a foveated distance there
            // would make whoever the player happens to look at sound closer.
            using Harness h = new Harness(Ahead(10f));
            h.Run(avatarSq: 100f, reductionMultiplier: 1f / 128f, useEyeGaze: true,
                gazeForward: new float3(0f, 0f, 1f), cosHalfCone: math.cos(math.radians(30f)),
                gazeBoost: 0.25f);

            Assert.That(h.DistanceSq[0], Is.EqualTo(100f).Within(1e-2f));
            Assert.That(h.PerIndexMinD2[0], Is.EqualTo(100f).Within(1e-2f));
        }

        [Test]
        public void GazeBoostDoesNotMoveTheRangeGates()
        {
            // Foveation changes detail, never audibility or whether someone is drawn at all.
            using Harness h = new Harness(Ahead(30f));
            h.Run(voiceSq: 20f * 20f, hearingSq: 20f * 20f, avatarSq: 20f * 20f,
                useEyeGaze: true, gazeForward: new float3(0f, 0f, 1f),
                cosHalfCone: math.cos(math.radians(45f)), gazeBoost: 0.01f);

            Assert.That(h.Mic[0], Is.False);
            Assert.That(h.Hearing[0], Is.False);
            Assert.That(h.Avatar[0], Is.False);
        }

        [Test]
        public void APlayerStandingOnTheCamera_DoesNotProduceANaNBand()
        {
            // rsqrt(0) is infinity, so the job's own epsilon has to keep that out of the band math.
            using Harness h = new Harness(float3.zero);
            h.Run(reductionMultiplier: 1f, useEyeGaze: true, gazeForward: new float3(0f, 0f, 1f),
                cosHalfCone: 0.5f, gazeBoost: 0.25f);

            Assert.That((int)h.MeshLod[0], Is.EqualTo(0));
            Assert.That((int)h.PoseLod[0], Is.EqualTo(0));
            Assert.That(h.DistanceSq[0], Is.EqualTo(0f));
        }

        // ── reduction ─────────────────────────────────────────────────────────

        [Test]
        public void ReduceTakesTheNearestDistanceAndTheUnionOfChanges()
        {
            NativeArray<float> minD2 = new NativeArray<float>(new[] { 90f, 4f, 25f }, Allocator.Temp);
            NativeArray<int> masks = new NativeArray<int>(new[] { 1, 0, 8 }, Allocator.Temp);
            NativeArray<float> smallest = new NativeArray<float>(1, Allocator.Temp);
            NativeArray<int> changed = new NativeArray<int>(1, Allocator.Temp);
            try
            {
                new BasisDistanceReduceJob
                {
                    ReceiverCount = 3,
                    PerIndexMinD2 = minD2,
                    PerIndexMask = masks,
                    SmallestD2 = smallest,
                    ChangeMask = changed,
                }.Execute();

                Assert.That(smallest[0], Is.EqualTo(4f));
                Assert.That(changed[0], Is.EqualTo(9), "the mask is the union across every receiver.");
            }
            finally
            {
                minD2.Dispose();
                masks.Dispose();
                smallest.Dispose();
                changed.Dispose();
            }
        }

        [Test]
        public void ReduceOverNoReceivers_ReportsNoNearestPlayer()
        {
            NativeArray<float> minD2 = new NativeArray<float>(1, Allocator.Temp);
            NativeArray<int> masks = new NativeArray<int>(1, Allocator.Temp);
            NativeArray<float> smallest = new NativeArray<float>(1, Allocator.Temp);
            NativeArray<int> changed = new NativeArray<int>(1, Allocator.Temp);
            try
            {
                new BasisDistanceReduceJob
                {
                    ReceiverCount = 0,
                    PerIndexMinD2 = minD2,
                    PerIndexMask = masks,
                    SmallestD2 = smallest,
                    ChangeMask = changed,
                }.Execute();

                Assert.That(float.IsPositiveInfinity(smallest[0]), Is.True,
                    "an empty instance has no nearest player, and infinity is what every distance comparison downstream handles safely.");
                Assert.That(changed[0], Is.EqualTo(0));
            }
            finally
            {
                minD2.Dispose();
                masks.Dispose();
                smallest.Dispose();
                changed.Dispose();
            }
        }
    }
}
