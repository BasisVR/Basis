using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.TestTools;

[Category("Graphics Tools")]

class SpeedTree7MaterialUpgraderTest : MaterialUpgraderTestBase<SpeedTreeUpgrader>
{
    [OneTimeSetUp]
    public override void OneTimeSetUp()
    {
        m_Upgrader = new SpeedTreeUpgrader("Nature/SpeedTree");
    }

    public SpeedTree7MaterialUpgraderTest() : base("Nature/SpeedTree",
        "Universal Render Pipeline/Nature/SpeedTree7")
    {
    }

    [Test]
    [TestCaseSource(nameof(MaterialUpgradeCases))]
    public void UpgradeSpeedTree7Material(MaterialUpgradeTestCase testCase)
    {
        base.UpgradeMaterial(testCase);
    }

    private static IEnumerable MaterialUpgradeCases()
    {
        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree7Material_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7",
            setup = material =>
            {
                // No specific setup is needed for this test case, as we are only verifying that the shader is upgraded correctly.
            },
            verify = material =>
            {
                // Verify that the shader has been upgraded to the expected SpeedTree7 shader.
                Assert.AreEqual("Universal Render Pipeline/Nature/SpeedTree7", material.shader.name);
            }

        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialHueVariationColorGreen_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7HueVariationColorGreenStay",
            setup = material =>
            {
                // Set the HueVariationColor property to Green before upgrading.
                material.SetColor("_HueVariation", Color.green);
            },
            verify = material =>
            {
                // Verify that the HueVariationColor property is still set to Green after upgrading.
                Assert.AreEqual(Color.green, material.GetColor("_HueVariation"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialColorRed_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7ColorRedStay",
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
            name = "Given_SpeedTree7MaterialCullBack_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7CullBackStay",
            setup = material =>
            {
                // Set the Cull property to Back before upgrading.
                material.SetFloat("_Cull", 2);
            },
            verify = material =>
            {
                // Verify that the Cull property is still set to Back after upgrading.
                Assert.AreEqual(2, material.GetFloat("_Cull"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialCullOff_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7CullOffStay",
            setup = material =>
            {
                // Set the Cull property to Off before upgrading.
                material.SetFloat("_Cull", 0);
            },
            verify = material =>
            {
                // Verify that the Cull property is still set to Off after upgrading.
                Assert.AreEqual(0, material.GetFloat("_Cull"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialCullFront_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7CullFrontStay",
            setup = material =>
            {
                // Set the Cull property to Front before upgrading.
                material.SetFloat("_Cull", 1);
            },
            verify = material =>
            {
                // Verify that the Cull property is still set to Front after upgrading.
                Assert.AreEqual(1, material.GetFloat("_Cull"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityNone_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityNoneStay",
            setup = material =>
            {
                // Set the WindQuality property to None before upgrading.
                material.SetFloat("_WindQuality", 0);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to None after upgrading.
                Assert.AreEqual(0, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityFastest_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityFastestStay",
            setup = material =>
            {
                // Set the WindQuality property to Fastest before upgrading.
                material.SetFloat("_WindQuality", 1);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to Fastest after upgrading.
                Assert.AreEqual(1, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityFast_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityFastStay",
            setup = material =>
            {
                // Set the WindQuality property to Fast before upgrading.
                material.SetFloat("_WindQuality", 2);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to Fast after upgrading.
                Assert.AreEqual(2, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityBetter_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityBetterStay",
            setup = material =>
            {
                // Set the WindQuality property to Better before upgrading.
                material.SetFloat("_WindQuality", 3);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to Better after upgrading.
                Assert.AreEqual(3, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityBest_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityBestStay",
            setup = material =>
            {
                // Set the WindQuality property to Best before upgrading.
                material.SetFloat("_WindQuality", 4);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to Best after upgrading.
                Assert.AreEqual(4, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialWindQualityPalm_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7WindQualityPalmStay",
            setup = material =>
            {
                // Set the WindQuality property to Palm before upgrading.
                material.SetFloat("_WindQuality", 5);
            },
            verify = material =>
            {
                // Verify that the WindQuality property is still set to Palm after upgrading.
                Assert.AreEqual(5, material.GetFloat("_WindQuality"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name = "Given_SpeedTree7MaterialGPUInstancingOn_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7GPUInstancingOnStay",
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
            name = "Given_SpeedTree7MaterialGPUInstancingOff_When_Upgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree7GPUInstancingOffStay",
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
