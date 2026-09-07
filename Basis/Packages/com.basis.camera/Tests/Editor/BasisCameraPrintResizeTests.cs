using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Print Photo's fit-before-share step: which shots the pickup service takes as they stand,
    /// what the copy of an oversized one comes out as, and that the resampler averages the pixels
    /// it stands for instead of dropping them.
    /// </summary>
    public class BasisCameraPrintResizeTests
    {
        private const long SmallFile = 2L * 1024 * 1024;

        [Test]
        public void AnOrdinaryShotIsImportedAsItStands()
        {
            Assert.That(BasisCameraPrintResize.FitsPickupImport(1920, 1080, SmallFile), Is.True);
        }

        [Test]
        public void TheLargestResolutionPresetDoesNotFit()
        {
            Assert.That(BasisCameraPrintResize.FitsPickupImport(7680, 4320, SmallFile), Is.False,
                "8K is past the pickup service's source dimension cap and has to be resized.");
        }

        [Test]
        public void AHeavyFileDoesNotFitEvenAtAnAcceptedSize()
        {
            Assert.That(BasisCameraPrintResize.FitsPickupImport(4096, 4096, 48L * 1024 * 1024), Is.False);
        }

        [Test]
        public void AShotPastTheDisplayCapIsJudgedOnSizeRatherThanOnFileLength()
        {
            // The service re-encodes from its own downscale, so a fat 4K file is still fine.
            Assert.That(BasisCameraPrintResize.FitsPickupImport(3840, 2160, 20L * 1024 * 1024), Is.True);
        }

        [Test]
        public void ASmallShotTooHeavyForTheWireDoesNotFit()
        {
            // Inside the display cap nothing is downscaled, so the file predicts the sanitized
            // PNG and a 9MiB one would be refused at the far end.
            Assert.That(BasisCameraPrintResize.FitsPickupImport(1024, 768, 9L * 1024 * 1024), Is.False);
        }

        [Test]
        public void FittingKeepsTheShapeAndCapsTheLongestSide()
        {
            BasisCameraPrintResize.FitPrintSize(7680, 4320, BasisCameraPrintResize.MaxPrintDimension, out int width, out int height);
            Assert.That(width, Is.EqualTo(BasisCameraPrintResize.MaxPrintDimension));
            Assert.That(height, Is.EqualTo(1152), "16:9 kept: 2048 wide is 1152 tall.");
        }

        [Test]
        public void FittingNeverEnlarges()
        {
            BasisCameraPrintResize.FitPrintSize(640, 480, BasisCameraPrintResize.MaxPrintDimension, out int width, out int height);
            Assert.That(width, Is.EqualTo(640));
            Assert.That(height, Is.EqualTo(480));
        }

        [Test]
        public void ResamplingAveragesTheFootprintRatherThanSamplingIt()
        {
            byte[] checker = new byte[2 * 2 * 4];
            for (int pixel = 0; pixel < 4; pixel++)
            {
                byte value = (pixel % 2 == 0) ? (byte)0 : (byte)255;
                checker[pixel * 4] = value;
                checker[pixel * 4 + 1] = value;
                checker[pixel * 4 + 2] = value;
                checker[pixel * 4 + 3] = 255;
            }

            byte[] resampled = BasisCameraPrintResize.BoxDownscaleRgba32(checker, 2, 2, 1, 1);

            Assert.That(resampled.Length, Is.EqualTo(4));
            Assert.That(resampled[0], Is.EqualTo(127), "A tap would return 0 or 255; the average is the midpoint.");
            Assert.That(resampled[3], Is.EqualTo(255));
        }

        [Test]
        public void ResamplingKeepsRowOrder()
        {
            byte[] column = new byte[1 * 4 * 4];
            byte[] rows = { 0, 64, 128, 192 };
            for (int row = 0; row < 4; row++)
            {
                column[row * 4] = rows[row];
                column[row * 4 + 1] = rows[row];
                column[row * 4 + 2] = rows[row];
                column[row * 4 + 3] = 255;
            }

            byte[] resampled = BasisCameraPrintResize.BoxDownscaleRgba32(column, 1, 4, 1, 2);

            Assert.That(resampled[0], Is.EqualTo(32), "First row out is the mean of the first two in.");
            Assert.That(resampled[4], Is.EqualTo(160));
        }

        [Test]
        public void ResamplingAtTheSameSizeIsAPassThrough()
        {
            byte[] source = new byte[3 * 2 * 4];
            for (int index = 0; index < source.Length; index++) source[index] = (byte)(index * 7);

            byte[] resampled = BasisCameraPrintResize.BoxDownscaleRgba32(source, 3, 2, 3, 2);

            Assert.That(resampled, Is.EqualTo(source));
        }

        [Test]
        public void AShotThatFitsProducesNoCopy()
        {
            Texture2D photo = MakePhoto(64, 48);
            try
            {
                BasisCameraPrintResize.PrintCopy copy = BasisCameraPrintResize.Build(photo, SmallFile);
                Assert.That(copy.Exists, Is.False, "A shot the service takes is still spawned from its file.");
            }
            finally
            {
                Object.DestroyImmediate(photo);
            }
        }

        [Test]
        public void AnOversizedShotComesBackAsAPngInsideThePrintCap()
        {
            // Wider than the source cap but only a few rows tall, so the test costs a fraction of
            // a real 8K frame while exercising the same rejection.
            Texture2D photo = MakePhoto(4200, 32);
            try
            {
                BasisCameraPrintResize.PrintCopy copy = BasisCameraPrintResize.Build(photo, SmallFile);

                Assert.That(copy.Exists, Is.True);
                Assert.That(copy.SourceWidth, Is.EqualTo(4200), "The size it was shot at is kept for the notice.");
                Assert.That(copy.SourceHeight, Is.EqualTo(32));
                Assert.That(copy.Width, Is.EqualTo(BasisCameraPrintResize.MaxPrintDimension));
                Assert.That(BasisCameraPrintResize.FitsPickupImport(copy.Width, copy.Height, copy.Png.LongLength), Is.True,
                    "The copy has to be something the service will now take.");

                var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    Assert.That(ImageConversion.LoadImage(decoded, copy.Png), Is.True, "The copy has to be a readable PNG.");
                    Assert.That(decoded.width, Is.EqualTo(copy.Width));
                    Assert.That(decoded.height, Is.EqualTo(copy.Height));
                }
                finally
                {
                    Object.DestroyImmediate(decoded);
                }
            }
            finally
            {
                Object.DestroyImmediate(photo);
            }
        }

        [Test]
        public void TheArrayOverloadMatchesTheTextureOverloadByteForByte()
        {
            // The camera save path now runs the fit on a worker from raw pixels; the copy it makes
            // must be exactly the one the texture path would have made.
            Texture2D photo = MakePhoto(4200, 32);
            try
            {
                byte[] raw = photo.GetRawTextureData();
                BasisCameraPrintResize.PrintCopy fromTexture = BasisCameraPrintResize.Build(photo, SmallFile);
                BasisCameraPrintResize.PrintCopy fromArray = BasisCameraPrintResize.Build(raw, 4200, 32, SmallFile);

                Assert.That(fromArray.Exists, Is.EqualTo(fromTexture.Exists));
                Assert.That(fromArray.Width, Is.EqualTo(fromTexture.Width));
                Assert.That(fromArray.Height, Is.EqualTo(fromTexture.Height));
                Assert.That(fromArray.SourceWidth, Is.EqualTo(fromTexture.SourceWidth));
                Assert.That(fromArray.SourceHeight, Is.EqualTo(fromTexture.SourceHeight));
                Assert.That(fromArray.Png, Is.EqualTo(fromTexture.Png));
            }
            finally
            {
                Object.DestroyImmediate(photo);
            }
        }

        [Test]
        public void TheArrayOverloadRefusesPixelDataSmallerThanTheClaimedSize()
        {
            BasisCameraPrintResize.PrintCopy copy = BasisCameraPrintResize.Build(new byte[16], 100, 100, 64L * 1024 * 1024);
            Assert.That(copy.Exists, Is.False);
        }

        [Test]
        public void TheArrayOverloadProducesNoCopyForAShotThatFits()
        {
            byte[] raw = new byte[64 * 48 * 4];
            BasisCameraPrintResize.PrintCopy copy = BasisCameraPrintResize.Build(raw, 64, 48, SmallFile);
            Assert.That(copy.Exists, Is.False);
        }

        private static Texture2D MakePhoto(int width, int height)
        {
            var photo = new Texture2D(width, height, TextureFormat.RGBA32, false);
            byte[] pixels = new byte[width * height * 4];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = (byte)(index % 251);
                pixels[index + 1] = (byte)(index % 199);
                pixels[index + 2] = (byte)(index % 157);
                pixels[index + 3] = 255;
            }
            photo.LoadRawTextureData(pixels);
            photo.Apply(false, false);
            return photo;
        }
    }
}
