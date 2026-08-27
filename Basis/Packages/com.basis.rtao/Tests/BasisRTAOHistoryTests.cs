using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOHistoryTests
    {
        private BasisRTAOHistory history;
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            history = new BasisRTAOHistory();
            camera = new GameObject("BasisRTAOHistoryTestCamera").AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown()
        {
            history.Dispose();
            if (camera != null)
                Object.DestroyImmediate(camera.gameObject);
        }

        [Test]
        public void AllocatesArrayTexturesWithRequestedSlices()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 64, 32, 2, 0);

            Assert.IsTrue(entry.valid);
            Assert.AreEqual(64, entry.width);
            Assert.AreEqual(32, entry.height);
            Assert.AreEqual(2, entry.viewCount);

            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(TextureDimension.Tex2DArray, entry.visibilityTextures[i].dimension);
                Assert.AreEqual(2, entry.visibilityTextures[i].volumeDepth);
                Assert.AreEqual(TextureDimension.Tex2DArray, entry.depthTextures[i].dimension);
                Assert.AreEqual(2, entry.depthTextures[i].volumeDepth);
                Assert.IsTrue(entry.visibilityTextures[i].enableRandomWrite, "The temporal kernel writes the history as a UAV.");
                Assert.IsTrue(entry.depthTextures[i].enableRandomWrite);
            }
        }

        [Test]
        public void MonoscopicAllocationStillUsesAnArrayOfOne()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 16, 16, 1, 0);
            Assert.AreEqual(TextureDimension.Tex2DArray, entry.visibilityTextures[0].dimension,
                "The trace and denoise shaders always declare Texture2DArray, so the non-XR path must allocate a one slice array.");
            Assert.AreEqual(1, entry.visibilityTextures[0].volumeDepth);
        }

        [Test]
        public void HistoryFormatsCarryVisibilityFramesAndNormal()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 16, 16, 1, 0);
            Assert.AreEqual(GraphicsFormat.R16G16B16A16_SFloat, entry.visibilityTextures[0].graphicsFormat);
            Assert.AreEqual(GraphicsFormat.R16G16_SFloat, entry.depthTextures[0].graphicsFormat,
                "View depth in x and the accumulated mean hit distance in y, in the same four bytes the depth alone used to take. Half float resolves view depth to about a twentieth of a percent, and the rejection test it feeds runs at three percent.");
        }

        [Test]
        public void SameRequestReturnsTheSameEntry()
        {
            BasisRTAOHistory.Entry first = history.Get(camera, 64, 32, 2, 0);
            RenderTexture firstTexture = first.visibilityTextures[0];
            BasisRTAOHistory.Entry second = history.Get(camera, 64, 32, 2, 1);

            Assert.AreSame(first, second);
            Assert.AreSame(firstTexture, second.visibilityTextures[0], "A matching request must not reallocate.");
        }

        [Test]
        public void ResizeReallocatesAndDropsAccumulatedHistory()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 64, 32, 2, 0);
            entry.framesRendered = 12;

            BasisRTAOHistory.Entry resized = history.Get(camera, 128, 64, 2, 1);
            Assert.AreEqual(128, resized.width);
            Assert.AreEqual(0, resized.framesRendered, "Resizing invalidates the accumulated frames, otherwise the first frame reads garbage.");
        }

        [Test]
        public void ViewCountChangeReallocates()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 64, 32, 1, 0);
            entry.framesRendered = 5;

            BasisRTAOHistory.Entry stereo = history.Get(camera, 64, 32, 2, 1);
            Assert.AreEqual(2, stereo.viewCount);
            Assert.AreEqual(2, stereo.visibilityTextures[0].volumeDepth);
            Assert.AreEqual(0, stereo.framesRendered, "Entering VR must not reuse the monoscopic history.");
        }

        [Test]
        public void SwapAlternatesReadAndWriteTargets()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 16, 16, 1, 0);
            RTHandle firstWrite = entry.CurrentVisibility;
            RTHandle firstRead = entry.PreviousVisibility;
            Assert.AreNotSame(firstWrite, firstRead);

            entry.Swap();
            Assert.AreSame(firstWrite, entry.PreviousVisibility, "After the swap, last frame's output must be this frame's history.");
            Assert.AreSame(firstRead, entry.CurrentVisibility);

            entry.Swap();
            Assert.AreSame(firstWrite, entry.CurrentVisibility);
        }

        [Test]
        public void DepthPingPongsInLockstepWithVisibility()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 16, 16, 1, 0);
            int index = entry.writeIndex;
            Assert.AreSame(entry.visibility[index], entry.CurrentVisibility);
            Assert.AreSame(entry.depth[index], entry.CurrentDepth);

            entry.Swap();
            index = entry.writeIndex;
            Assert.AreSame(entry.visibility[index], entry.CurrentVisibility);
            Assert.AreSame(entry.depth[index], entry.CurrentDepth);
        }

        [Test]
        public void SeparateCamerasKeepSeparateHistory()
        {
            Camera other = new GameObject("BasisRTAOHistoryTestCameraB").AddComponent<Camera>();
            try
            {
                BasisRTAOHistory.Entry a = history.Get(camera, 16, 16, 1, 0);
                BasisRTAOHistory.Entry b = history.Get(other, 16, 16, 1, 0);
                Assert.AreNotSame(a, b);
                Assert.AreEqual(2, history.Count);
            }
            finally
            {
                Object.DestroyImmediate(other.gameObject);
            }
        }

        [Test]
        public void EvictDropsEntriesThatStoppedRendering()
        {
            history.Get(camera, 16, 16, 1, 0);
            Assert.AreEqual(1, history.Count);

            history.Evict(4);
            Assert.AreEqual(1, history.Count, "An entry inside the age window must survive.");

            history.Evict(100);
            Assert.AreEqual(0, history.Count);
        }

        [Test]
        public void DisposeReleasesEveryTexture()
        {
            BasisRTAOHistory.Entry entry = history.Get(camera, 16, 16, 1, 0);
            history.Dispose();

            Assert.AreEqual(0, history.Count);
            Assert.IsFalse(entry.valid);
            Assert.IsNull(entry.visibility[0]);
            Assert.IsNull(entry.depth[0]);
        }
    }
}
