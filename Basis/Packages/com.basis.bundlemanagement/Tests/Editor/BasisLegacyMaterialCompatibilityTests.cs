using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public class BasisLegacyMaterialCompatibilityTests
{
    [Test]
    public void ReadsUnityVersionFromUnityFsHeader()
    {
        byte[] signature = Encoding.ASCII.GetBytes("UnityFS\0");
        byte[] version = Encoding.ASCII.GetBytes("5.x.x\0");
        byte[] revision = Encoding.ASCII.GetBytes("6000.5.10f1\0");
        byte[] bytes = new byte[signature.Length + 4 + version.Length + revision.Length];
        int offset = 0;
        signature.CopyTo(bytes, offset);
        offset += signature.Length;
        bytes[offset + 3] = 8;
        offset += 4;
        version.CopyTo(bytes, offset);
        offset += version.Length;
        revision.CopyTo(bytes, offset);

        Assert.IsTrue(BasisEncryptionToData.TryReadUnityVersion(bytes, out string parsed));
        Assert.AreEqual("6000.5.10f1", parsed);
    }

    [TestCase("2022.3.62f1", true)]
    [TestCase("6000.5.10f1", true)]
    [TestCase("6000.6.0f1", true)]
    [TestCase("6000.7.0a1", false)]
    [TestCase("6000.7.0b2", false)]
    [TestCase("6001.0.0a1", false)]
    [TestCase(null, true)]
    [TestCase("not-a-version", true)]
    public void SelectsOnlyPre67OrUnknownBundles(string unityVersion, bool expected)
    {
        Assert.AreEqual(expected, BasisLegacyMaterialCompatibility.RequiresLegacyEmissionUpgrade(unityVersion));
    }

    [Test]
    public void AddsDirectEmissionToLegacyMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            Assert.Ignore("URP Lit shader is unavailable in this test configuration.");

        GameObject gameObject = new GameObject("legacy-emission-test");
        Material material = new Material(shader);
        try
        {
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            // BakedEmission without EmissiveIsBlack is legacy emission intent.
            material.SetColor("_EmissionColor", Color.black);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmission;
            renderer.sharedMaterial = material;

            int upgraded = BasisLegacyMaterialCompatibility.UpgradeEmission(
                new List<Renderer> { renderer }, "6000.5.10f1");

            Assert.AreEqual(1, upgraded);
            MaterialGlobalIlluminationFlags expectedFlags =
                MaterialGlobalIlluminationFlags.RealtimeDirectEmission |
                MaterialGlobalIlluminationFlags.RealtimeIndirectEmission |
                MaterialGlobalIlluminationFlags.BakedEmission;
            Assert.AreEqual(expectedFlags, material.globalIlluminationFlags & expectedFlags);
            Assert.IsFalse((material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) != 0);
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void LeavesUnity67MaterialUntouched()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (shader == null)
            Assert.Ignore("URP Simple Lit shader is unavailable in this test configuration.");

        GameObject gameObject = new GameObject("repacked-legacy-emission-test");
        Material material = new Material(shader);
        try
        {
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmission;
            renderer.sharedMaterial = material;

            int upgraded = BasisLegacyMaterialCompatibility.UpgradeEmission(
                new List<Renderer> { renderer }, "6000.7.0b2");

            Assert.AreEqual(0, upgraded);
            Assert.AreEqual(MaterialGlobalIlluminationFlags.BakedEmission, material.globalIlluminationFlags);
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void LeavesNonEmissiveLegacyMaterialDisabled()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            Assert.Ignore("URP Lit shader is unavailable in this test configuration.");

        GameObject gameObject = new GameObject("stripped-version-emission-test");
        Material material = new Material(shader);
        try
        {
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            material.DisableKeyword("_EMISSION");
            renderer.sharedMaterial = material;

            int upgraded = BasisLegacyMaterialCompatibility.UpgradeEmission(
                new List<Renderer> { renderer }, "5.x.x");

            Assert.AreEqual(0, upgraded);
            Assert.AreEqual(MaterialGlobalIlluminationFlags.EmissiveIsBlack, material.globalIlluminationFlags);
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void LeavesStaleBakedBitDisabledWhenEmissiveIsBlack()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            Assert.Ignore("URP Lit shader is unavailable in this test configuration.");

        GameObject gameObject = new GameObject("stale-baked-emission-test");
        Material material = new Material(shader);
        try
        {
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmission |
                                               MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            material.DisableKeyword("_EMISSION");
            renderer.sharedMaterial = material;

            int upgraded = BasisLegacyMaterialCompatibility.UpgradeEmission(
                new List<Renderer> { renderer }, "6000.5.7f1");

            Assert.AreEqual(0, upgraded);
            Assert.AreEqual(
                MaterialGlobalIlluminationFlags.BakedEmission |
                MaterialGlobalIlluminationFlags.EmissiveIsBlack,
                material.globalIlluminationFlags);
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(gameObject);
        }
    }
}
