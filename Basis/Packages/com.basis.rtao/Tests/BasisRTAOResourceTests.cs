using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOResourceTests
    {
        private const string PackageRoot = "Packages/com.basis.rtao/";
        private const string ShaderRoot = PackageRoot + "Shaders/";
        private const string ResourcesPath = PackageRoot + "BasisRTAOResources.asset";

        [Test]
        public void EveryShaderFileIsImported()
        {
            string[] paths =
            {
                ShaderRoot + "BasisRTAOCommon.hlsl",
                ShaderRoot + "BasisRTAOKernel.hlsl",
                ShaderRoot + "BasisRTAOPrepass.shader",
                ShaderRoot + "BasisRTAOComposite.shader",
                ShaderRoot + "BasisRTAODenoise.compute",
                ShaderRoot + "BasisRTAO.compute",
                ShaderRoot + "BasisRTAO.raytrace"
            };

            foreach (string path in paths)
                Assert.IsNotEmpty(AssetDatabase.AssetPathToGUID(path), $"{path} is missing from the package.");
        }

        [Test]
        public void PrepassShaderCompiles()
        {
            AssertShaderCompiles(ShaderRoot + "BasisRTAOPrepass.shader");
        }

        [Test]
        public void CompositeShaderCompiles()
        {
            AssertShaderCompiles(ShaderRoot + "BasisRTAOComposite.shader");
        }

        [Test]
        public void PrepassWritesTwoRenderTargets()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "BasisRTAOPrepass.shader");
            Assert.IsNotNull(shader);
            Assert.AreEqual(1, shader.passCount, "The prepass is a single MRT pass writing camera relative position and octahedral normal.");
        }

        [Test]
        public void CompositeHasResolveAndDebugPasses()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "BasisRTAOComposite.shader");
            Assert.IsNotNull(shader);
            Assert.AreEqual(3, shader.passCount);

            Material material = new Material(shader);
            try
            {
                // Each of these is drawn by index from C#, so inserting a pass silently repoints the others.
                // That is exactly what adding After Opaque did to the debug view.
                Assert.AreEqual(0, material.FindPass("BasisRTAOComposite"));
                Assert.AreEqual(BasisRTAOAfterOpaquePass.ShaderPass, material.FindPass("BasisRTAOAfterOpaque"));
                Assert.AreEqual(BasisRTAODebugPass.ShaderPass, material.FindPass("BasisRTAODebugView"));
                Assert.AreEqual("BasisRTAOComposite", material.GetPassName(0));
                Assert.AreEqual("BasisRTAOAfterOpaque", material.GetPassName(1));
                Assert.AreEqual("BasisRTAODebugView", material.GetPassName(2));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void DenoiseComputeExposesBothKernels()
        {
            ComputeShader denoise = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "BasisRTAODenoise.compute");
            Assert.IsNotNull(denoise);
            Assert.IsFalse(ShaderUtil.GetComputeShaderMessageCount(denoise) > 0 && HasComputeError(denoise), DescribeComputeErrors(denoise));

            int temporal = denoise.FindKernel("BasisRTAOTemporal");
            int blur = denoise.FindKernel("BasisRTAOBlur");
            Assert.GreaterOrEqual(temporal, 0);
            Assert.GreaterOrEqual(blur, 0);
            Assert.AreNotEqual(temporal, blur);
        }

        [Test]
        public void DenoiseKernelsUseAnEightByEightGroup()
        {
            ComputeShader denoise = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "BasisRTAODenoise.compute");
            denoise.GetKernelThreadGroupSizes(denoise.FindKernel("BasisRTAOTemporal"), out uint x, out uint y, out uint z);
            Assert.AreEqual(8u, x);
            Assert.AreEqual(8u, y);
            Assert.AreEqual(1u, z, "The dispatch uses one group per view in z, so the kernel must declare a z group of one.");
        }

        [Test]
        public void ComputeTraceBackendExposesTheRayGenKernel()
        {
            ComputeShader trace = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "BasisRTAO.compute");
            Assert.IsNotNull(trace, "The compute fallback of the trace shader failed to import.");
            Assert.IsFalse(HasComputeError(trace), DescribeComputeErrors(trace));
            Assert.GreaterOrEqual(trace.FindKernel("MainRayGenShader"), 0);
            Assert.GreaterOrEqual(trace.FindKernel("ComputeIndirectDispatchDims"), 0);
        }

        [Test]
        public void ResourcesAssetExistsAndResolvesEveryShader()
        {
            BasisRTAOResources resources = AssetDatabase.LoadAssetAtPath<BasisRTAOResources>(ResourcesPath);
            Assert.IsNotNull(resources, $"{ResourcesPath} is missing.");

            resources.PopulateFromPackage();

            Assert.IsNotNull(resources.PrepassShader);
            Assert.IsNotNull(resources.CompositeShader);
            Assert.IsNotNull(resources.DenoiseShader);
            Assert.IsNotNull(resources.ComputeTraceShader);
            Assert.IsNotNull(resources.HardwareTraceShader, "The .raytrace asset did not import as a RayTracingShader.");
        }

        [Test]
        public void IsCompleteTracksTheBackendItIsAskedAbout()
        {
            BasisRTAOResources resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            try
            {
                Assert.IsFalse(resources.IsComplete(BasisRTAOBackend.Hardware));
                Assert.IsFalse(resources.IsComplete(BasisRTAOBackend.ScreenSpace));
                Assert.IsNotEmpty(resources.DescribeMissing(BasisRTAOBackend.Hardware));

                resources.PopulateFromPackage();
                Assert.IsTrue(resources.IsComplete(BasisRTAOBackend.Hardware), resources.DescribeMissing(BasisRTAOBackend.Hardware));
                Assert.IsTrue(resources.IsComplete(BasisRTAOBackend.ComputeBvh), resources.DescribeMissing(BasisRTAOBackend.ComputeBvh));
                Assert.IsTrue(resources.IsComplete(BasisRTAOBackend.ScreenSpace), resources.DescribeMissing(BasisRTAOBackend.ScreenSpace));
                Assert.IsEmpty(resources.DescribeMissing(BasisRTAOBackend.Hardware));
            }
            finally
            {
                Object.DestroyImmediate(resources);
            }
        }

        [Test]
        public void DescribeMissingNamesTheBackendSpecificShader()
        {
            BasisRTAOResources resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            try
            {
                StringAssert.Contains("hardwareTraceShader", resources.DescribeMissing(BasisRTAOBackend.Hardware));
                StringAssert.Contains("computeTraceShader", resources.DescribeMissing(BasisRTAOBackend.ComputeBvh));
                StringAssert.Contains("screenSpaceShader", resources.DescribeMissing(BasisRTAOBackend.ScreenSpace));
                StringAssert.DoesNotContain("computeTraceShader", resources.DescribeMissing(BasisRTAOBackend.Hardware));
                StringAssert.DoesNotContain("hardwareTraceShader", resources.DescribeMissing(BasisRTAOBackend.ScreenSpace));
            }
            finally
            {
                Object.DestroyImmediate(resources);
            }
        }

        private static void AssertShaderCompiles(string path)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.IsNotNull(shader, $"{path} did not import as a Shader.");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), DescribeShaderErrors(shader));
        }

        private static string DescribeShaderErrors(Shader shader)
        {
            int count = ShaderUtil.GetShaderMessageCount(shader);
            if (count == 0)
                return $"{shader.name} compiled cleanly.";

            UnityEditor.ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            System.Text.StringBuilder builder = new System.Text.StringBuilder($"{shader.name} reported {count} message(s):");
            for (int i = 0; i < messages.Length; i++)
                builder.Append($"\n  {messages[i].severity} {messages[i].file}:{messages[i].line} {messages[i].message}");
            return builder.ToString();
        }

        private static bool HasComputeError(ComputeShader shader)
        {
            UnityEditor.ShaderMessage[] messages = ShaderUtil.GetComputeShaderMessages(shader);
            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    return true;
            }
            return false;
        }

        private static string DescribeComputeErrors(ComputeShader shader)
        {
            UnityEditor.ShaderMessage[] messages = ShaderUtil.GetComputeShaderMessages(shader);
            if (messages.Length == 0)
                return $"{shader.name} compiled cleanly.";

            System.Text.StringBuilder builder = new System.Text.StringBuilder($"{shader.name} reported {messages.Length} message(s):");
            for (int i = 0; i < messages.Length; i++)
                builder.Append($"\n  {messages[i].severity} {messages[i].file}:{messages[i].line} {messages[i].message}");
            return builder.ToString();
        }
    }
}
