using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public class BasisImageSecurityTests
    {
        private const string AnimatedGif =
            "R0lGODlhAgABAIEAAAD/AP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAgABAAAIBQADAAgIACH5BAkUAAAALAAAAAACAAEAgQAAAAAA/wAAAAAAAAgFAAMACAgAOw==";

        private static readonly byte[] PngSignature =
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A,
        };

        [TestCase("image.png")]
        [TestCase("image.PNG")]
        [TestCase("image.jpg")]
        [TestCase("image.JPEG")]
        [TestCase("image.gif")]
        [TestCase("image.GIF")]
        public void SupportedImageExtensionsAreAccepted(string path)
        {
            Assert.That(BasisImageSecurity.HasSupportedImageExtension(path), Is.True);
        }

        [TestCase("image.webp")]
        [TestCase("image.exr")]
        [TestCase("image.png.exe")]
        [TestCase("")]
        public void UnsupportedImageExtensionsAreRejected(string path)
        {
            Assert.That(BasisImageSecurity.HasSupportedImageExtension(path), Is.False);
        }

        [Test]
        public void RejectionPopupDescriptionIncludesFileAndEscapedReason()
        {
            string description = BasisImagePickupRejectionPopup.BuildDescription(
                "/tmp/test<image>.gif",
                "Canvas < limit & invalid"
            );

            Assert.That(description, Does.Contain("test&lt;image&gt;.gif"));
            Assert.That(description, Does.Contain("Canvas &lt; limit &amp; invalid"));
            Assert.That(description, Does.Contain("<b>File:</b>"));
            Assert.That(description, Does.Contain("<b>Reason:</b>"));
        }

        [Test]
        public void PopupKeepsDefaultSizeWhenDescriptionFits()
        {
            Vector2 size = BasisImagePickupRejectionPopup.CalculateHeightFirstPanelSize(
                new Vector2(700f, 500f),
                240f,
                220f,
                900f
            );

            Assert.That(size, Is.EqualTo(new Vector2(700f, 500f)));
        }

        [Test]
        public void PopupExpandsHeightBeforeWidth()
        {
            Vector2 size = BasisImagePickupRejectionPopup.CalculateHeightFirstPanelSize(
                new Vector2(700f, 500f),
                240f,
                400f,
                900f
            );

            Assert.That(size.x, Is.EqualTo(700f));
            Assert.That(size.y, Is.GreaterThan(500f));
            Assert.That(size.y, Is.LessThanOrEqualTo(900f));
        }

        [Test]
        public void PopupHeightIsClampedToViewportLimit()
        {
            Vector2 size = BasisImagePickupRejectionPopup.CalculateHeightFirstPanelSize(
                new Vector2(700f, 500f),
                240f,
                1200f,
                760f
            );

            Assert.That(size, Is.EqualTo(new Vector2(700f, 760f)));
        }

        [Test]
        public void BatchNoticeExplainsLimitAndSerializedAnimationSending()
        {
            string description = BasisImagePickupRejectionPopup.BuildBatchNotice(60, 10, 4, 6);

            Assert.That(description, Does.Contain("at most <b>64</b>"));
            Assert.That(description, Does.Contain("Only <b>4</b>"));
            Assert.That(description, Does.Contain("remaining images were blocked"));
            Assert.That(description, Does.Contain("one image at a time"));
            Assert.That(description, Does.Contain("longer to finish syncing"));
        }

        [Test]
        public void JpegDimensionParserReadsStartOfFrame()
        {
            byte[] jpeg =
            {
                0xFF,
                0xD8,
                0xFF,
                0xE0,
                0x00,
                0x04,
                0x00,
                0x00,
                0xFF,
                0xC0,
                0x00,
                0x11,
                0x08,
                0x00,
                0x03,
                0x00,
                0x02,
                0x03,
                0x01,
                0x11,
                0x00,
                0x02,
                0x11,
                0x00,
                0x03,
                0x11,
                0x00,
                0xFF,
                0xD9,
            };

            bool valid = BasisImageSecurity.TryReadJpegDimensions(
                jpeg,
                out int width,
                out int height,
                out string error
            );

            Assert.That(valid, Is.True, error);
            Assert.That(width, Is.EqualTo(2));
            Assert.That(height, Is.EqualTo(3));
        }

        [Test]
        public void OversizedStaticImageExplainsExceededDimension()
        {
            int width = BasisImagePickupSettings.MaxSourceDimension + 50;
            int height = 100;
            byte[] pngHeader = new byte[24];
            Array.Copy(PngSignature, pngHeader, PngSignature.Length);
            pngHeader[12] = (byte)'I';
            pngHeader[13] = (byte)'H';
            pngHeader[14] = (byte)'D';
            pngHeader[15] = (byte)'R';
            pngHeader[16] = (byte)(width >> 24);
            pngHeader[17] = (byte)(width >> 16);
            pngHeader[18] = (byte)(width >> 8);
            pngHeader[19] = (byte)width;
            pngHeader[20] = (byte)(height >> 24);
            pngHeader[21] = (byte)(height >> 16);
            pngHeader[22] = (byte)(height >> 8);
            pngHeader[23] = (byte)height;

            string path = Path.Combine(Path.GetTempPath(), $"BasisImagePickup_{Guid.NewGuid():N}.png");
            try
            {
                File.WriteAllBytes(path, pngHeader);
                BasisImageValidationResult result = BasisImageSecurity.ValidateFile(path);

                Assert.That(result.Ok, Is.False);
                Assert.That(result.Error, Does.Contain($"{width:N0}×{height:N0}"));
                Assert.That(result.Error, Does.Contain("width exceeds the limit by 50px"));
                Assert.That(
                    result.Error,
                    Does.Contain(
                        $"{BasisImagePickupSettings.MaxSourceDimension:N0}×{BasisImagePickupSettings.MaxSourceDimension:N0}"
                    )
                );
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void OversizedGifUsesAnimationCanvasLimitsBeforeDecode()
        {
            int width = BasisImagePickupSettings.MaxAnimationDimension + 1;
            const int height = 1;
            byte[] gifHeader =
            {
                (byte)'G',
                (byte)'I',
                (byte)'F',
                (byte)'8',
                (byte)'9',
                (byte)'a',
                (byte)width,
                (byte)(width >> 8),
                (byte)height,
                (byte)(height >> 8),
            };
            string path = Path.Combine(Path.GetTempPath(), $"BasisImagePickup_{Guid.NewGuid():N}.gif");
            try
            {
                File.WriteAllBytes(path, gifHeader);
                BasisImageValidationResult result = BasisImageSecurity.ValidateFile(path);

                Assert.That(result.Ok, Is.False);
                Assert.That(result.Error, Does.Contain("Animation canvas"));
                Assert.That(result.Error, Does.Contain($"{width:N0}×{height:N0}"));
                Assert.That(
                    result.Error,
                    Does.Contain(
                        $"{BasisImagePickupSettings.MaxAnimationDimension:N0}×{BasisImagePickupSettings.MaxAnimationDimension:N0}"
                    )
                );
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void RemoteBudgetAcceptsLargeSmallImageBatch()
        {
            const int imageCount = 29;
            long aggregatePixels = imageCount * 498L * 498L;
            long aggregateBytes = imageCount * 150_000L;

            Assert.That(
                BasisImagePickupManager.IsWithinRemoteImageBudget(
                    imageCount,
                    aggregatePixels,
                    aggregateBytes,
                    out string reason
                ),
                Is.True,
                reason
            );
            Assert.That(BasisImagePickupSettings.SpawnRateBurstAllowance, Is.GreaterThanOrEqualTo(imageCount));
        }

        [Test]
        public void RemoteBudgetRejectsCountAboveLimit()
        {
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteImageBudget(
                    BasisImagePickupSettings.MaxConcurrentImagesPerSender + 1,
                    1,
                    1,
                    out string reason
                ),
                Is.False
            );
            Assert.That(reason, Does.Contain("count limit"));
        }

        [Test]
        public void RemoteBudgetRejectsAggregatePixelsAboveLimit()
        {
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteImageBudget(
                    1,
                    BasisImagePickupSettings.MaxRemoteImagePixelsPerSender + 1,
                    1,
                    out string reason
                ),
                Is.False
            );
            Assert.That(reason, Does.Contain("pixel budget"));
        }

        [Test]
        public void RemoteBudgetRejectsAggregateBytesAboveLimit()
        {
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteImageBudget(
                    1,
                    1,
                    BasisImagePickupSettings.MaxRemoteImageBytesPerSender + 1,
                    out string reason
                ),
                Is.False
            );
            Assert.That(reason, Does.Contain("byte budget"));
        }

        [Test]
        public void ValidateBytesReencodesNetworkPngAndStripsTrailingData()
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            BasisImageValidationResult result = default;
            try
            {
                source.SetPixels32(
                    new[]
                    {
                        new Color32(255, 0, 0, 255),
                        new Color32(0, 255, 0, 255),
                        new Color32(0, 0, 255, 255),
                        new Color32(255, 255, 255, 255),
                    }
                );
                source.Apply(false, false);
                byte[] clean = source.EncodeToPNG();
                byte[] marker = System.Text.Encoding.ASCII.GetBytes("BASIS_TRAILING_MARKER");
                var tainted = new byte[clean.Length + marker.Length];
                Buffer.BlockCopy(clean, 0, tainted, 0, clean.Length);
                Buffer.BlockCopy(marker, 0, tainted, clean.Length, marker.Length);

                result = BasisImageSecurity.ValidateBytes(tainted);

                Assert.That(result.Ok, Is.True, result.Error);
                Assert.That(result.CleanPng, Is.Not.Null.And.Not.Empty);
                Assert.That(result.CleanPng.Length, Is.LessThan(tainted.Length));
                Assert.That(ContainsSequence(result.CleanPng, marker), Is.False);
            }
            finally
            {
                if (result.Texture != null)
                    UnityEngine.Object.DestroyImmediate(result.Texture);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void ValidateFileAcceptsGifThroughQueuedPipeline()
        {
            string path = Path.Combine(Path.GetTempPath(), $"BasisImagePickup_{Guid.NewGuid():N}.gif");
            BasisImageValidationResult result = default;
            try
            {
                File.WriteAllBytes(path, Convert.FromBase64String(AnimatedGif));
                result = BasisImageSecurity.ValidateFile(path);

                Assert.That(result.Ok, Is.True, result.Error);
                Assert.That(result.Animation, Is.Not.Null);
                Assert.That(result.Animation.FrameCount, Is.EqualTo(2));
                Assert.That(result.AnimationPayload, Is.Not.Null);
                Assert.That(result.CleanPng, Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                if (result.Texture != null)
                    UnityEngine.Object.DestroyImmediate(result.Texture);
                result.Animation?.Dispose();
                result.AnimationPayload?.Dispose();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void ValidateFileAcceptsJpegAndReturnsSanitizedPng()
        {
            string path = Path.Combine(Path.GetTempPath(), $"BasisImagePickup_{Guid.NewGuid():N}.jpg");
            var source = new Texture2D(2, 3, TextureFormat.RGBA32, false, false);
            BasisImageValidationResult result = default;
            try
            {
                source.SetPixels32(
                    new[]
                    {
                        new Color32(255, 0, 0, 255),
                        new Color32(0, 255, 0, 255),
                        new Color32(0, 0, 255, 255),
                        new Color32(255, 255, 0, 255),
                        new Color32(0, 255, 255, 255),
                        new Color32(255, 0, 255, 255),
                    }
                );
                source.Apply(false, false);
                File.WriteAllBytes(path, source.EncodeToJPG(90));

                result = BasisImageSecurity.ValidateFile(path);

                Assert.That(result.Ok, Is.True, result.Error);
                Assert.That(result.Width, Is.EqualTo(2));
                Assert.That(result.Height, Is.EqualTo(3));
                Assert.That(result.HasAlpha, Is.False);
                Assert.That(result.CleanPng, Is.Not.Null.And.Not.Empty);
                Assert.That(result.CleanPng.Length, Is.GreaterThanOrEqualTo(PngSignature.Length));
                int signatureLength = PngSignature.Length;
                for (int i = 0; i < signatureLength; i++)
                    Assert.That(result.CleanPng[i], Is.EqualTo(PngSignature[i]));
            }
            finally
            {
                if (result.Texture != null)
                    UnityEngine.Object.DestroyImmediate(result.Texture);
                UnityEngine.Object.DestroyImmediate(source);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        private static bool ContainsSequence(byte[] source, byte[] value)
        {
            if (source == null || value == null || value.Length == 0 || value.Length > source.Length)
                return false;
            int finalStart = source.Length - value.Length;
            for (int start = 0; start <= finalStart; start++)
            {
                int index = 0;
                while (index < value.Length && source[start + index] == value[index])
                    index++;
                if (index == value.Length)
                    return true;
            }
            return false;
        }
    }
}
