using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Whether the ray traced mode is built for every camera drawing it, or only for whichever one
    /// rendered first.
    ///
    /// One acceleration structure, one light budget and one emitter budget are shared by every camera in
    /// the frame - that is what stops a mirror, a photo camera and the player's eye each building their
    /// own. What they were shared AROUND was a single position, taken from whichever camera reached the
    /// refresh first, and everything that ranks or culls by distance answered for that camera alone.
    ///
    /// The handheld camera is where the two positions come apart by more than a rounding error: it can be
    /// flown across the room, set to follow from behind, or left on a table pointing back at the player.
    /// Past Skinned Max Distance from the player's head every avatar standing in front of it was missing
    /// from the structure it traced - a photo of a room where nobody bounces any light and nobody casts a
    /// traced shadow, taken beside a direct view of the same room where they all do, with both cameras
    /// running the same correctly configured effect.
    /// </summary>
    public class BasisGlobalIlluminationViewerTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<BasisGlobalIlluminationEmitter> ranked = new List<BasisGlobalIlluminationEmitter>();

        /// <summary>Where a player stands, and where a handheld camera they have flown away ends up.</summary>
        private static readonly Vector3 Player = Vector3.zero;
        private static readonly Vector3 FlownCamera = new Vector3(40f, 0f, 0f);

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationEmitter.Registered.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = 0; index < spawned.Count; index++)
            {
                if (spawned[index] != null) { Object.DestroyImmediate(spawned[index]); }
            }
            spawned.Clear();
            ranked.Clear();
            BasisGlobalIlluminationEmitter.Registered.Clear();
        }

        private Camera CameraAt(Vector3 position)
        {
            GameObject host = new GameObject("BasisGIViewerCamera");
            host.transform.position = position;
            spawned.Add(host);
            return host.AddComponent<Camera>();
        }

        private Light PointLight(Vector3 position, float intensity, float range = 20f)
        {
            GameObject host = new GameObject("BasisGIViewerLight");
            host.transform.position = position;
            spawned.Add(host);
            Light light = host.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = intensity;
            light.range = range;
            light.bounceIntensity = 1f;
            return light;
        }

        private BasisGlobalIlluminationEmitter Emitter(Vector3 position, Color colour, float intensity)
        {
            GameObject host = new GameObject("BasisGIViewerEmitter");
            host.transform.position = position;
            spawned.Add(host);
            BasisGlobalIlluminationEmitter emitter = host.AddComponent<BasisGlobalIlluminationEmitter>();
            emitter.Color = colour;
            emitter.Intensity = intensity;
            emitter.Radius = 0.25f;
            emitter.Range = 40f;
            emitter.Register();
            return emitter;
        }

        [Test]
        public void OneViewerMeasuresDistanceToItself()
        {
            BasisGlobalIlluminationRayViewers viewers = new Vector3(3f, 0f, 4f);
            Assert.AreEqual(1, viewers.Count);
            Assert.AreEqual(25f, viewers.DistanceSquared(Vector3.zero), 1e-4f);
        }

        [Test]
        public void DistanceIsMeasuredToTheNearestViewer()
        {
            BasisGlobalIlluminationRayViewers viewers =
                new BasisGlobalIlluminationRayViewers(new List<Vector3> { Player, FlownCamera });

            Assert.AreEqual(2, viewers.Count);
            Assert.AreEqual(1f, viewers.DistanceSquared(new Vector3(41f, 0f, 0f)), 1e-3f,
                "something standing in front of the second camera was measured from the first, which is how " +
                "it fell out of the skinned mesh budget");
            Assert.AreEqual(1f, viewers.DistanceSquared(new Vector3(1f, 0f, 0f)), 1e-3f,
                "adding a viewer must never push anything further away than it already was");
        }

        [Test]
        public void AnEmptySetIsTreatedAsASingleViewerAtTheOrigin()
        {
            BasisGlobalIlluminationRayViewers viewers = new BasisGlobalIlluminationRayViewers(new List<Vector3>());
            Assert.AreEqual(1, viewers.Count);
            Assert.AreEqual(0f, viewers.DistanceSquared(Vector3.zero), 1e-4f);
        }

        [Test]
        public void AnAvatarInFrontOfTheFlownCameraIsInsideTheSkinnedRange()
        {
            // The cull UpdateSkinned runs, spelled the way it now spells it. The default is 16m.
            const float maxDistance = 16f;
            Vector3 avatar = FlownCamera + new Vector3(2f, 0f, 0f);

            BasisGlobalIlluminationRayViewers playerOnly = Player;
            Assert.Greater(playerOnly.DistanceSquared(avatar), maxDistance * maxDistance,
                "the test is not set up: this avatar has to be out of range of the player for the case to exist");

            BasisGlobalIlluminationRayViewers both =
                new BasisGlobalIlluminationRayViewers(new List<Vector3> { Player, FlownCamera });
            Assert.Less(both.DistanceSquared(avatar), maxDistance * maxDistance,
                "an avatar standing right in front of the handheld camera is still not baked into the " +
                "structure, so it bounces no light and casts no traced shadow in the photo");
        }

        [Test]
        public void ALightBesideTheFlownCameraMakesTheBudget()
        {
            Light besidePlayer = PointLight(new Vector3(2f, 0f, 0f), 4f);
            Light besideFlownCamera = PointLight(FlownCamera + new Vector3(2f, 0f, 0f), 4f);
            List<Light> candidates = new List<Light> { besidePlayer, besideFlownCamera };

            BasisGlobalIlluminationRayViewers playerOnly = Player;
            Assert.Greater(BasisGlobalIlluminationRayLights.Score(besidePlayer, playerOnly),
                BasisGlobalIlluminationRayLights.Score(besideFlownCamera, playerOnly),
                "the test is not set up: ranked from the player alone the near light has to win");

            BasisGlobalIlluminationRayViewers both =
                new BasisGlobalIlluminationRayViewers(new List<Vector3> { Player, FlownCamera });
            Assert.AreEqual(BasisGlobalIlluminationRayLights.Score(besidePlayer, both),
                BasisGlobalIlluminationRayLights.Score(besideFlownCamera, both), 1e-3f,
                "a light lighting what the handheld camera is pointed at scored as though nobody was " +
                "looking at it, so a one slot budget would drop it");

            Assert.AreEqual(1f, BasisGlobalIlluminationRayLights.BoundaryWeight(candidates, both, 2), 1e-4f,
                "nothing was dropped, so nothing should have been faded");
        }

        [Test]
        public void AnEmitterBesideTheFlownCameraSurvivesAOneSlotBudget()
        {
            BasisGlobalIlluminationEmitter besidePlayer = Emitter(new Vector3(6f, 0f, 0f), Color.red, 4f);
            BasisGlobalIlluminationEmitter besideFlownCamera =
                Emitter(FlownCamera + new Vector3(1f, 0f, 0f), Color.green, 4f);

            BasisGlobalIlluminationEmitter.Selection playerOnly =
                BasisGlobalIlluminationEmitter.Rank(ranked, Player, 1);
            Assert.AreEqual(1, playerOnly.Count);
            Assert.AreSame(besidePlayer, ranked[0],
                "the test is not set up: ranked from the player alone the near emitter has to win");

            BasisGlobalIlluminationEmitter.Selection both = BasisGlobalIlluminationEmitter.Rank(
                ranked, new BasisGlobalIlluminationRayViewers(new List<Vector3> { Player, FlownCamera }), 1);
            Assert.AreEqual(1, both.Count);
            Assert.AreSame(besideFlownCamera, ranked[0],
                "an emitter a world author placed exactly where the handheld camera is pointed lost its " +
                "slot to one nearer the player, which is the bounce the author put it there for");
        }

        [Test]
        public void TheRefreshingCameraLeadsTheSet()
        {
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            Camera handheld = CameraAt(FlownCamera);

            set.Submit(player, 0);
            set.Submit(handheld, 0);

            BasisGlobalIlluminationRayViewers viewers = set.Resolve(player, 0);
            Assert.AreEqual(2, viewers.Count, "the handheld camera was not in the set the structure is built for");
            Assert.AreEqual(Player, viewers[0], "the camera doing the refresh should lead, since its position is the current one");
        }

        [Test]
        public void ACameraIsNotCountedTwiceWhenItRefreshes()
        {
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            set.Submit(player, 0);
            Assert.AreEqual(1, set.Resolve(player, 0).Count);
        }

        [Test]
        public void ACameraRegisteredLastFrameIsStillInThisFramesSet()
        {
            // The refresh runs on whichever camera gets there first, so the only way it can know about the
            // others is that they registered on the frames before. This is the whole mechanism.
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            Camera handheld = CameraAt(FlownCamera);

            set.Submit(handheld, 0);
            set.Submit(player, 1);
            Assert.AreEqual(2, set.Resolve(player, 1).Count,
                "a camera that rendered last frame was dropped, so the structure is rebuilt without it every frame");
        }

        [Test]
        public void ARateLimitedCameraOutlastsItsOwnCadence()
        {
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            Camera handheld = CameraAt(FlownCamera);

            // 10Hz on a 90Hz headset: one render in nine.
            set.Submit(handheld, 0);
            for (int frame = 1; frame < 9; frame++)
            {
                set.Submit(player, frame);
                Assert.AreEqual(2, set.Resolve(player, frame).Count,
                    "the handheld camera fell out of the set between its own renders at frame " + frame +
                    ", so what it can see enters and leaves the trace at its render rate");
            }
        }

        [Test]
        public void ACameraThatStoppedRenderingAgesOut()
        {
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            Camera handheld = CameraAt(FlownCamera);

            set.Submit(handheld, 0);
            int frame = BasisGlobalIlluminationRayViewerSet.MaxAge + 1;
            set.Submit(player, frame);
            Assert.AreEqual(1, set.Resolve(player, frame).Count,
                "a camera that stopped rendering kept the structure paying for what it used to see");
            Assert.AreEqual(1, set.Count, "the stale entry was not pruned");
        }

        [Test]
        public void ADestroyedCameraLeavesAtOnce()
        {
            BasisGlobalIlluminationRayViewerSet set = new BasisGlobalIlluminationRayViewerSet();
            Camera player = CameraAt(Player);
            Camera handheld = CameraAt(FlownCamera);

            set.Submit(handheld, 0);
            set.Submit(player, 0);
            Object.DestroyImmediate(handheld.gameObject);

            Assert.AreEqual(1, set.Resolve(player, 1).Count,
                "a destroyed camera was read for a position, which is a null dereference waiting for a frame");
            Assert.AreEqual(1, set.Count);
        }
    }
}
