using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.TestTools;

[Category("Graphics Tools")]

class SpeedTreeBillboardMaterialUpgraderTest : MaterialUpgraderTestBase<SpeedTreeBillboardUpgrader>
{
    [OneTimeSetUp]
    public override void OneTimeSetUp()
    {
        m_Upgrader = new SpeedTreeBillboardUpgrader("Nature/SpeedTree Billboard");
    }

    public SpeedTreeBillboardMaterialUpgraderTest() : base("Nature/SpeedTree Billboard",
        "Universal Render Pipeline/Nature/SpeedTree7 Billboard")
    {
    }

    [Test]
    [TestCaseSource(nameof(MaterialUpgradeCases))]
    public void UpgradeSpeedTree7BillboardMaterial(MaterialUpgradeTestCase testCase)
    {
        base.UpgradeMaterial(testCase);
    }

    private static IEnumerable MaterialUpgradeCases()
    {
        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTreeBillboardMaterial_When_Upgrading_Then_ShaderIsUpgradedToSpeedTree7Billboard",
            setup = material =>
            {
                // No specific setup is needed for this test case, as we are only verifying that the shader is upgraded correctly.
            },
            verify = material =>
            {
                // Verify that the shader has been upgraded to the expected shader.
                Assert.AreEqual("Universal Render Pipeline/Nature/SpeedTree7 Billboard", material.shader.name);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialMainColorRed_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardMainColorIsRed",
            setup = material =>
            {
                // Set the main color to red before upgrading.
                material.SetColor("_Color", Color.red);
            },
            verify = material =>
            {
                // Verify that the main color is still red after upgrading.
                Assert.AreEqual(Color.red, material.GetColor("_Color"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialHueVariationGreen_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardHueVariationIsGreen",
            setup = material =>
            {
                // Set the hue variation to green before upgrading.
                material.SetColor("_HueVariation", Color.green);
            },
            verify = material =>
            {
                // Verify that the hue variation is still green after upgrading.
                Assert.AreEqual(Color.green, material.GetColor("_HueVariation"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialAlphaCutoff02_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardAlphaCutoffIs02",
            setup = material =>
            {
                // Set the alpha cutoff to 0.2 before upgrading.
                material.SetFloat("_Cutoff", 0.2f);
            },
            verify = material =>
            {
                // Verify that the alpha cutoff is still 0.2 after upgrading.
                Assert.AreEqual(0.2f, material.GetFloat("_Cutoff"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialWindQualityNone_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardWindQualityIsNone",
            setup = material =>
            {
                // Set the wind quality to none before upgrading.
                material.SetFloat("_WindQuality", 0.0f);
            },
            verify = material =>
            {
                // Verify that the wind quality is still none after upgrading.
                Assert.AreEqual(0.0f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialWindQualityFastest_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardWindQualityIsFastest",
            setup = material =>
            {
                // Set the wind quality to fastest before upgrading.
                material.SetFloat("_WindQuality", 1.0f);
            },
            verify = material =>
            {
                // Verify that the wind quality is still fastest after upgrading.
                Assert.AreEqual(1.0f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialGPUInstancingEnabled_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardGPUInstancingIsEnabled",
            setup = material =>
            {
                // Enable GPU instancing before upgrading.
                material.enableInstancing = true;
            },
            verify = material =>
            {
                // Verify that GPU instancing is still enabled after upgrading.
                Assert.IsTrue(material.enableInstancing);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTreeBillboardMaterialGPUInstancingDisabled_When_Upgrading_Then_TheShaderUniversalRenderPipelineSpeedTree7BillboardGPUInstancingIsDisabled",
            setup = material =>
            {
                // Disable GPU instancing before upgrading.
                material.enableInstancing = false;
            },
            verify = material =>
            {
                // Verify that GPU instancing is still disabled after upgrading.
                Assert.IsFalse(material.enableInstancing);
            }
        };
    }
}
