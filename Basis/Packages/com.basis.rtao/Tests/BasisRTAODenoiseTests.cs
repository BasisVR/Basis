using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAODenoiseTests
    {
        private const int Width = 8;
        private const int Height = 8;

        private BasisRTAOGpuHarness harness;
        private ComputeShader denoise;
        private int temporalKernel, blurKernel;
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            harness = new BasisRTAOGpuHarness();

            denoise = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.basis.rtao/Shaders/BasisRTAODenoise.compute");
            Assert.IsNotNull(denoise, "BasisRTAODenoise.compute failed to import.");
            temporalKernel = denoise.FindKernel("BasisRTAOTemporal");
            blurKernel = denoise.FindKernel("BasisRTAOBlur");

            camera = harness.Track(new GameObject("BasisRTAODenoiseCamera")).AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
            camera = null;
        }

        private sealed class Frame
        {
            public Texture2DArray position, normal, raw, history, historyDepth, traceDepth;
            public RenderTexture output, outputDepth;
        }

        private Texture2DArray MakeArray(TextureFormat format, int slices = 1)
        {
            Texture2DArray texture = harness.Track(new Texture2DArray(Width, Height, slices, format, false, true));
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private RenderTexture MakeTarget(GraphicsFormat format, int slices = 1)
        {
            RenderTexture texture = harness.Track(new RenderTexture(new RenderTextureDescriptor(Width, Height, format, GraphicsFormat.None, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = slices,
                enableRandomWrite = true,
                msaaSamples = 1
            }));
            texture.filterMode = FilterMode.Point;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.Create();
            return texture;
        }

        private static Vector2 EncodeNormal(Vector3 normal)
        {
            normal.Normalize();
            float sum = Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z);
            Vector2 encoded = new Vector2(normal.x / sum, normal.y / sum);
            if (normal.z < 0f)
            {
                encoded = new Vector2(
                    (1f - Mathf.Abs(encoded.y)) * (encoded.x >= 0f ? 1f : -1f),
                    (1f - Mathf.Abs(encoded.x)) * (encoded.y >= 0f ? 1f : -1f));
            }
            return encoded;
        }

        private static void Fill(Texture2DArray texture, int slice, System.Func<int, int, Color> generator)
        {
            Color[] pixels = new Color[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    pixels[y * Width + x] = generator(x, y);
            }
            texture.SetPixels(pixels, slice);
        }

        private Frame BuildFlatFrame(float planeZ, float rawVisibility, float historyVisibility, float historyFrames, Vector3 normalWS, float historyDepthOverride, int slices = 1)
        {
            Vector2 encoded = EncodeNormal(normalWS);
            Frame frame = new Frame
            {
                position = MakeArray(TextureFormat.RGBAFloat, slices),
                normal = MakeArray(TextureFormat.RGBAFloat, slices),
                raw = MakeArray(TextureFormat.RGFloat, slices),
                history = MakeArray(TextureFormat.RGBAFloat, slices),
                historyDepth = MakeArray(TextureFormat.RGFloat, slices),
                traceDepth = MakeArray(TextureFormat.RGFloat, slices),
                output = MakeTarget(GraphicsFormat.R16G16B16A16_SFloat, slices),
                outputDepth = MakeTarget(GraphicsFormat.R16G16_SFloat, slices)
            };

            for (int slice = 0; slice < slices; slice++)
            {
                Fill(frame.position, slice, (x, y) => new Color(WorldOf(x, y, planeZ).x, WorldOf(x, y, planeZ).y, WorldOf(x, y, planeZ).z, 1f));
                Fill(frame.normal, slice, (x, y) => new Color(encoded.x, encoded.y, 0f, 0f));
                Fill(frame.raw, slice, (x, y) => new Color(rawVisibility, 0.5f, 0f, 0f));
                Fill(frame.history, slice, (x, y) => new Color(historyVisibility, historyFrames, encoded.x, encoded.y));
                Fill(frame.historyDepth, slice, (x, y) => new Color(historyDepthOverride, 0.5f, 0f, 0f));
            }

            frame.position.Apply(false, false);
            frame.normal.Apply(false, false);
            frame.raw.Apply(false, false);
            frame.history.Apply(false, false);
            frame.historyDepth.Apply(false, false);
            FillTraceDepth(frame, (x, y) => planeZ, 1f, slices);
            return frame;
        }

        // What the temporal pass hands the blur and the composite: view depth in x, zero for no geometry,
        // and the accumulated mean hit distance in y.
        private void FillTraceDepth(Frame frame, System.Func<int, int, float> planeZ, float hitDistance, int slices = 1)
        {
            Vector4 plane = PlaneOf(camera);
            for (int slice = 0; slice < slices; slice++)
            {
                Fill(frame.traceDepth, slice, (x, y) =>
                {
                    Vector3 point = WorldOf(x, y, planeZ(x, y));
                    return new Color(plane.x * point.x + plane.y * point.y + plane.z * point.z + plane.w, hitDistance, 0f, 0f);
                });
            }
            frame.traceDepth.Apply(false, false);
        }

        private void FillTraceDepthAsSky(Frame frame)
        {
            Fill(frame.traceDepth, 0, (x, y) => new Color(0f, 0f, 0f, 0f));
            frame.traceDepth.Apply(false, false);
        }

        private static Vector3 WorldOf(int x, int y, float planeZ)
        {
            return new Vector3((x - (Width - 1) * 0.5f) * 0.1f, (y - (Height - 1) * 0.5f) * 0.1f, planeZ);
        }

        // varianceGamma defaults to off here so these cases pin the accumulation arithmetic on its own. The
        // clamp is a separate contract with its own tests, and it is deliberately loud enough to swamp the
        // blend it sits in front of.
        private Vector4[] RunTemporal(Frame frame, bool hasHistory, Matrix4x4 previousViewProjection, Vector4 viewPlane, Vector4 previousViewPlane,
            float maxFrames = 24f, float minAlpha = 0.05f, float depthTolerance = 0.03f, float normalTolerance = 0.9f, int slices = 1,
            float varianceGamma = 0f, float varianceFloor = 0f)
        {
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.PositionTex, frame.position);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.NormalTex, frame.normal);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.RawTex, frame.raw);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.HistoryTex, frame.history);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.HistoryDepthTex, frame.historyDepth);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.TemporalOutTex, frame.output);
            denoise.SetTexture(temporalKernel, BasisRTAOShaderIds.TemporalDepthOutTex, frame.outputDepth);
            denoise.SetMatrixArray(BasisRTAOShaderIds.PrevViewProj, new[] { previousViewProjection, previousViewProjection });
            denoise.SetVectorArray(BasisRTAOShaderIds.ViewPlane, new[] { viewPlane, viewPlane });
            denoise.SetVectorArray(BasisRTAOShaderIds.PrevViewPlane, new[] { previousViewPlane, previousViewPlane });
            denoise.SetVector(BasisRTAOShaderIds.Reference, Vector4.zero);
            denoise.SetVector(BasisRTAOShaderIds.Size, new Vector4(Width, Height, 1f / Width, 1f / Height));
            denoise.SetVector(BasisRTAOShaderIds.TemporalParams, new Vector4(maxFrames, minAlpha, depthTolerance, normalTolerance));
            denoise.SetVector(BasisRTAOShaderIds.TemporalClamp, new Vector4(varianceGamma, varianceFloor, 0f, 0f));
            denoise.SetInt(BasisRTAOShaderIds.ViewCount, slices);
            denoise.SetInt(BasisRTAOShaderIds.HasHistory, hasHistory ? 1 : 0);
            denoise.Dispatch(temporalKernel, (Width + 7) / 8, (Height + 7) / 8, slices);

            return harness.ReadTextureArray(frame.output, Width, Height, slices);
        }

        private Vector4 PlaneOf(Camera source)
        {
            return BasisRTAOPass.ViewPlaneOf(source.worldToCameraMatrix);
        }

        private Matrix4x4 ViewProjectionOf(Camera source)
        {
            return source.projectionMatrix * source.worldToCameraMatrix;
        }

        private static float Mean(Vector4[] values, int channel = 0)
        {
            float sum = 0f;
            for (int i = 0; i < values.Length; i++)
                sum += channel == 0 ? values[i].x : values[i].y;
            return sum / values.Length;
        }

        [Test]
        public void SkyPixelsResolveToFullVisibility()
        {
            Frame frame = BuildFlatFrame(0f, 0.2f, 0.2f, 10f, Vector3.back, 5f);
            Fill(frame.position, 0, (x, y) => new Color(0f, 0f, 0f, 0f));
            frame.position.Apply(false, false);

            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));
            for (int i = 0; i < result.Length; i++)
                Assert.AreEqual(1f, result[i].x, 1e-3f, $"texel {i} has no geometry but did not resolve to full visibility.");
        }

        [Test]
        public void FirstFrameTakesTheRawEstimateWhole()
        {
            Frame frame = BuildFlatFrame(0f, 0.25f, 0.9f, 20f, Vector3.back, 5f);
            Vector4[] result = RunTemporal(frame, false, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Assert.AreEqual(0.25f, Mean(result), 2e-3f,
                "With no history the output must be the raw trace, not a blend with whatever the buffer happened to hold.");
            Assert.AreEqual(0f, Mean(result, 1), 1e-3f, "The frame counter must restart at zero.");
        }

        [Test]
        public void AccumulationBlendsTowardsTheRawEstimate()
        {
            Frame frame = BuildFlatFrame(0f, 0f, 1f, 3f, Vector3.back, 5f);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            float expectedAlpha = 1f / (4f + 1f);
            float expected = Mathf.Lerp(1f, 0f, expectedAlpha);
            Assert.AreEqual(expected, Mean(result), 5e-3f,
                "After 3 accumulated frames the fourth sample must carry a weight of 1/(frames+1).");
            Assert.AreEqual(4f, Mean(result, 1), 1e-2f, "The frame counter must advance by one.");
        }

        [Test]
        public void FrameCounterSaturatesAtTheConfiguredMaximum()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 100f, Vector3.back, 5f);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera), maxFrames: 8f);

            Assert.AreEqual(8f, Mean(result, 1), 1e-2f, "The accumulated frame count must clamp so the blend never freezes completely.");
        }

        [Test]
        public void MinimumAlphaKeepsTheFilterResponsive()
        {
            Frame frame = BuildFlatFrame(0f, 0f, 1f, 64f, Vector3.back, 5f);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera), maxFrames: 64f, minAlpha: 0.25f);

            Assert.AreEqual(0.75f, Mean(result), 5e-3f,
                "A long history must still yield to new samples at the minimum alpha, otherwise lighting changes never appear.");
        }

        [Test]
        public void MismatchedHistoryDepthIsRejected()
        {
            Frame frame = BuildFlatFrame(0f, 0.2f, 0.95f, 20f, Vector3.back, 40f);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Assert.AreEqual(0.2f, Mean(result), 5e-3f,
                "History that sat at a different depth belongs to different geometry. Reusing it is exactly how disocclusion ghosts.");
            Assert.AreEqual(0f, Mean(result, 1), 1e-2f, "A rejected history must restart the accumulation.");
        }

        [Test]
        public void MismatchedHistoryNormalIsRejected()
        {
            Frame frame = BuildFlatFrame(0f, 0.2f, 0.95f, 20f, Vector3.back, 5f);
            Vector2 wrongNormal = EncodeNormal(Vector3.up);
            Fill(frame.history, 0, (x, y) => new Color(0.95f, 20f, wrongNormal.x, wrongNormal.y));
            frame.history.Apply(false, false);

            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Assert.AreEqual(0.2f, Mean(result), 5e-3f,
                "History from a surface facing elsewhere must be rejected, or occlusion bleeds across creases.");
        }

        [Test]
        public void HistoryBehindTheCameraIsRejected()
        {
            Frame frame = BuildFlatFrame(0f, 0.2f, 0.95f, 20f, Vector3.back, 5f);

            GameObject behind = harness.Track(new GameObject("BasisRTAOBehindCamera"));
            Camera previous = behind.AddComponent<Camera>();
            previous.transform.position = new Vector3(0f, 0f, 20f);
            previous.transform.rotation = Quaternion.identity;
            previous.nearClipPlane = 0.1f;
            previous.farClipPlane = 100f;

            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(previous), PlaneOf(camera), PlaneOf(previous));

            Assert.AreEqual(0.2f, Mean(result), 5e-3f,
                "A point that was behind the previous camera has no history to reproject from.");
        }

        [Test]
        public void WrittenDepthMatchesTheCurrentViewPlane()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 4f, Vector3.back, 5f);
            RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Vector4[] depths = harness.ReadTextureArray(frame.outputDepth, Width, Height, 1);
            for (int i = 0; i < depths.Length; i++)
                Assert.AreEqual(5f, depths[i].x, 1e-3f, $"texel {i} stored the wrong view depth for next frame's rejection test.");
        }

        [Test]
        public void CameraMotionStillFindsTheHistory()
        {
            Frame frame = BuildFlatFrame(0f, 0f, 1f, 8f, Vector3.back, 5f);

            GameObject moved = harness.Track(new GameObject("BasisRTAOMovedCamera"));
            Camera previous = moved.AddComponent<Camera>();
            previous.transform.position = camera.transform.position + new Vector3(0.01f, 0.01f, 0f);
            previous.transform.rotation = camera.transform.rotation;
            previous.nearClipPlane = camera.nearClipPlane;
            previous.farClipPlane = camera.farClipPlane;
            previous.fieldOfView = camera.fieldOfView;
            previous.aspect = camera.aspect;

            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(previous), PlaneOf(camera), PlaneOf(previous));

            float expected = Mathf.Lerp(1f, 0f, 1f / 10f);
            Assert.AreEqual(expected, Mean(result), 0.05f,
                $"A small camera move must reproject onto the existing history rather than throwing it away. Reading {Mean(result):F3} instead of {expected:F3} means the history was rejected and the frame reset.");
            Assert.Greater(Mean(result, 1), 1f, "The accumulated frame count must survive the reprojection.");
        }

        [Test]
        public void BothEyesAccumulateIndependentlyAndIdentically()
        {
            Frame frame = BuildFlatFrame(0f, 0.25f, 1f, 3f, Vector3.back, 5f, slices: 2);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera), slices: 2);

            for (int i = 0; i < Width * Height; i++)
            {
                Assert.AreEqual(result[i].x, result[i + Width * Height].x, 1e-3f,
                    $"texel {i} diverged between eyes given identical inputs.");
            }

            float expected = Mathf.Lerp(1f, 0.25f, 1f / 5f);
            Assert.AreEqual(expected, Mean(result), 5e-3f, "Both slices must run the same accumulation as the monoscopic case.");
        }

        [Test]
        public void VarianceClampPullsBackHistoryTheFrameDisagreesWith()
        {
            Frame loose = BuildFlatFrame(0f, 1f, 0.2f, 20f, Vector3.back, 5f);
            Vector4[] unclamped = RunTemporal(loose, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Frame tight = BuildFlatFrame(0f, 1f, 0.2f, 20f, Vector3.back, 5f);
            Vector4[] clamped = RunTemporal(tight, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera),
                varianceGamma: 1.25f, varianceFloor: 0.35f);

            Assert.Greater(Mean(clamped), Mean(unclamped) + 0.2f,
                $"Reprojection carries camera motion only, so an avatar walking off a floor leaves history the depth and normal tests both accept. Clamped {Mean(clamped):F3} against unclamped {Mean(unclamped):F3} means the box never closed on it.");
            Assert.Less(Mean(clamped, 1), Mean(unclamped, 1),
                "Confidence has to fall by how far the clamp had to move, or the blend stays as slow as the ghost it just cut.");
        }

        [Test]
        public void VarianceClampLeavesAgreeingHistoryAlone()
        {
            Frame loose = BuildFlatFrame(0f, 0.5f, 0.55f, 20f, Vector3.back, 5f);
            Vector4[] unclamped = RunTemporal(loose, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera));

            Frame tight = BuildFlatFrame(0f, 0.5f, 0.55f, 20f, Vector3.back, 5f);
            Vector4[] clamped = RunTemporal(tight, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera),
                varianceGamma: 1.25f, varianceFloor: 0.35f);

            Assert.AreEqual(Mean(unclamped), Mean(clamped), 1e-3f,
                "History inside the neighbourhood's spread is the accumulation working. Clipping it there would throw away the convergence the filter exists to build.");
            Assert.AreEqual(Mean(unclamped, 1), Mean(clamped, 1), 1e-2f, "An untouched history must keep its confidence.");
        }

        [Test]
        public void VarianceFloorKeepsTheBoxOpenWhenTheNeighbourhoodAgreesByLuck()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.55f, 20f, Vector3.back, 5f);
            Vector4[] result = RunTemporal(frame, true, ViewProjectionOf(camera), PlaneOf(camera), PlaneOf(camera),
                varianceGamma: 1.25f, varianceFloor: 0.35f);

            Assert.Greater(Mean(result, 1), 20f,
                "Nine taps of a one ray estimate agree outright often enough to matter, and a box measured from that alone has zero width. Without the floor every such pixel resets and the frame sparkles.");
        }

        [Test]
        public void BlurAveragesAcrossAFlatSurface()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            Texture2DArray source = MakeArray(TextureFormat.RGBAFloat);
            Vector2 encoded = EncodeNormal(Vector3.back);
            Fill(source, 0, (x, y) => new Color(x % 2 == 0 ? 0f : 1f, 24f, encoded.x, encoded.y));
            source.Apply(false, false);

            RenderTexture target = MakeTarget(GraphicsFormat.R16G16B16A16_SFloat);
            Vector4[] result = RunBlur(frame, source, target, new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 4f);

            float mean = Mean(result);
            Assert.AreEqual(0.5f, mean, 0.12f,
                $"A horizontal blur across a flat surface must pull the alternating pattern toward its mean, got {mean:F3}.");

            float spread = 0f;
            for (int i = 0; i < result.Length; i++)
                spread = Mathf.Max(spread, Mathf.Abs(result[i].x - mean));
            Assert.Less(spread, 0.45f, "The blur left the checkerboard essentially untouched.");
        }

        [Test]
        public void BlurDoesNotCrossADepthDiscontinuity()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            FillTraceDepth(frame, (x, y) => x < Width / 2 ? 0f : 30f, 1f);

            Texture2DArray source = MakeArray(TextureFormat.RGBAFloat);
            Vector2 encoded = EncodeNormal(Vector3.back);
            Fill(source, 0, (x, y) => new Color(x < Width / 2 ? 0f : 1f, 24f, encoded.x, encoded.y));
            source.Apply(false, false);

            RenderTexture target = MakeTarget(GraphicsFormat.R16G16B16A16_SFloat);
            Vector4[] result = RunBlur(frame, source, target, new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 4f);

            float nearEdge = result[Width / 2 - 1].x;
            float farEdge = result[Width / 2].x;

            Assert.Less(nearEdge, 0.2f, $"The near side of a 30 m depth step bled to {nearEdge:F3}; the bilateral weight is not rejecting the far surface.");
            Assert.Greater(farEdge, 0.8f, $"The far side bled to {farEdge:F3}.");
        }

        [Test]
        public void BlurLeavesSkyPixelsAlone()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            FillTraceDepthAsSky(frame);

            Texture2DArray source = MakeArray(TextureFormat.RGBAFloat);
            Fill(source, 0, (x, y) => new Color(0.37f, 24f, 0f, 0f));
            source.Apply(false, false);

            RenderTexture target = MakeTarget(GraphicsFormat.R16G16B16A16_SFloat);
            Vector4[] result = RunBlur(frame, source, target, new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 4f);

            for (int i = 0; i < result.Length; i++)
                Assert.AreEqual(0.37f, result[i].x, 1e-2f, $"texel {i} has no geometry and must pass through the blur unchanged.");
        }

        [Test]
        public void BlurNarrowsAsHistoryMatures()
        {
            Frame young = BuildFlatFrame(0f, 0.5f, 0.5f, 0f, Vector3.back, 5f);
            Texture2DArray sourceYoung = MakeArray(TextureFormat.RGBAFloat);
            Vector2 encoded = EncodeNormal(Vector3.back);
            Fill(sourceYoung, 0, (x, y) => new Color(x % 2 == 0 ? 0f : 1f, 0f, encoded.x, encoded.y));
            sourceYoung.Apply(false, false);
            Vector4[] youngResult = RunBlur(young, sourceYoung, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat), new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 0f);

            Frame mature = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            Texture2DArray sourceMature = MakeArray(TextureFormat.RGBAFloat);
            Fill(sourceMature, 0, (x, y) => new Color(x % 2 == 0 ? 0f : 1f, 24f, encoded.x, encoded.y));
            sourceMature.Apply(false, false);
            Vector4[] matureResult = RunBlur(mature, sourceMature, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat), new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 0f);

            float youngSpread = Spread(youngResult);
            float matureSpread = Spread(matureResult);

            Assert.Less(youngSpread, matureSpread,
                "A pixel with no accumulated history needs the wide filter; a converged one should keep its detail.");
        }

        [Test]
        public void BlurNarrowsWhereTheRaysStruckClose()
        {
            Vector2 encoded = EncodeNormal(Vector3.back);

            Frame close = BuildFlatFrame(0f, 0.5f, 0.5f, 0f, Vector3.back, 5f);
            FillTraceDepth(close, (x, y) => 0f, 0f);
            Texture2DArray sourceClose = MakeArray(TextureFormat.RGBAFloat);
            Fill(sourceClose, 0, (x, y) => new Color(x % 2 == 0 ? 0f : 1f, 0f, encoded.x, encoded.y));
            sourceClose.Apply(false, false);
            Vector4[] closeResult = RunBlur(close, sourceClose, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat),
                new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 0f);

            Frame open = BuildFlatFrame(0f, 0.5f, 0.5f, 0f, Vector3.back, 5f);
            Texture2DArray sourceOpen = MakeArray(TextureFormat.RGBAFloat);
            Fill(sourceOpen, 0, (x, y) => new Color(x % 2 == 0 ? 0f : 1f, 0f, encoded.x, encoded.y));
            sourceOpen.Apply(false, false);
            Vector4[] openResult = RunBlur(open, sourceOpen, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat),
                new Vector4(1f, 0f, 0f, 0f), maxRadius: 4f, minRadius: 0f);

            Assert.Greater(Spread(closeResult), Spread(openResult),
                "Both pixels are equally unconverged, so only the hit distance separates them. Occlusion whose rays all struck at zero distance varies over that distance and must keep it; occlusion whose rays flew the whole radius is low frequency and can take the wide filter.");
        }

        [Test]
        public void AWiderStrideReachesFurther()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            Vector2 encoded = EncodeNormal(Vector3.back);

            // one lit column in the middle of an otherwise dark row: a stride of one cannot reach it from
            // four texels away, a stride of four lands exactly on it.
            Texture2DArray source = MakeArray(TextureFormat.RGBAFloat);
            Fill(source, 0, (x, y) => new Color(x == Width / 2 ? 1f : 0f, 24f, encoded.x, encoded.y));
            source.Apply(false, false);

            Vector4[] tight = RunBlur(frame, source, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat),
                new Vector4(1f, 0f, 0f, 0f), maxRadius: 2f, minRadius: 2f, stride: 1);
            Vector4[] wide = RunBlur(frame, source, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat),
                new Vector4(1f, 0f, 0f, 0f), maxRadius: 2f, minRadius: 2f, stride: 4);

            int far = 0;
            float tightFar = tight[far].x;
            float wideFar = wide[far].x;

            Assert.AreEqual(0f, tightFar, 1e-3f,
                $"At stride 1 a two tap radius cannot see a column four texels away, so this texel must stay dark. Got {tightFar:F3}.");
            Assert.Greater(wideFar, 1e-3f,
                $"At stride 4 the same two taps land on the lit column, which is the whole point of widening the cascade. Got {wideFar:F3}.");
        }

        [Test]
        public void StrideStillRefusesToCrossADepthStep()
        {
            Frame frame = BuildFlatFrame(0f, 0.5f, 0.5f, 24f, Vector3.back, 5f);
            FillTraceDepth(frame, (x, y) => x < Width / 2 ? 0f : 30f, 1f);

            Vector2 encoded = EncodeNormal(Vector3.back);
            Texture2DArray source = MakeArray(TextureFormat.RGBAFloat);
            Fill(source, 0, (x, y) => new Color(x < Width / 2 ? 0f : 1f, 24f, encoded.x, encoded.y));
            source.Apply(false, false);

            Vector4[] result = RunBlur(frame, source, MakeTarget(GraphicsFormat.R16G16B16A16_SFloat),
                new Vector4(1f, 0f, 0f, 0f), maxRadius: 2f, minRadius: 2f, stride: 4);

            float nearEdge = result[Width / 2 - 1].x;
            Assert.Less(nearEdge, 0.2f,
                $"A wide stride reaches across more geometry, so the edge stopping has to tighten with it. The near side of a 30 m step bled to {nearEdge:F3}.");
        }

        private static float Spread(Vector4[] values)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                min = Mathf.Min(min, values[i].x);
                max = Mathf.Max(max, values[i].x);
            }
            return max - min;
        }

        private Vector4[] RunBlur(Frame frame, Texture2DArray source, RenderTexture target, Vector4 direction, float maxRadius, float minRadius, int slices = 1, int stride = 1)
        {
            denoise.SetTexture(blurKernel, BasisRTAOShaderIds.DepthTex, frame.traceDepth);
            denoise.SetTexture(blurKernel, BasisRTAOShaderIds.BlurSourceTex, source);
            denoise.SetTexture(blurKernel, BasisRTAOShaderIds.BlurTargetTex, target);
            denoise.SetVector(BasisRTAOShaderIds.Size, new Vector4(Width, Height, 1f / Width, 1f / Height));
            denoise.SetVector(BasisRTAOShaderIds.BlurParams, new Vector4(maxRadius, minRadius, 0.05f, 16f));
            denoise.SetVector(BasisRTAOShaderIds.TemporalParams, new Vector4(24f, 0.05f, 0.03f, 0.9f));
            denoise.SetVector(BasisRTAOShaderIds.BlurDirection, new Vector4(direction.x, direction.y, stride, 0f));
            denoise.SetInt(BasisRTAOShaderIds.ViewCount, slices);
            denoise.Dispatch(blurKernel, (Width + 7) / 8, (Height + 7) / 8, slices);

            return harness.ReadTextureArray(target, Width, Height, slices);
        }
    }
}
