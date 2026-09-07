using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOGpuHarness : IDisposable
    {
        public const string TestKernelPath = "Packages/com.basis.rtao/Tests/Shaders/BasisRTAOTestKernels.compute";

        private readonly List<GraphicsBuffer> buffers = new List<GraphicsBuffer>();
        private readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();

        public ComputeShader Kernels { get; }

        public BasisRTAOGpuHarness()
        {
            Kernels = AssetDatabase.LoadAssetAtPath<ComputeShader>(TestKernelPath);
            Assert.IsNotNull(Kernels, $"{TestKernelPath} failed to import.");
        }

        public static void SkipUnlessComputeIsAvailable()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("This device has no compute shader support, so the GPU tests cannot run.");
        }

        public GraphicsBuffer Track(GraphicsBuffer buffer)
        {
            buffers.Add(buffer);
            return buffer;
        }

        public T Track<T>(T target) where T : UnityEngine.Object
        {
            objects.Add(target);
            return target;
        }

        public GraphicsBuffer InputBuffer(Vector4[] values)
        {
            GraphicsBuffer buffer = Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, values.Length), sizeof(float) * 4));
            buffer.SetData(values);
            return buffer;
        }

        public GraphicsBuffer OutputBuffer(int count)
        {
            return Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), sizeof(float) * 4));
        }

        public Vector4[] RunLinearKernel(string kernelName, Vector4[] input, int outputCount, Action<ComputeShader, int> configure = null)
        {
            int kernel = Kernels.FindKernel(kernelName);
            Assert.GreaterOrEqual(kernel, 0, $"kernel {kernelName} is missing.");

            GraphicsBuffer output = OutputBuffer(outputCount);
            if (input != null)
                Kernels.SetBuffer(kernel, "_TestInput", InputBuffer(input));
            else
                Kernels.SetBuffer(kernel, "_TestInput", InputBuffer(new[] { Vector4.zero }));

            Kernels.SetBuffer(kernel, "_TestOutput", output);
            Kernels.SetInt("_TestCount", outputCount);
            configure?.Invoke(Kernels, kernel);

            Kernels.Dispatch(kernel, Mathf.Max(1, (outputCount + 63) / 64), 1, 1);

            Vector4[] results = new Vector4[outputCount];
            output.GetData(results);
            return results;
        }

        public Vector4[] ReadTextureArray(Texture texture, int width, int height, int slices)
        {
            int kernel = Kernels.FindKernel("TestStereoSlices");
            GraphicsBuffer output = OutputBuffer(width * height * slices);

            Kernels.SetTexture(kernel, "_TestSliceTex", texture);
            Kernels.SetBuffer(kernel, "_TestOutput", output);
            Kernels.SetVector("_TestParams", new Vector4(width, height, slices, 0f));
            Kernels.Dispatch(kernel, Mathf.Max(1, (width + 7) / 8), Mathf.Max(1, (height + 7) / 8), Mathf.Max(1, slices));

            Vector4[] results = new Vector4[width * height * slices];
            output.GetData(results);
            return results;
        }

        public void Dispose()
        {
            for (int i = 0; i < buffers.Count; i++)
                buffers[i]?.Dispose();
            buffers.Clear();

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] == null)
                    continue;
                if (objects[i] is RenderTexture renderTexture)
                    renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(objects[i]);
            }
            objects.Clear();
        }
    }
}
