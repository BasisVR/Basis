using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Pins the batched gizmo backend (BasisGizmoManager): the line-mesh vertex layout that
    /// Unity actually accepts (the first in-editor run failed on attribute ordering — Color
    /// must precede TexCoords and the struct must match byte-for-byte), the ribbon quad
    /// pairing that keeps segments untwisted, the icosphere's Ø1 parity with the built-in
    /// sphere the old prefab used, and the id lifecycle the fifteen-odd consumer systems
    /// rely on. Sphere/line slots are plain data (no scene objects), so everything here
    /// runs in edit mode; text gizmos create GameObjects and are deliberately not covered.
    /// </summary>
    public class BasisGizmoBackendTests
    {
        [SetUp]
        public void SetUp()
        {
            BasisGizmoManager.DestroyAll();
        }

        [TearDown]
        public void TearDown()
        {
            BasisGizmoManager.DestroyAll();
        }

        // ── Line vertex layout — the exact thing that broke on first run ────

        [Test]
        public void LineVertexLayout_IsAcceptedByUnity()
        {
            // SetVertexBufferParams throws on non-canonical attribute order
            // (Position, Normal, Tangent, Color, TexCoord0..N). This is the regression
            // test for the ArgumentException that killed all line rendering.
            Mesh mesh = new Mesh();
            try
            {
                Assert.DoesNotThrow(() => mesh.SetVertexBufferParams(4, BasisGizmoManager.LineVertexLayout));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void LineVertexStruct_MatchesDeclaredLayoutStride()
        {
            int declared = 0;
            foreach (VertexAttributeDescriptor descriptor in BasisGizmoManager.LineVertexLayout)
            {
                int component = descriptor.format == VertexAttributeFormat.UNorm8 ? 1 : 4;
                declared += component * descriptor.dimension;
            }
            Assert.AreEqual(36, declared);
            Assert.AreEqual(declared, Marshal.SizeOf<BasisGizmoManager.LineVertex>());
        }

        // ── Ribbon quad construction ────────────────────────────────────────

        [Test]
        public void AppendSegmentVertices_PairsSidesSoTheQuadCannotTwist()
        {
            var verts = new BasisGizmoManager.LineVertex[4];
            int cursor = 0;
            Vector3 a = new Vector3(1f, 2f, 3f);
            Vector3 b = new Vector3(4f, 5f, 6f);
            Color32 colorA = new Color32(255, 0, 0, 255);
            Color32 colorB = new Color32(0, 255, 0, 255);

            BasisGizmoManager.AppendSegmentVertices(verts, ref cursor, a, b, colorA, colorB, 0.25f);

            Assert.AreEqual(4, cursor);

            // Verts 0/1 anchor at a and carry b; verts 2/3 anchor at b and carry a.
            Assert.AreEqual(a, verts[0].Position);
            Assert.AreEqual(b, verts[0].OtherEnd);
            Assert.AreEqual(a, verts[1].Position);
            Assert.AreEqual(b, verts[2].Position);
            Assert.AreEqual(a, verts[2].OtherEnd);
            Assert.AreEqual(b, verts[3].Position);

            // The shader offset flips with segment direction, so the b-end side signs are
            // mirrored: (-1, +1) at a pairs with (+1, -1) at b to land on matching sides.
            Assert.AreEqual(-1f, verts[0].SideWidth.x);
            Assert.AreEqual(1f, verts[1].SideWidth.x);
            Assert.AreEqual(1f, verts[2].SideWidth.x);
            Assert.AreEqual(-1f, verts[3].SideWidth.x);

            // Per-point colors: a-verts carry colorA, b-verts carry colorB (gradient support).
            Assert.AreEqual(colorA, verts[0].Color);
            Assert.AreEqual(colorA, verts[1].Color);
            Assert.AreEqual(colorB, verts[2].Color);
            Assert.AreEqual(colorB, verts[3].Color);

            // Width rides every vert.
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(0.25f, verts[i].SideWidth.y);
            }
        }

        [Test]
        public void FillQuadIndices_ProducesSharedDiagonalTriangles()
        {
            uint[] indices = new uint[12];
            BasisGizmoManager.FillQuadIndices(indices, 2);

            // Quad 0: (0,1,2) + (2,1,3) — both triangles share the 1–2 diagonal.
            CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 2, 1, 3 }, new[] { indices[0], indices[1], indices[2], indices[3], indices[4], indices[5] });
            // Quad 1: same pattern shifted by 4 verts.
            CollectionAssert.AreEqual(new uint[] { 4, 5, 6, 6, 5, 7 }, new[] { indices[6], indices[7], indices[8], indices[9], indices[10], indices[11] });
        }

        // ── Icosphere — must keep the old prefab's Ø1 sizing ────────────────

        [Test]
        public void SphereMesh_MatchesBuiltInSphereSizingAndShading()
        {
            Mesh mesh = BasisGizmoManager.BuildSphereMesh(markNoLongerReadable: false);
            try
            {
                // Two icosahedron subdivisions: 162 verts / 320 tris — about a third of the
                // 515-vert built-in sphere the old prefab drew per marker.
                Assert.AreEqual(162, mesh.vertexCount);
                Assert.AreEqual(320 * 3, mesh.triangles.Length);

                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Assert.AreEqual(vertices.Length, normals.Length);
                for (int i = 0; i < vertices.Length; i++)
                {
                    // Radius 0.5 everywhere = Ø1, the same sizing convention as the built-in
                    // sphere, so CreateSphereGizmo(size) means world diameter == size.
                    Assert.AreEqual(0.5f, vertices[i].magnitude, 1e-4f);
                    // Analytic sphere normals: unit length, parallel to the vertex direction
                    // (the lit solid style shades wrong without them).
                    Assert.AreEqual(1f, normals[i].magnitude, 1e-4f);
                    Assert.AreEqual(1f, Vector3.Dot(normals[i], vertices[i].normalized), 1e-3f);
                }
                Assert.AreEqual(Vector3.one, mesh.bounds.size);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        // ── Id lifecycle (sphere/line slots are pure data — no scene objects) ──

        [Test]
        public void SphereGizmo_CreateExistsDestroy_Roundtrip()
        {
            Assert.IsTrue(BasisGizmoManager.CreateSphereGizmo("test", out int id, Vector3.one, 0.1f, Color.red));
            Assert.Greater(id, 0);
            Assert.IsTrue(BasisGizmoManager.Exists(id));
            Assert.IsTrue(BasisGizmoManager.UpdateSphereGizmo(id, Vector3.zero, Vector3.one * 0.2f));
            Assert.IsTrue(BasisGizmoManager.UpdateSphereGizmo(id, Vector3.zero, Quaternion.identity, Vector3.one));
            Assert.IsTrue(BasisGizmoManager.UpdateGizmoColor(id, Color.blue));

            BasisGizmoManager.DestroyGizmo(id);
            Assert.IsFalse(BasisGizmoManager.Exists(id));
        }

        [Test]
        public void SolidSphereGizmo_SharesTheSphereLifecycle()
        {
            Assert.IsTrue(BasisGizmoManager.CreateSolidSphereGizmo("marker", out int id, Vector3.one, 0.05f));
            Assert.IsTrue(BasisGizmoManager.Exists(id));
            Assert.IsTrue(BasisGizmoManager.UpdateSphereGizmo(id, Vector3.up, Vector3.one * 0.05f));
            BasisGizmoManager.DestroyGizmo(id);
            Assert.IsFalse(BasisGizmoManager.Exists(id));
        }

        [Test]
        public void LineGizmo_CreateUpdateResizeGradient_Roundtrip()
        {
            Assert.IsTrue(BasisGizmoManager.CreateLineGizmo("line", out int id, Vector3.zero, Vector3.one, 0.01f, Color.white));
            Assert.IsTrue(BasisGizmoManager.Exists(id));
            Assert.IsTrue(BasisGizmoManager.UpdateLineGizmo(id, Vector3.one, Vector3.zero));

            // Point-count changes rebuild the slot in place under the same id.
            var ring = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one };
            Assert.IsTrue(BasisGizmoManager.UpdateLineGizmo(id, ring));

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.blue, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            Assert.IsTrue(BasisGizmoManager.SetLineGizmoGradient(id, gradient));
            Assert.IsTrue(BasisGizmoManager.UpdateGizmoColor(id, Color.green));

            BasisGizmoManager.DestroyGizmo(id);
            Assert.IsFalse(BasisGizmoManager.Exists(id));
        }

        [Test]
        public void MultiPointLineGizmo_CreatesWithLoop()
        {
            var points = new[] { Vector3.zero, Vector3.right, Vector3.up };
            Assert.IsTrue(BasisGizmoManager.CreateLineGizmo("ring", out int id, points, 0.01f, Color.cyan, loop: true));
            Assert.IsTrue(BasisGizmoManager.Exists(id));
        }

        [Test]
        public void LegacyExplicitIdLine_DoesNotCollideWithGeneratedIds()
        {
            // The legacy overload registers under a caller-chosen id; the id counter must
            // advance past it so the next generated id can't silently overwrite the slot.
            Assert.IsTrue(BasisGizmoManager.CreateLineGizmo("legacy", 5000, Vector3.zero, Vector3.one, 0.01f, Color.white, null));
            Assert.IsTrue(BasisGizmoManager.Exists(5000));

            Assert.IsTrue(BasisGizmoManager.CreateSphereGizmo("after", out int nextId, Vector3.zero, 0.1f, Color.white));
            Assert.Greater(nextId, 5000);
            Assert.IsTrue(BasisGizmoManager.Exists(5000));
            Assert.IsTrue(BasisGizmoManager.Exists(nextId));
        }

        [Test]
        public void UpdateOnMissingId_ReturnsFalse()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.IsFalse(BasisGizmoManager.UpdateSphereGizmo(123456, Vector3.zero, Vector3.one));
                Assert.IsFalse(BasisGizmoManager.UpdateLineGizmo(123456, Vector3.zero, Vector3.one));
                Assert.IsFalse(BasisGizmoManager.UpdateGizmoColor(123456, Color.red));
                Assert.IsFalse(BasisGizmoManager.Exists(123456));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void SetGizmoActive_TogglesWithoutDestroying()
        {
            BasisGizmoManager.CreateSphereGizmo("s", out int sphereId, Vector3.zero, 0.1f, Color.white);
            BasisGizmoManager.CreateLineGizmo("l", out int lineId, Vector3.zero, Vector3.one, 0.01f, Color.white);

            BasisGizmoManager.SetGizmoActive(sphereId, false);
            BasisGizmoManager.SetGizmoActive(lineId, false);
            Assert.IsTrue(BasisGizmoManager.Exists(sphereId));
            Assert.IsTrue(BasisGizmoManager.Exists(lineId));

            BasisGizmoManager.SetGizmoActive(sphereId, true);
            BasisGizmoManager.SetGizmoActive(lineId, true);
            Assert.IsTrue(BasisGizmoManager.Exists(sphereId));
            Assert.IsTrue(BasisGizmoManager.Exists(lineId));
        }

        [Test]
        public void DestroyAll_ClearsEverySlot()
        {
            BasisGizmoManager.CreateSphereGizmo("s", out int sphereId, Vector3.zero, 0.1f, Color.white);
            BasisGizmoManager.CreateLineGizmo("l", out int lineId, Vector3.zero, Vector3.one, 0.01f, Color.white);
            BasisGizmoManager.CreateSolidSphereGizmo("m", out int solidId, Vector3.zero, 0.05f);

            BasisGizmoManager.DestroyAll();

            Assert.IsFalse(BasisGizmoManager.Exists(sphereId));
            Assert.IsFalse(BasisGizmoManager.Exists(lineId));
            Assert.IsFalse(BasisGizmoManager.Exists(solidId));
        }

        [Test]
        public void SlotReuse_AfterDestroy_KeepsIdsDistinct()
        {
            BasisGizmoManager.CreateSphereGizmo("a", out int first, Vector3.zero, 0.1f, Color.white);
            BasisGizmoManager.DestroyGizmo(first);
            BasisGizmoManager.CreateSphereGizmo("b", out int second, Vector3.zero, 0.1f, Color.white);

            // The freed slot may be recycled, but the public id must never be.
            Assert.AreNotEqual(first, second);
            Assert.IsFalse(BasisGizmoManager.Exists(first));
            Assert.IsTrue(BasisGizmoManager.Exists(second));
        }

        // ── Label color diffing ─────────────────────────────────────────────

        [Test]
        public void LabelColors_WithinTolerance_CountAsUnchanged()
        {
            Color baseline = new Color(0.30f, 0.90f, 0.35f, 1f);
            Assert.IsTrue(BasisTextGizmos.ColorsApproximatelyEqual(baseline, new Color(0.31f, 0.90f, 0.35f, 1f)));
            Assert.IsFalse(BasisTextGizmos.ColorsApproximatelyEqual(baseline, new Color(0.33f, 0.90f, 0.35f, 1f)));
            Assert.IsFalse(BasisTextGizmos.ColorsApproximatelyEqual(baseline, new Color(0.30f, 0.90f, 0.35f, 0.5f)));
        }
    }
}
