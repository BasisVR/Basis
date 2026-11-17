using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEngine;

public class BasisMaterialPropertyBlockStreamingTests
{
    [Test]
    public void RuntimeServiceCanBeConstructedWithoutNativeFieldInitializers()
    {
        var host = new GameObject("PropertyBlockStreamingConstructionTest");
        try { Assert.That(host.AddComponent<BasisMaterialPropertyBlockStreaming>(), Is.Not.Null); }
        finally { Object.DestroyImmediate(host); }
    }

    [Test]
    public void DemandChangesWithDistanceResolutionAndTiling()
    {
        // A 1024-square texture spanning a unit of UV/world area projects to 256 pixels.
        int baseline = BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 256f * 256f, 0f, 11);
        Assert.That(baseline, Is.EqualTo(2));
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 16f, 1f, 256f * 256f, 0f, 11), Is.EqualTo(baseline + 2), "Four times farther away needs two fewer detail levels.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 512f * 512f, 0f, 11), Is.EqualTo(baseline - 1), "Doubling camera resolution needs one more detail level.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 0.25f, 256f * 256f, 0f, 11), Is.EqualTo(baseline + 1), "Doubling UV tiling uses a smaller source-texture footprint per pixel.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 256f * 256f, 1f, 11), Is.EqualTo(baseline + 1));
    }

    [Test]
    public void DemandStaysWithinAvailableMips()
    {
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 0f, 1f, 256f * 256f, 0f, 11), Is.Zero);
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1e20f, 1f, 1f, 0f, 11), Is.EqualTo(10));
    }

    [Test]
    public void PerMaterialBlockTakesPrecedenceEvenWhenItOnlyOverridesAFloat()
    {
        var rendererBlock = new MaterialPropertyBlock();
        var indexedBlock = new MaterialPropertyBlock();
        var rendererTexture = new Texture2D(2, 2);
        var indexedTexture = new Texture2D(2, 2);
        int textureId = Shader.PropertyToID("_MainTex");
        try
        {
            rendererBlock.SetTexture(textureId, rendererTexture);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(rendererTexture));
            indexedBlock.SetFloat("_Cutoff", 0.5f);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.Null, "Unity does not merge the renderer block into a nonempty indexed block.");
            indexedBlock.SetTexture(textureId, indexedTexture);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(indexedTexture));
            indexedBlock.Clear();
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(rendererTexture));
        }
        finally
        {
            Object.DestroyImmediate(rendererTexture);
            Object.DestroyImmediate(indexedTexture);
        }
    }
}
