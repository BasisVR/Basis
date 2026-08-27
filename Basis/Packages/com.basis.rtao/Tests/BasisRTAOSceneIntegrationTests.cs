using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOSceneIntegrationTests
    {
        private BasisRTAOContext context;
        private BasisRTAOScene scene;
        private BasisRTAOResources resources;
        private readonly List<GameObject> spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();

            resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            resources.PopulateFromPackage();

            context = BasisRTAOContext.Create(resources, BasisRTAOContext.HardwareSupported ? BasisRTAOBackend.Hardware : BasisRTAOBackend.ComputeBvh, out string error);
            if (context == null)
                Assert.Ignore($"No ray tracing backend is available here: {error}");

            scene = new BasisRTAOScene(context);
        }

        [TearDown]
        public void TearDown()
        {
            scene?.Dispose();
            scene = null;
            context?.Dispose();
            context = null;

            if (resources != null)
                Object.DestroyImmediate(resources);
            resources = null;

            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null)
                    Object.DestroyImmediate(spawned[i]);
            }
            spawned.Clear();
        }

        private GameObject Cube(string name, Vector3 position)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            spawned.Add(go);
            return go;
        }

        [Test]
        public void FreshSceneWantsABuild()
        {
            Assert.IsTrue(scene.NeedsBuild);
            Assert.IsFalse(scene.HasGeometry);
        }

        [Test]
        public void RescanPicksUpSceneRenderers()
        {
            Cube("BasisRTAOSceneA", Vector3.zero);
            Cube("BasisRTAOSceneB", new Vector3(4f, 0f, 0f));

            int before = scene.InstanceCount;
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);

            Assert.GreaterOrEqual(scene.InstanceCount, before + 2);
            Assert.IsTrue(scene.HasGeometry);
        }

        [Test]
        public void RescanIsIdempotentForAStaticScene()
        {
            Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            int first = scene.InstanceCount;

            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            Assert.AreEqual(first, scene.InstanceCount, "Rescanning an unchanged scene must not duplicate instances.");
        }

        [Test]
        public void DestroyedRenderersLeaveTheStructure()
        {
            GameObject cube = Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            int withCube = scene.InstanceCount;

            Object.DestroyImmediate(cube);
            spawned.Remove(cube);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);

            Assert.AreEqual(withCube - 1, scene.InstanceCount);
        }

        [Test]
        public void DisabledRenderersLeaveTheStructure()
        {
            GameObject cube = Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            int withCube = scene.InstanceCount;

            cube.GetComponent<Renderer>().enabled = false;
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);

            Assert.AreEqual(withCube - 1, scene.InstanceCount);
        }

        [Test]
        public void LayerMaskFiltersTheRescan()
        {
            GameObject cube = Cube("BasisRTAOSceneA", Vector3.zero);
            cube.layer = 11;

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.layerMask = ~(1 << 11);
            scene.Rescan(settings);
            int without = scene.InstanceCount;

            settings.layerMask = ~0;
            scene.Rescan(settings);
            Assert.AreEqual(without + 1, scene.InstanceCount);
        }

        [Test]
        public void BuildClearsTheDirtyFlag()
        {
            Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            Assert.IsTrue(scene.NeedsBuild);

            CommandBuffer cmd = new CommandBuffer { name = "BasisRTAOSceneTestBuild" };
            try
            {
                scene.Build(cmd);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }

            Assert.IsFalse(scene.NeedsBuild, "A completed build must clear the dirty flag so static scenes stop rebuilding every frame.");
        }

        [Test]
        public void MovingADynamicRendererDirtiesTheStructure()
        {
            GameObject cube = Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            BuildOnce();
            Assert.IsFalse(scene.NeedsBuild);

            cube.transform.position = new Vector3(0f, 3f, 0f);
            scene.Refresh(BasisRTAOTestSettings.EveryLayer, Vector3.zero, 1000f, 1);

            Assert.IsTrue(scene.NeedsBuild, "A moved renderer must trigger a rebuild, otherwise its occlusion stays at the old position.");
        }

        [Test]
        public void AStillSceneDoesNotAskForRepeatedRebuilds()
        {
            Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            BuildOnce();

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;
            scene.Refresh(settings, Vector3.zero, 1f, 1);
            scene.Refresh(settings, Vector3.zero, 2f, 2);

            Assert.IsFalse(scene.NeedsBuild, "Nothing moved, so no rebuild should be queued.");
        }

        [Test]
        public void MarkDirtyForcesAnImmediateRescan()
        {
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            int before = scene.InstanceCount;

            Cube("BasisRTAOSceneLate", Vector3.zero);

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;
            scene.Refresh(settings, Vector3.zero, 1f, 1);
            int afterQuietRefresh = scene.InstanceCount;

            scene.MarkDirty();
            scene.Refresh(settings, Vector3.zero, 2f, 2);

            Assert.AreEqual(before + 1, scene.InstanceCount, "MarkDirty must force the rescan that the interval was still holding off.");
            Assert.LessOrEqual(afterQuietRefresh, scene.InstanceCount);
        }

        [Test]
        public void SkinnedRenderersAreIgnoredUnlessAskedFor()
        {
            GameObject skinnedObject = new GameObject("BasisRTAOSkinned");
            spawned.Add(skinnedObject);
            SkinnedMeshRenderer skinned = skinnedObject.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshFilter>().sharedMesh;

            BasisRTAOSceneSettings off = BasisRTAOTestSettings.EveryLayer;
            off.skinnedMode = BasisRTAOSkinnedMode.Off;
            scene.Rescan(off);
            Assert.AreEqual(0, scene.SkinnedCount);

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            scene.Rescan(settings);

            Assert.AreEqual(1, scene.SkinnedCount, "Turning skinned mode on must bring skinned renderers into the structure.");
        }

        private GameObject SkinnedAvatar(string name, Vector3 position)
        {
            GameObject go = new GameObject(name);
            go.transform.position = position;
            spawned.Add(go);

            SkinnedMeshRenderer skinned = go.AddComponent<SkinnedMeshRenderer>();
            GameObject donor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            skinned.sharedMesh = donor.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(donor);
            return go;
        }

        [Test]
        public void ARemoteAvatarWithShadowsLoddedOffStillOccludes()
        {
            GameObject avatar = SkinnedAvatar("BasisRTAOShadowLodAvatar", new Vector3(1f, 0f, 0f));
            // BasisAvatarShadowLOD forces this on every remote past mesh LOD 2, roughly 14 m out.
            avatar.GetComponent<SkinnedMeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.requireShadowCasting = true;

            Assert.IsTrue(BasisRTAOScene.ShouldInclude(avatar.GetComponent<SkinnedMeshRenderer>(), settings),
                "Shadow casting mode is an authoring signal on world geometry, but on a remote avatar it is driven at runtime by the shadow LOD. Filtering avatars on it drops most of the room out of the structure.");

            scene.Rescan(settings);
            Assert.AreEqual(1, scene.SkinnedCount);
        }

        [Test]
        public void WorldGeometryStillHonoursShadowCastingMode()
        {
            GameObject cube = Cube("BasisRTAONoShadowCube", Vector3.zero);
            cube.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.requireShadowCasting = true;

            Assert.IsFalse(BasisRTAOScene.ShouldInclude(cube.GetComponent<Renderer>(), settings),
                "On a mesh renderer the flag is what the author set, so it stays a valid opt out.");
        }

        [Test]
        public void DistantAvatarsKeepOccludingFromWhereTheyActuallyAre()
        {
            GameObject avatar = SkinnedAvatar("BasisRTAOFarAvatar", new Vector3(50f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.skinnedMaxDistance = 8f;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);
            int instances = scene.InstanceCount;
            BuildOnce();

            avatar.transform.position = new Vector3(60f, 0f, 0f);
            scene.Refresh(settings, Vector3.zero, 1f, 100);

            Assert.AreEqual(instances, scene.InstanceCount,
                "A remote past the pose budget must stay in the structure; dropping it means it casts nothing at all.");
            Assert.IsTrue(scene.NeedsBuild,
                "Its instance transform has to follow it, or it occludes from where it used to be standing.");
        }

        [Test]
        public void OnlyNearbyAvatarsSpendTheBakeBudget()
        {
            SkinnedAvatar("BasisRTAONearAvatar", new Vector3(1f, 0f, 0f));
            SkinnedAvatar("BasisRTAOFarAvatar", new Vector3(500f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.skinnedMaxDistance = 8f;
            settings.skinnedBakesPerFrame = 8;
            settings.skinnedBakeInterval = 1;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);
            Assert.AreEqual(2, scene.SkinnedCount);

            scene.Refresh(settings, Vector3.zero, 1f, 500);

            Assert.AreEqual(1, scene.StaleSkinnedCount(500, 1),
                "The distant avatar keeps its last pose so the budget goes to the ones close enough to read.");
        }

        [Test]
        public void AZeroDistanceBudgetBakesEveryAvatar()
        {
            SkinnedAvatar("BasisRTAODistantAvatar", new Vector3(500f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.skinnedMaxDistance = 0f;
            settings.skinnedBakesPerFrame = 8;
            settings.skinnedBakeInterval = 1;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);
            scene.Refresh(settings, Vector3.zero, 1f, 500);

            Assert.AreEqual(0, scene.StaleSkinnedCount(500, 1), "A distance of zero means unlimited.");
        }

        [Test]
        public void SeveralCamerasInOneFrameRefreshTheSceneOnce()
        {
            SkinnedAvatar("BasisRTAOBudgetAvatar", new Vector3(1f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.skinnedBakesPerFrame = 1;
            settings.skinnedBakeInterval = 1;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);

            // frame 500: the main view, then a mirror, then the handheld camera
            scene.Refresh(settings, Vector3.zero, 1f, 500);
            Assert.AreEqual(0, scene.StaleSkinnedCount(500, 1), "The first camera of the frame does the work.");

            // if the later cameras refreshed too they would each spend the bake budget again
            scene.Refresh(settings, Vector3.zero, 1f, 500);
            scene.Refresh(settings, Vector3.zero, 1f, 500);

            Assert.AreEqual(0, scene.StaleSkinnedCount(500, 1),
                "A mirror and the handheld camera record their own passes in the same frame, so the rescan, the transform sweep and the avatar re-bakes must happen once, not once per camera.");
        }

        [Test]
        public void MarkDirtyPunchesThroughTheOncePerFrameGuard()
        {
            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;

            scene.Refresh(settings, Vector3.zero, 1f, 500);
            int before = scene.InstanceCount;

            Cube("BasisRTAOLateCube", Vector3.zero);

            // same frame, no dirty: the guard holds
            scene.Refresh(settings, Vector3.zero, 1f, 500);
            Assert.AreEqual(before, scene.InstanceCount);

            // same frame, dirtied: an avatar swap must not wait for the next frame
            scene.MarkDirty();
            scene.Refresh(settings, Vector3.zero, 1f, 500);

            Assert.AreEqual(before + 1, scene.InstanceCount,
                "MarkDirty has to override the per frame guard, or in edit mode - where the frame counter never advances - it would be swallowed entirely.");
        }

        [Test]
        public void ANewFrameRefreshesAgain()
        {
            GameObject cube = Cube("BasisRTAOMover", Vector3.zero);

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);
            scene.Refresh(settings, Vector3.zero, 1f, 500);
            BuildOnce();
            Assert.IsFalse(scene.NeedsBuild);

            cube.transform.position = new Vector3(0f, 5f, 0f);
            scene.Refresh(settings, Vector3.zero, 1f, 501);

            Assert.IsTrue(scene.NeedsBuild, "The guard is per frame, not permanent.");
        }

        [Test]
        public void AddingExcludeRemovesTheRendererOnTheNextRescan()
        {
            GameObject cube = Cube("BasisRTAOExcludeMe", Vector3.zero);

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;

            scene.Rescan(settings);
            int withCube = scene.InstanceCount;
            Assert.Greater(withCube, 0);

            cube.AddComponent<BasisRTAOExclude>();
            scene.Rescan(settings);

            Assert.AreEqual(withCube - 1, scene.InstanceCount,
                "A renderer that grows a BasisRTAOExclude has to leave the structure on the next rescan, not just fail the filter for new additions.");
        }

        [Test]
        public void MarkDirtyThenRefreshHonoursANewExclude()
        {
            GameObject cube = Cube("BasisRTAOExcludeLater", Vector3.zero);

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.rescanInterval = 10000f;

            scene.Refresh(settings, Vector3.zero, 1f, 700);
            int withCube = scene.InstanceCount;
            Assert.Greater(withCube, 0);

            cube.AddComponent<BasisRTAOExclude>();
            scene.MarkDirty();
            scene.Refresh(settings, Vector3.zero, 1f, 700);

            Assert.AreEqual(withCube - 1, scene.InstanceCount,
                "This is the path the runtime actually takes: add the component, mark dirty, refresh. If it does not drop here, exclusion never reaches the acceleration structure.");
        }

        [Test]
        public void SwappedAvatarsLeaveTheStructureAndTheSkinnedList()
        {
            GameObject avatar = SkinnedAvatar("BasisRTAOSwapAvatar", new Vector3(1f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;

            scene.Rescan(settings);
            int withAvatar = scene.InstanceCount;
            Assert.AreEqual(1, scene.SkinnedCount);

            Object.DestroyImmediate(avatar);
            spawned.Remove(avatar);
            scene.Rescan(settings);

            Assert.AreEqual(withAvatar - 1, scene.InstanceCount,
                "The avatar you took off goes on occluding until its instances leave the structure: the geometry is a baked copy this class owns, so destroying the avatar does not take it with it.");
            Assert.AreEqual(0, scene.SkinnedCount,
                "A destroyed SkinnedMeshRenderer compares equal to null, so whether an entry is skinned has to be remembered as a flag - asking the component answers no exactly when the entry needs taking out of the re-bake list.");
        }

        [Test]
        public void ADestroyedAvatarLeavesBeforeTheNextRescanIsDue()
        {
            GameObject avatar = SkinnedAvatar("BasisRTAOSwapAvatarBetweenScans", new Vector3(1f, 0f, 0f));

            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.skinnedMode = BasisRTAOSkinnedMode.Dynamic;
            settings.rescanInterval = 10000f;

            scene.Refresh(settings, Vector3.zero, 1f, 900);
            int withAvatar = scene.InstanceCount;
            Assert.Greater(withAvatar, 0);

            Object.DestroyImmediate(avatar);
            spawned.Remove(avatar);
            scene.Refresh(settings, Vector3.zero, 2f, 901);

            Assert.AreEqual(withAvatar - 1, scene.InstanceCount,
                "Avatars are destroyed the moment they are swapped, which is almost never on a scan boundary. Waiting for the interval leaves the old body occluding for up to that long.");
            Assert.AreEqual(0, scene.SkinnedCount);
        }

        [Test]
        public void DisposeReleasesEverything()
        {
            Cube("BasisRTAOSceneA", Vector3.zero);
            scene.Rescan(BasisRTAOTestSettings.EveryLayer);
            Assert.Greater(scene.InstanceCount, 0);

            scene.Dispose();
            Assert.AreEqual(0, scene.InstanceCount);
            Assert.IsNull(scene.AccelerationStructure);
            scene = null;
        }

        private void BuildOnce()
        {
            CommandBuffer cmd = new CommandBuffer { name = "BasisRTAOSceneTestBuild" };
            try
            {
                scene.Build(cmd);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }
        }
    }
}
