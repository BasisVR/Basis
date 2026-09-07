using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOSceneFilterTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null)
                    Object.DestroyImmediate(spawned[i]);
            }
            spawned.Clear();
        }

        private GameObject Cube()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            spawned.Add(go);
            return go;
        }

        [Test]
        public void NullRendererIsRejected()
        {
            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(null, settings));
        }

        [Test]
        public void PlainCubeIsAccepted()
        {
            Renderer renderer = Cube().GetComponent<Renderer>();
            Assert.IsTrue(BasisRTAOScene.ShouldInclude(renderer, BasisRTAOTestSettings.EveryLayer));
        }

        [Test]
        public void DisabledRendererIsRejected()
        {
            Renderer renderer = Cube().GetComponent<Renderer>();
            renderer.enabled = false;
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(renderer, BasisRTAOTestSettings.EveryLayer));
        }

        [Test]
        public void InactiveGameObjectIsRejected()
        {
            GameObject go = Cube();
            go.SetActive(false);
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(go.GetComponent<Renderer>(), BasisRTAOTestSettings.EveryLayer));
        }

        [Test]
        public void LayerMaskExcludesRenderer()
        {
            GameObject go = Cube();
            go.layer = 9;
            BasisRTAOSceneSettings settings = BasisRTAOTestSettings.EveryLayer;
            settings.layerMask = ~(1 << 9);
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(go.GetComponent<Renderer>(), settings));

            settings.layerMask = 1 << 9;
            Assert.IsTrue(BasisRTAOScene.ShouldInclude(go.GetComponent<Renderer>(), settings));
        }

        [Test]
        public void ShadowCastingOffIsRejectedOnlyWhenRequired()
        {
            Renderer renderer = Cube().GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            BasisRTAOSceneSettings requiring = BasisRTAOTestSettings.EveryLayer;
            requiring.requireShadowCasting = true;
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(renderer, requiring));

            BasisRTAOSceneSettings permissive = BasisRTAOTestSettings.EveryLayer;
            permissive.requireShadowCasting = false;
            Assert.IsTrue(BasisRTAOScene.ShouldInclude(renderer, permissive));
        }

        [Test]
        public void ShadowsOnlyRendererStillOccludes()
        {
            Renderer renderer = Cube().GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            Assert.IsTrue(BasisRTAOScene.ShouldInclude(renderer, BasisRTAOTestSettings.EveryLayer));
        }

        [Test]
        public void ExcludeComponentOptsOut()
        {
            GameObject go = Cube();
            go.AddComponent<BasisRTAOExclude>();
            Assert.IsFalse(BasisRTAOScene.ShouldInclude(go.GetComponent<Renderer>(), BasisRTAOTestSettings.EveryLayer));
        }

        [Test]
        public void MeshRendererIsAlwaysASupportedType()
        {
            Renderer renderer = Cube().GetComponent<Renderer>();
            Assert.IsTrue(BasisRTAOScene.IsSupportedRendererType(renderer, BasisRTAOSkinnedMode.Off));
            Assert.IsTrue(BasisRTAOScene.IsSupportedRendererType(renderer, BasisRTAOSkinnedMode.Proxy));
        }

        [Test]
        public void SkinnedRendererIsNeverASupportedType()
        {
            GameObject go = new GameObject("skinned", typeof(SkinnedMeshRenderer));
            spawned.Add(go);
            Renderer renderer = go.GetComponent<SkinnedMeshRenderer>();

            // An avatar reaches the structure as proxy capsules, never as its own deforming mesh, so no
            // skinned renderer is registered in either mode. Proxy answering false here is the whole point:
            // a registered skinned mesh would be a body that has to be re-baked to stay in its own pose.
            Assert.IsFalse(BasisRTAOScene.IsSupportedRendererType(renderer, BasisRTAOSkinnedMode.Off));
            Assert.IsFalse(BasisRTAOScene.IsSupportedRendererType(renderer, BasisRTAOSkinnedMode.Proxy));
        }

        [Test]
        public void LineRendererIsNotASupportedType()
        {
            GameObject go = new GameObject("line", typeof(LineRenderer));
            spawned.Add(go);
            Assert.IsFalse(BasisRTAOScene.IsSupportedRendererType(go.GetComponent<LineRenderer>(), BasisRTAOSkinnedMode.Proxy));
        }

        [Test]
        public void ResolveMeshReadsMeshFilter()
        {
            GameObject go = Cube();
            Mesh mesh = BasisRTAOScene.ResolveMesh(go.GetComponent<Renderer>());
            Assert.IsNotNull(mesh);
            Assert.AreSame(go.GetComponent<MeshFilter>().sharedMesh, mesh);
        }

        [Test]
        public void ResolveMeshReadsSkinnedSharedMesh()
        {
            GameObject go = new GameObject("skinned", typeof(SkinnedMeshRenderer));
            spawned.Add(go);
            SkinnedMeshRenderer skinned = go.GetComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Assert.AreSame(skinned.sharedMesh, BasisRTAOScene.ResolveMesh(skinned));
        }

        [Test]
        public void ResolveMeshHandlesMissingFilter()
        {
            GameObject go = new GameObject("bare", typeof(MeshRenderer));
            spawned.Add(go);
            Assert.IsNull(BasisRTAOScene.ResolveMesh(go.GetComponent<MeshRenderer>()));
        }

        [Test]
        public void IsUsableMeshRejectsNullAndEmpty()
        {
            Assert.IsFalse(BasisRTAOScene.IsUsableMesh(null));

            Mesh empty = new Mesh();
            try
            {
                Assert.IsFalse(BasisRTAOScene.IsUsableMesh(empty));
            }
            finally
            {
                Object.DestroyImmediate(empty);
            }
        }

        [Test]
        public void IsUsableMeshAcceptsPrimitive()
        {
            Assert.IsTrue(BasisRTAOScene.IsUsableMesh(Cube().GetComponent<MeshFilter>().sharedMesh));
        }
    }
}
