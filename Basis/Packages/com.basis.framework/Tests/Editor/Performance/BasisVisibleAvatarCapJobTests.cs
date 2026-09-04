using NUnit.Framework;
using Unity.Collections;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The two hard caps that stop a crowded instance from costing whatever the crowd feels like:
    /// MaxVisibleAvatars and MaxAudioSources. Both take the in-range set the distance pass produced
    /// and keep only the closest N, using quickselect rather than a full sort because only the
    /// boundary matters.
    ///
    /// The part worth pinning is not the arithmetic but the edges: which entries count toward the
    /// cap, what "0" means for each job (they disagree, deliberately), and the stickiness bonus that
    /// stops an avatar hovering on the boundary from being loaded and unloaded every tick, which
    /// costs far more than simply drawing it would have.
    /// </summary>
    public class BasisVisibleAvatarCapJobTests
    {
        const float Stickiness = 0.75f;

        sealed class AvatarCase : System.IDisposable
        {
            public NativeArray<float> DistanceSq;
            public NativeArray<bool> Loaded, InRange;
            public NativeArray<AvatarCapEntry> Entries;
            public readonly int Count;

            public AvatarCase(float[] distances)
            {
                Count = distances.Length;
                DistanceSq = new NativeArray<float>(distances, Allocator.Temp);
                Loaded = new NativeArray<bool>(Count, Allocator.Temp);
                InRange = new NativeArray<bool>(Count, Allocator.Temp);
                Entries = new NativeArray<AvatarCapEntry>(Count, Allocator.Temp);
                for (int index = 0; index < Count; index++)
                {
                    InRange[index] = true;
                }
            }

            public void Run(int maxVisible, float stickiness = Stickiness)
            {
                new BasisAvatarCapJob
                {
                    MaxVisible = maxVisible,
                    ReceiverCount = Count,
                    StickinessBonus = stickiness,
                    DistanceSq = DistanceSq,
                    HasRealAvatarLoaded = Loaded,
                    AvatarRange = InRange,
                    Entries = Entries,
                }.Execute();
            }

            public int Kept
            {
                get
                {
                    int kept = 0;
                    for (int index = 0; index < Count; index++)
                    {
                        if (InRange[index]) kept++;
                    }
                    return kept;
                }
            }

            public void Dispose()
            {
                DistanceSq.Dispose();
                Loaded.Dispose();
                InRange.Dispose();
                Entries.Dispose();
            }
        }

        sealed class AudioCase : System.IDisposable
        {
            public NativeArray<float> DistanceSq;
            public NativeArray<bool> Active, InRange;
            public NativeArray<AudioCapEntry> Entries;
            public readonly int Count;

            public AudioCase(float[] distances)
            {
                Count = distances.Length;
                DistanceSq = new NativeArray<float>(distances, Allocator.Temp);
                Active = new NativeArray<bool>(Count, Allocator.Temp);
                InRange = new NativeArray<bool>(Count, Allocator.Temp);
                Entries = new NativeArray<AudioCapEntry>(Count, Allocator.Temp);
                for (int index = 0; index < Count; index++)
                {
                    InRange[index] = true;
                }
            }

            public void Run(int maxAudio, float stickiness = Stickiness)
            {
                new BasisAudioCapJob
                {
                    MaxAudio = maxAudio,
                    ReceiverCount = Count,
                    StickinessBonus = stickiness,
                    DistanceSq = DistanceSq,
                    HasActiveAudioSource = Active,
                    HearingRange = InRange,
                    Entries = Entries,
                }.Execute();
            }

            public int Kept
            {
                get
                {
                    int kept = 0;
                    for (int index = 0; index < Count; index++)
                    {
                        if (InRange[index]) kept++;
                    }
                    return kept;
                }
            }

            public void Dispose()
            {
                DistanceSq.Dispose();
                Active.Dispose();
                InRange.Dispose();
                Entries.Dispose();
            }
        }

        // ── avatar cap ────────────────────────────────────────────────────────

        [Test]
        public void UnderTheCap_NobodyIsDropped()
        {
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f });
            c.Run(maxVisible: 10);
            Assert.That(c.Kept, Is.EqualTo(3));
        }

        [Test]
        public void ExactlyAtTheCap_NobodyIsDropped()
        {
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f });
            c.Run(maxVisible: 3);
            Assert.That(c.Kept, Is.EqualTo(3));
        }

        [Test]
        public void OverTheCap_TheClosestSurvive()
        {
            using AvatarCase c = new AvatarCase(new[] { 100f, 4f, 900f, 1f, 25f });
            c.Run(maxVisible: 3);

            Assert.That(c.Kept, Is.EqualTo(3));
            Assert.That(c.InRange[3], Is.True, "1 m away");
            Assert.That(c.InRange[1], Is.True, "2 m away");
            Assert.That(c.InRange[4], Is.True, "5 m away");
            Assert.That(c.InRange[0], Is.False, "10 m away");
            Assert.That(c.InRange[2], Is.False, "30 m away");
        }

        [Test]
        public void ManyPlayers_StillKeepExactlyTheClosestN()
        {
            // Quickselect is the part most likely to be subtly wrong, and only shows up
            // once the partition actually recurses.
            const int count = 200;
            float[] distances = new float[count];
            for (int index = 0; index < count; index++)
            {
                // Deterministic, non-monotonic, no duplicates.
                distances[index] = ((index * 37) % count) + 1f;
            }

            using AvatarCase c = new AvatarCase(distances);
            c.Run(maxVisible: 20);

            Assert.That(c.Kept, Is.EqualTo(20));
            for (int index = 0; index < count; index++)
            {
                bool shouldSurvive = distances[index] <= 20f;
                Assert.That(c.InRange[index], Is.EqualTo(shouldSurvive),
                    $"index {index} at squared distance {distances[index]}");
            }
        }

        [Test]
        public void OutOfRangePlayersDoNotConsumeCapSlots()
        {
            // Someone already beyond avatar range costs nothing to draw, so they must not
            // push a visible player out of the budget.
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f, 16f });
            c.InRange[0] = false;
            c.InRange[1] = false;
            c.Run(maxVisible: 2);

            Assert.That(c.InRange[2], Is.True);
            Assert.That(c.InRange[3], Is.True);
            Assert.That(c.Kept, Is.EqualTo(2));
        }

        [Test]
        public void CapOfZero_ShowsNoRealAvatars()
        {
            // 0 means "show zero", not "unlimited" — unlimited is the limiter being off,
            // which the caller expresses by not scheduling the job at all.
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f });
            c.Run(maxVisible: 0);
            Assert.That(c.Kept, Is.EqualTo(0));
        }

        [Test]
        public void NegativeCap_IsTreatedAsNoCapAtAll()
        {
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f });
            c.Run(maxVisible: -1);
            Assert.That(c.Kept, Is.EqualTo(3));
        }

        [Test]
        public void AlreadyLoadedAvatarsAreStickyOnTheBoundary()
        {
            // Two players near the cap boundary: the further one is already loaded. Reloading
            // an avatar costs far more than one extra frame of drawing it, so the bonus keeps it.
            using AvatarCase c = new AvatarCase(new[] { 1f, 100f, 110f });
            c.Loaded[2] = true;
            c.Run(maxVisible: 2);

            Assert.That(c.InRange[0], Is.True);
            Assert.That(c.InRange[2], Is.True, "110 * 0.75 = 82.5 beats the unloaded 100.");
            Assert.That(c.InRange[1], Is.False);
        }

        [Test]
        public void StickinessDoesNotOverrideALargeDistanceGap()
        {
            // The bonus smooths the boundary; it does not keep someone across the room loaded.
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 10_000f });
            c.Loaded[2] = true;
            c.Run(maxVisible: 2);

            Assert.That(c.InRange[2], Is.False);
        }

        [Test]
        public void NoReceivers_IsANoOp()
        {
            using AvatarCase c = new AvatarCase(new float[0]);
            c.Run(maxVisible: 0);
            Assert.That(c.Kept, Is.EqualTo(0));
        }

        [Test]
        public void EveryoneAlreadyOutOfRange_StaysOut()
        {
            using AvatarCase c = new AvatarCase(new[] { 1f, 4f, 9f });
            for (int index = 0; index < c.Count; index++)
            {
                c.InRange[index] = false;
            }
            c.Run(maxVisible: 1);
            Assert.That(c.Kept, Is.EqualTo(0));
        }

        // ── audio cap ─────────────────────────────────────────────────────────

        [Test]
        public void AudioCapKeepsTheClosestVoices()
        {
            using AudioCase c = new AudioCase(new[] { 400f, 1f, 100f, 9f });
            c.Run(maxAudio: 2);

            Assert.That(c.Kept, Is.EqualTo(2));
            Assert.That(c.InRange[1], Is.True);
            Assert.That(c.InRange[3], Is.True);
            Assert.That(c.InRange[0], Is.False);
            Assert.That(c.InRange[2], Is.False);
        }

        [Test]
        public void AudioCapOfZero_MeansUnlimited_UnlikeTheAvatarCap()
        {
            // The two jobs read 0 differently on purpose: silencing everyone is never a
            // useful setting, so the audio job treats 0 as "no cap".
            using AudioCase audio = new AudioCase(new[] { 1f, 4f, 9f });
            audio.Run(maxAudio: 0);
            Assert.That(audio.Kept, Is.EqualTo(3));

            using AvatarCase avatars = new AvatarCase(new[] { 1f, 4f, 9f });
            avatars.Run(maxVisible: 0);
            Assert.That(avatars.Kept, Is.EqualTo(0));
        }

        [Test]
        public void AudibleSourcesAreStickyOnTheBoundary()
        {
            // Restarting a voice source clips the first packets, so a source already playing
            // gets the same boundary bonus a loaded avatar does.
            using AudioCase c = new AudioCase(new[] { 1f, 100f, 110f });
            c.Active[2] = true;
            c.Run(maxAudio: 2);

            Assert.That(c.InRange[2], Is.True);
            Assert.That(c.InRange[1], Is.False);
        }

        [Test]
        public void OutOfHearingRangeSourcesDoNotConsumeCapSlots()
        {
            using AudioCase c = new AudioCase(new[] { 1f, 4f, 9f, 16f });
            c.InRange[0] = false;
            c.Run(maxAudio: 2);

            Assert.That(c.Kept, Is.EqualTo(2));
            Assert.That(c.InRange[1], Is.True);
            Assert.That(c.InRange[2], Is.True);
            Assert.That(c.InRange[3], Is.False);
        }
    }
}
