using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// The surface read runs over every instance in the structure whenever the scene re-reads its
    /// materials, so both of the things it does per instance were hoisted out of the per sub-mesh loop:
    /// whether the renderer carries a property block at all, and what its whole-renderer block holds.
    /// A hoist is only sound if a sub-mesh still resolves the same block it did when each one asked for
    /// itself, and the interesting case is a renderer where one slot has its own block and another does
    /// not - which is exactly the case the old per slot fallback covered by re-reading.
    /// </summary>
    public class BasisGlobalIlluminationSurfaceReadTests
    {
        private static Shader LitShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        }

        private static string BaseColorName(Material material)
        {
            return material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
        }

        [Test]
        public void APerSlotBlockWinsOverTheRendererWideBlockOnItsOwnSlot()
        {
            Shader shader = LitShader();
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = new Material(shader);
            try
            {
                material.SetColor(BaseColorName(material), Color.white);
                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { material, material };

                MaterialPropertyBlock wide = new MaterialPropertyBlock();
                wide.SetColor(BaseColorName(material), new Color(0.9f, 0.1f, 0.1f));
                renderer.SetPropertyBlock(wide);

                MaterialPropertyBlock perSlot = new MaterialPropertyBlock();
                perSlot.SetColor(BaseColorName(material), new Color(0.1f, 0.9f, 0.1f));
                renderer.SetPropertyBlock(perSlot, 0);

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 0, settings, null, out Color slotZero, out Color _);
                Assert.Greater(slotZero.g, slotZero.r, "slot 0 carries its own block and that block is green");

                // The slot with no block of its own has to fall through to the renderer-wide one, which is
                // the read the hoist replaced.
                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 1, settings, null, out Color slotOne, out Color _);
                Assert.Greater(slotOne.r, slotOne.g, "slot 1 has no block of its own and must fall back to the renderer-wide red");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ARendererWithNoBlockReadsTheMaterialUnchanged()
        {
            Shader shader = LitShader();
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = new Material(shader);
            try
            {
                material.SetColor(BaseColorName(material), new Color(0.25f, 0.5f, 0.75f));
                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 0, settings, null, out Color withRenderer, out Color _);
                BasisGlobalIlluminationRayScene.ReadSurface(material, settings, null, out Color withoutRenderer, out Color _);

                Assert.AreEqual(withoutRenderer.r, withRenderer.r, 1e-5f);
                Assert.AreEqual(withoutRenderer.g, withRenderer.g, 1e-5f);
                Assert.AreEqual(withoutRenderer.b, withRenderer.b, 1e-5f);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// The version this class bumps is what makes the scene re-read every material it holds, so a
        /// texture whose average came back the same number it came back last time must not bump it. That
        /// is the whole of the difference between a rescan-cadence walk and a per frame one.
        /// </summary>
        [Test]
        public void AnAverageThatDidNotMoveIsNotAChange()
        {
            Color held = new Color(0.4f, 0.6f, 0.2f, 1f);
            Assert.IsFalse(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, held));
            Assert.IsFalse(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, new Color(0.4f + 1e-6f, 0.6f, 0.2f, 1f)),
                "float noise off a re-blitted mip chain is not a change");
        }

        [Test]
        public void AnAverageThatMovedInAnyChannelIsAChange()
        {
            Color held = new Color(0.4f, 0.6f, 0.2f, 1f);
            Assert.IsTrue(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, new Color(0.5f, 0.6f, 0.2f, 1f)));
            Assert.IsTrue(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, new Color(0.4f, 0.7f, 0.2f, 1f)));
            Assert.IsTrue(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, new Color(0.4f, 0.6f, 0.3f, 1f)));
            Assert.IsTrue(BasisGlobalIlluminationRayTextureAverage.AverageChanged(held, new Color(0.4f, 0.6f, 0.2f, 0.5f)));
        }

        /// <summary>
        /// A texture that has never been read has no average to keep, so it queues regardless of type -
        /// the TTL split only decides how often a resolved one is read AGAIN.
        /// </summary>
        [Test]
        public void AnUnresolvedTextureQueuesWhicheverTypeItIs()
        {
            BasisGlobalIlluminationRayTextureAverage average = new BasisGlobalIlluminationRayTextureAverage();
            Texture2D flat = new Texture2D(4, 4);
            RenderTexture live = new RenderTexture(4, 4, 0);
            try
            {
                Assert.AreEqual(Color.white, average.Get(flat), "an unread texture reads as white, not as black");
                Assert.AreEqual(1, average.QueuedCount);
                average.Get(live);
                Assert.AreEqual(2, average.QueuedCount);
            }
            finally
            {
                average.Dispose();
                Object.DestroyImmediate(flat);
                live.Release();
                Object.DestroyImmediate(live);
            }
        }
    }
}
