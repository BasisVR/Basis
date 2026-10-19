using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.TestTools;

[Category("Graphics Tools")]

class SpeedTree9MaterialUpgraderTest : MaterialUpgraderTestBase<UniversalSpeedTree9Upgrader>
{
    [OneTimeSetUp]
    public override void OneTimeSetUp()
    {
        m_Upgrader = new UniversalSpeedTree9Upgrader("Nature/SpeedTree9");
    }

    public SpeedTree9MaterialUpgraderTest() : base("Nature/SpeedTree9",
        "Universal Render Pipeline/Nature/SpeedTree9_URP")
    {
    }

    [Test]
    [TestCaseSource(nameof(MaterialUpgradeCases))]
    public void UpgradeSpeedTree9Material(MaterialUpgradeTestCase testCase)
    {
        base.UpgradeMaterial(testCase);
    }

    private static IEnumerable MaterialUpgradeCases()
    {
        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9Material_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URP",
            setup = material =>
            {
                // No specific setup is needed for this test case, as we are only verifying that the shader is upgraded correctly.
            },
            verify = material =>
            {
                Assert.AreEqual("Universal Render Pipeline/Nature/SpeedTree9_URP", material.shader.name);
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialColorRed_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPColorRed",
            setup = material =>
            {
                // set _ColorTint to red
                material.SetColor("_ColorTint", Color.red);
            },
            verify = material =>
            {
                Assert.AreEqual(Color.red, material.GetColor("_Color"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialSmoothness02f_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPSmoothness02f",
            setup = material =>
            {
                material.SetFloat("_Glossiness", 0.2f);
            },
            verify = material =>
            {
                Assert.AreEqual(0.2f, material.GetFloat("_Glossiness"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialMetallic02f_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPMetallic02f",
            setup = material =>
            {
                material.SetFloat("_Metallic", 0.2f);
            },
            verify = material =>
            {
                Assert.AreEqual(0.2f, material.GetFloat("_Metallic"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialSubsurfaceColorRed_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPSurfaceColorRed",
            setup = material =>
            {
                material.SetColor("_SubsurfaceColor", Color.red);
            },
            verify = material =>
            {
                Assert.AreEqual(Color.red, material.GetColor("_SubsurfaceColor"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialHueVariationEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_HueVariationEnabled",
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
                "Given_SpeedTree9MaterialHueVariationDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_HueVariationDisabled",
            setup = material =>
            {
                //no need because this is off by default
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_HueVariationKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialHueVariationColorRed_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_HueVariationColorRed",
            setup = material =>
            {
                //enable hue variation checkbox
                material.EnableKeyword("EFFECT_HUE_VARIATION");
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
                "Given_Speedtree9MaterialNormalMapEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_NormalMapEnabled",
            setup = material =>
            {
                material.EnableKeyword("EFFECT_BUMP");
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_NormalMapKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialNormalMapDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_NormalMapDisabled",
            setup = material =>
            {
                material.DisableKeyword("EFFECT_BUMP");
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_NormalMapKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialSubsurfaceEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPSubsurfaceEnabled",
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
                "Given_SpeedTree9MaterialSubsurfaceDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_URPSubsurfaceDisabled",
            setup = material =>
            {
                //no need to check, because it is off by default
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_SubsurfaceKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBillboardEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_BillboardEnabled",
            setup = material =>
            {
                material.EnableKeyword("EFFECT_BILLBOARD");
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_BillboardKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBillboardDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_BillboardDisabled",
            setup = material =>
            {
                material.DisableKeyword("EFFECT_BILLBOARD");
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_BillboardKwToggle"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialSharedMotionEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_MotionEnabled",
            setup = material =>
            {
                material.SetFloat("_WIND_SHARED", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_WIND_SHARED"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialSharedMotionDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_MotionDisabled",
            setup = material =>
            {
                material.SetFloat("_WIND_SHARED", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_WIND_SHARED"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBranch1MotionEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_Branch1MotionEnabled",
            setup = material =>
            {
                material.SetFloat("_WIND_BRANCH1", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_WIND_BRANCH1"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBranch1MotionDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_Branch1MotionDisabled",
            setup = material =>
            {
                material.SetFloat("_WIND_BRANCH1", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_WIND_BRANCH1"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBranch2MotionEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_Branch2MotionEnabled",
            setup = material =>
            {
                material.SetFloat("_WIND_BRANCH2", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_WIND_BRANCH2"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialBranch2MotionDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_Branch2MotionDisabled",
            setup = material =>
            {
                material.SetFloat("_WIND_BRANCH2", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_WIND_BRANCH2"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialRippleMotionEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_RippleMotionEnabled",
            setup = material =>
            {
                material.SetFloat("_WIND_RIPPLE", 1f);
            },
            verify = material =>
            {
                Assert.AreEqual(1f, material.GetFloat("_WIND_RIPPLE"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialRippleMotionDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_RippleMotionDisabled",
            setup = material =>
            {
                material.SetFloat("_WIND_RIPPLE", 0f);
            },
            verify = material =>
            {
                Assert.AreEqual(0f, material.GetFloat("_WIND_RIPPLE"));
            }
        };

        yield return new MaterialUpgradeTestCase
        {
            name =
                "Given_SpeedTree9MaterialGPUInstancingEnabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_GPUInstancingEnabled",
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
                "Given_SpeedTree9MaterialGPUInstancingDisabled_WhenUpgrading_Then_TheShaderUpgradedToUniversalRenderPipelineSpeedTree9_GPUInstancingDisabled",
            setup = material =>
            {
                material.enableInstancing = false;
            },
            verify = material =>
            {
                Assert.IsFalse(material.enableInstancing);
            }
        };
    }
}
