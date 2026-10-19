using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.TestTools;

[Category("Graphics Tools")]

class SpeedTree8MaterialUpgraderTest : MaterialUpgraderTestBase<UniversalSpeedTree8Upgrader>
{
    [OneTimeSetUp]
    public override void OneTimeSetUp()
    {
        m_Upgrader = new UniversalSpeedTree8Upgrader("Nature/SpeedTree8");
    }

    public SpeedTree8MaterialUpgraderTest() : base("Nature/SpeedTree8",
        "Universal Render Pipeline/Nature/SpeedTree8_PBRLit")
    {
    }

    [Test]
    [TestCaseSource(nameof(MaterialUpgradeCases))]
    public void UpgradeSpeedTree8Material(MaterialUpgradeTestCase testCase)
    {
        base.UpgradeMaterial(testCase);
    }

    private static IEnumerable MaterialUpgradeCases()
    {
        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8Material_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLit",
            setup = material =>
            {
                // No specific setup is needed for this test case, as we are only verifying that the shader is upgraded correctly.
            },
            verify = material =>
            {
                Assert.AreEqual("Universal Render Pipeline/Nature/SpeedTree8_PBRLit", material.shader.name);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialColorRed_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitColorRedRemains",
            setup = material =>
            {
                // Set the Color property to Red before upgrading.
                 material.SetColor("_Color", Color.red);
            },
            verify = material =>
            {
                // Verify that the Color property is still set to Red after upgrading.
                Assert.AreEqual(Color.red, material.GetColor("_Color"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialSmoothness01f_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitSmoothness01fRemains",
            setup = material =>
            {
                material.SetFloat("_Glossiness", 0.1f);
            },
            verify = material =>
            {
                Assert.AreEqual(0.1f, material.GetFloat("_Glossiness"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialMetallic01_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitMetallic01Remains",
            setup = material =>
            {
                material.SetFloat("_Metallic", 0.1f);
            },
            verify = material =>
            {
                Assert.AreEqual(0.1f, material.GetFloat("_Metallic"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialSubsurfaceColorBlue_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitSubsurfaceColorBlueRemains",
            setup = material =>
            {
                //set the subsurface color to blue
                material.SetColor("_SubsurfaceColor", Color.blue);
            },
            verify = material =>
            {
                //verify that the subsurface color is still set to blue after upgrading
                Assert.AreEqual(Color.blue, material.GetColor("_SubsurfaceColor"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityNone_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityNoneRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityFastest_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityFastestRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityFast_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityFastRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 2f);
            },
            verify = material =>
            {
                Assert.AreEqual(2f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityBetter_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityBetterRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 3f);
            },
            verify = material =>
            {
                Assert.AreEqual(3f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityBest_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityBestRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 4f);
            },
            verify = material =>
            {
                Assert.AreEqual(4f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialWindQualityPalm_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitWindQualityPalmRemains",
            setup = material =>
            {
                material.SetFloat("_WindQuality", 5f);
            },
            verify = material =>
            {
                Assert.AreEqual(5f, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialHueVariationEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitHueVariationEnabledRemains",
            setup = material =>
            {
                material.EnableKeyword("EFFECT_HUE_VARIATION");
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_HueVariationKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialHueVariationDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitHueVariationDisabledRemains",
            setup = material =>
            {
                material.DisableKeyword("EFFECT_HUE_VARIATION");
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_HueVariationKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialHueVariationColorRed_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitHueVariationColorRedRemains",
            setup = material =>
            {
                //set the hue variation color to red
                material.SetColor("_HueVariationColor", Color.red);
            },
            verify = material =>
            {
                Assert.AreEqual(Color.red, material.GetColor("_HueVariationColor"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialSubsurfaceEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitSubsurfaceEnabledRemains",
            setup = material =>
            {
                material.EnableKeyword("EFFECT_SUBSURFACE");
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_SubsurfaceKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialSubsurfaceDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitSubsurfaceDisabledRemains",
            setup = material =>
            {
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_SubsurfaceKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialBillboardEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitBillboardEnabledRemains",
            setup = material =>
            {
                material.SetFloat("_BillboardKwToggle", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("EFFECT_BILLBOARD"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree8MaterialBillboardDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitBillboardDisabledRemains",
            setup = material =>
            {
                material.SetFloat("_BillboardKwToggle", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("EFFECT_BILLBOARD"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree8MaterialGPUInstancingOn_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitGPUInstancingOnRemains",
            setup = material =>
            {
                // Enable GPU Instancing before upgrading.
                material.enableInstancing = true;
            },
            verify = material =>
            {
                // Verify that GPU Instancing is still enabled after upgrading.
                Assert.IsTrue(material.enableInstancing);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree8MaterialGPUInstancingOff_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree8_PBRLitGPUInstancingOffRemains",
            setup = material =>
            {
                // Disable GPU Instancing before upgrading.
                material.enableInstancing = false;
            },
            verify = material =>
            {
                // Verify that GPU Instancing is still disabled after upgrading.
                Assert.IsFalse(material.enableInstancing);
            }
        };
    }
}
