using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.TestTools;

[Category("Graphics Tools")]

class TerrainStandardMaterialUpgraderTest : MaterialUpgraderTestBase<TerrainUpgrader>
{
    [OneTimeSetUp]
    public override void OneTimeSetUp()
    {
        m_Upgrader = new TerrainUpgrader("Nature/Terrain/Standard");
    }

    public TerrainStandardMaterialUpgraderTest() : base("Nature/Terrain/Standard",
        "Universal Render Pipeline/Terrain/Lit")
    {
    }

    [Test]
    [TestCaseSource(nameof(MaterialUpgradeCases))]
    public void UpgradeTerrainStandardMaterial(MaterialUpgradeTestCase testCase)
    {
        base.UpgradeMaterial(testCase);
    }

    private static IEnumerable MaterialUpgradeCases()
    {
        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_TerrainStandardMaterial_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineTerrainLit",
            setup = material =>
            {
                // No specific setup is needed for this test case, as we are only verifying that the shader is upgraded correctly.
            },
            verify = material =>
            {
                Assert.AreEqual("Universal Render Pipeline/Terrain/Lit", material.shader.name);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_TerrainStandardMaterialEnabledInstancing_When_Upgrading_Then_TheInstancingEnabled",
            setup = material =>
            {
                material.enableInstancing = true;
            },
            verify = material =>
            {
                Assert.IsTrue(material.enableInstancing);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_TerrainStandardMaterialDisabledInstancing_When_Upgrading_Then_TheInstancingDisabled",
            setup = material =>
            {
                material.enableInstancing = false;
            },
            verify = material =>
            {
                Assert.IsFalse(material.enableInstancing);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_TerrainStandardMaterialLodFadeCrossFadeEnabled_When_Upgrading_Then_TheLodFadeCrossFadeDisabled",
            setup = material =>
            {
                material.EnableKeyword("LOD_FADE_CROSSFADE");
            },
            verify = material =>
            {
                Assert.IsFalse(material.IsKeywordEnabled("LOD_FADE_CROSSFADE"));
            }
        };
    }
}
