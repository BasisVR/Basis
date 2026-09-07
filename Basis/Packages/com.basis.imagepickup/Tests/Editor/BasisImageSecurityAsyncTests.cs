using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.ImagePickup.Tests
{
    /// <summary>
    /// The async validation pipeline (decode on the main thread, downscale / re-encode / alpha scan
    /// on a worker) must reach the same verdicts and the same pixels as the synchronous one it
    /// shadows — including the downscale path and the rejection paths.
    /// </summary>
    public class BasisImageSecurityAsyncTests
    {
        private static byte[] EncodePng(int width, int height, bool withAlpha)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int index = 0; index < pixels.Length; index++)
            {
                byte alpha = withAlpha && index == 0 ? (byte)128 : (byte)255;
                pixels[index] = new Color32((byte)(index % 251), (byte)(index % 199), (byte)(index % 157), alpha);
            }
            texture.SetPixels32(pixels);
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);
            return png;
        }

        private static void DisposeResult(ref BasisImageValidationResult result)
        {
            if (result.Texture != null) Object.DestroyImmediate(result.Texture);
            result.Texture = null;
        }

        private static IEnumerator Compare(byte[] png)
        {
            BasisImageValidationResult sync = BasisImageSecurity.ValidateSourceBytes(png);
            var task = BasisImageSecurity.ValidateSourceBytesAsync(png);
            while (!task.IsCompleted) yield return null;
            BasisImageValidationResult async = task.Result;

            try
            {
                Assert.That(async.Ok, Is.EqualTo(sync.Ok), $"sync error '{sync.Error}' vs async error '{async.Error}'");
                Assert.That(async.Width, Is.EqualTo(sync.Width));
                Assert.That(async.Height, Is.EqualTo(sync.Height));
                Assert.That(async.HasAlpha, Is.EqualTo(sync.HasAlpha));

                if (sync.Ok)
                {
                    var syncDecoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    var asyncDecoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        Assert.That(ImageConversion.LoadImage(syncDecoded, sync.CleanPng), Is.True);
                        Assert.That(ImageConversion.LoadImage(asyncDecoded, async.CleanPng), Is.True);
                        Assert.That(asyncDecoded.width, Is.EqualTo(syncDecoded.width));
                        Assert.That(asyncDecoded.height, Is.EqualTo(syncDecoded.height));
                        Color32[] expected = syncDecoded.GetPixels32();
                        Color32[] actual = asyncDecoded.GetPixels32();
                        for (int index = 0; index < expected.Length; index++)
                        {
                            if (!expected[index].Equals(actual[index]))
                            {
                                Assert.Fail($"clean png pixel {index}: sync {expected[index]} vs async {actual[index]}");
                            }
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(syncDecoded);
                        Object.DestroyImmediate(asyncDecoded);
                    }
                }
            }
            finally
            {
                DisposeResult(ref sync);
                DisposeResult(ref async);
            }
        }

        [UnityTest]
        public IEnumerator AsyncPipeline_MatchesSync_ForAnOrdinaryImage()
        {
            return Compare(EncodePng(64, 48, withAlpha: false));
        }

        [UnityTest]
        public IEnumerator AsyncPipeline_MatchesSync_ForAnImageWithTransparency()
        {
            return Compare(EncodePng(32, 32, withAlpha: true));
        }

        [UnityTest]
        public IEnumerator AsyncPipeline_MatchesSync_ThroughTheDownscalePath()
        {
            return Compare(EncodePng(BasisImagePickupSettings.MaxDimension + 60, 32, withAlpha: true));
        }

        [UnityTest]
        public IEnumerator AsyncPipeline_RejectsGarbage_LikeSyncDoes()
        {
            byte[] garbage = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            BasisImageValidationResult sync = BasisImageSecurity.ValidateSourceBytes(garbage);
            var task = BasisImageSecurity.ValidateSourceBytesAsync(garbage);
            while (!task.IsCompleted) yield return null;
            BasisImageValidationResult async = task.Result;
            Assert.That(sync.Ok, Is.False);
            Assert.That(async.Ok, Is.False);
        }
    }
}
