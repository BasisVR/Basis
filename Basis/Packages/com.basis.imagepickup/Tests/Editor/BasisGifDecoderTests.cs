using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public class BasisGifDecoderTests
    {
        private const string AnimatedGif =
            "R0lGODlhAgABAIEAAAD/AP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAgABAAAIBQADAAgIACH5BAkUAAAALAAAAAACAAEAgQAAAAAA/wAAAAAAAAgFAAMACAgAOw==";
        private const string InterlacedPreviousGif =
            "R0lGODlhAgAEAIEAAAAAAP8AAP//AP///yH5BAQBAAAALAAAAAACAAQAAAIHDMMwDMMwBQAh+QQMAQAAACwAAAAAAgAEAMEAAAD/AAAA/wAAAP8CBwzDcRRFEAUAIfkEBAEAAAAsAAAAAAEAAQAAAgJUAQA7";

        [Test]
        public void BurstGifDecoderPreservesGifFeatures()
        {
            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(
                Convert.FromBase64String(InterlacedPreviousGif)
            );
            BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            try
            {
                Assert.That(result.Animation.FrameCount, Is.EqualTo(3));
                Assert.That(result.Animation.GetFrame(1).Disposal, Is.EqualTo(BasisAnimationDisposal.Previous));
                Color32[] pixels = result.Animation.CopyFramePixelsToManaged(1);
                Assert.That(pixels.Length, Is.EqualTo(8));
                Assert.That(result.PosterPixels.Length, Is.EqualTo(8));
            }
            finally
            {
                result.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClaimedBurstGifResultSurvivesRequestDisposal(bool useTryComplete)
        {
            var request = BasisBurstGifDecoder.Schedule(Convert.FromBase64String(AnimatedGif));
            BasisBurstGifDecodeResult result;
            if (useTryComplete)
            {
                while (!request.TryComplete(out result))
                    Thread.Yield();
            }
            else
            {
                result = request.Complete();
            }

            request.Dispose();
            try
            {
                Assert.That(result.Ok, Is.True, result.Error);
                Assert.That(result.Animation.FrameCount, Is.EqualTo(2));
                Assert.That(result.PosterPixels.IsCreated, Is.True);
            }
            finally
            {
                result.Dispose();
            }
        }

        [Test]
        public void BurstGifDecoderAcceptsCleanEndOfFileWithoutTrailer()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            Assert.That(source[^1], Is.EqualTo(0x3B));
            Array.Resize(ref source, source.Length - 1);

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Animation.FrameCount, Is.EqualTo(2));
        }

        [Test]
        public void LaterTransparentFrameDoesNotEraseOpaqueLogicalBackground()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            int firstGraphicControl = FindGraphicControlExtension(source, 0);
            Assert.That(firstGraphicControl, Is.GreaterThanOrEqualTo(0));
            source[firstGraphicControl + 3] &= 0xFE;

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Animation.BackgroundColor, Is.EqualTo(new Color32(0, 255, 0, 255)));
        }

        [Test]
        public void SkippedPlainTextConsumesPendingGraphicControl()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            int graphicControl = FindGraphicControlExtension(source, 0);
            Assert.That(graphicControl, Is.GreaterThanOrEqualTo(0));
            byte[] plainTextExtension =
            {
                0x21,
                0x01,
                0x0C,
                0x00,
                0x00,
                0x00,
                0x00,
                0x01,
                0x00,
                0x01,
                0x00,
                0x08,
                0x08,
                0x01,
                0x00,
                0x01,
                0x41,
                0x00,
            };
            source = InsertBytes(source, graphicControl + 8, plainTextExtension);

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            BasisAnimatedImageFrame firstFrame = result.Animation.GetFrame(0);
            Assert.That(
                firstFrame.DurationMicroseconds,
                Is.EqualTo(BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds)
            );
            Assert.That(firstFrame.Disposal, Is.EqualTo(BasisAnimationDisposal.None));
            Assert.That(firstFrame.Blend, Is.EqualTo(BasisAnimationBlend.Source));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClaimedAnimationEncodeResultSurvivesRequestDisposal(bool useTryComplete)
        {
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            var request = new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult result;
            if (useTryComplete)
            {
                while (!request.TryComplete(out result))
                    Thread.Yield();
            }
            else
            {
                result = request.Complete();
            }

            request.Dispose();
            try
            {
                Assert.That(result.Ok, Is.True, result.Error);
                Assert.That(result.Payload, Is.Not.Null);
                Assert.That(result.Payload.IsCreated, Is.True);
                Assert.That(result.Payload.Length, Is.GreaterThan(0));
                Assert.That(result.Payload.AllocatedBytes, Is.EqualTo(result.Payload.Length));
                Assert.That(
                    BasisNativeAnimationPayload.TotalAllocatedBytes,
                    Is.EqualTo(payloadBytesBefore + result.Payload.AllocatedBytes)
                );
            }
            finally
            {
                result.Payload?.Dispose();
            }
            Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClaimedAnimationDecodeResultSurvivesRequestDisposal(bool useTryComplete)
        {
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            using BasisBurstAnimationEncodeRequest encode =
                new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult encoded = encode.Complete();
            Assert.That(encoded.Ok, Is.True, encoded.Error);
            try
            {
                var request = new BasisBurstAnimationDecodeRequest(
                    encoded.Payload.Bytes,
                    encoded.Payload.Length,
                    false
                );
                BasisBurstAnimationDecodeResult result;
                if (useTryComplete)
                {
                    while (!request.TryComplete(out result))
                        Thread.Yield();
                }
                else
                {
                    result = request.Complete();
                }

                request.Dispose();
                try
                {
                    Assert.That(result.Ok, Is.True, result.Error);
                    Assert.That(result.Animation, Is.Not.Null);
                    Assert.That(result.Animation.IsCreated, Is.True);
                    Assert.That(result.Animation.FrameCount, Is.EqualTo(source.FrameCount));
                }
                finally
                {
                    result.Animation?.Dispose();
                }
            }
            finally
            {
                encoded.Payload.Dispose();
            }
        }

        [Test]
        public void BurstLz4CodecRoundTripsNativeAnimation()
        {
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            using BasisBurstAnimationEncodeRequest encode =
                new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult encoded = encode.Complete();
            Assert.That(encoded.Ok, Is.True, encoded.Error);
            try
            {
                using BasisBurstAnimationDecodeRequest decode =
                    new BasisBurstAnimationDecodeRequest(encoded.Payload.Bytes, encoded.Payload.Length, false);
                BasisBurstAnimationDecodeResult decoded = decode.Complete();
                Assert.That(decoded.Ok, Is.True, decoded.Error);
                using (decoded.Animation)
                {
                    Assert.That(decoded.Animation.FrameCount, Is.EqualTo(source.FrameCount));
                    Assert.That(decoded.Animation.GetFrame(1).Disposal, Is.EqualTo(source.GetFrame(1).Disposal));
                    Assert.That(
                        decoded.Animation.CopyFramePixelsToManaged(1),
                        Is.EqualTo(source.CopyFramePixelsToManaged(1))
                    );
                }
            }
            finally
            {
                encoded.Payload.Dispose();
            }
        }

        [Test]
        public void Lz4ExtendedLengthRejectsIntegerOverflow()
        {
            int length = int.MaxValue - 10;

            Assert.That(BasisBurstAnimationCodec.TryAccumulateLz4Length(ref length, 11), Is.False);
            Assert.That(length, Is.EqualTo(int.MaxValue - 10));
        }

        [Test]
        public void AnimationOuterHeaderSeparatesTrustedLocalAndRemoteLimits()
        {
            const int frameCount = 4;
            int rawLength = checked(
                BasisBurstAnimationCodec.BodyHeaderBytes
                + frameCount * BasisBurstAnimationCodec.FrameRecordBytes
                + frameCount * 2048 * 2048 * 4
            );
            Assert.That(rawLength, Is.EqualTo(64 * 1024 * 1024 + 232));

            var payload = new NativeArray<byte>(
                BasisBurstAnimationCodec.OuterHeaderBytes + 1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            try
            {
                WriteAnimationOuterHeader(payload, rawLength, 1);

                Assert.That(
                    BasisBurstAnimationCodec.TryReadOuterHeader(
                        payload,
                        payload.Length,
                        64L * 1024L * 1024L,
                        out _,
                        out _
                    ),
                    Is.False
                );
                Assert.That(
                    BasisBurstAnimationCodec.TryReadOuterHeader(
                        payload,
                        payload.Length,
                        BasisImagePickupSettings.MaxAnimationNetworkDecodedBytes,
                        out int trustedRawLength,
                        out string trustedError
                    ),
                    Is.True,
                    trustedError
                );
                Assert.That(trustedRawLength, Is.EqualTo(rawLength));
            }
            finally
            {
                payload.Dispose();
            }
        }

        [Test]
        public void Lz4DecompressRejectsOverflowingLiteralRange()
        {
            const int rawLength = 6;
            int extendedByteCount = CalculateExtendedLengthByteCount(int.MaxValue - 15);
            int compressedLength = 5 + extendedByteCount;
            var payload = new NativeArray<byte>(
                BasisBurstAnimationCodec.OuterHeaderBytes + compressedLength,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory
            );
            try
            {
                using var raw = new NativeArray<byte>(
                    rawLength,
                    Allocator.TempJob,
                    NativeArrayOptions.ClearMemory
                );
                using var result = new NativeArray<int>(
                    2,
                    Allocator.TempJob,
                    NativeArrayOptions.ClearMemory
                );

                WriteAnimationOuterHeader(payload, rawLength, compressedLength);
                int offset = BasisBurstAnimationCodec.OuterHeaderBytes;
                payload[offset++] = 0x10;
                payload[offset++] = 0x7F;
                payload[offset++] = 1;
                payload[offset++] = 0;
                payload[offset++] = 0xF0;
                WriteExtendedLength(payload, ref offset, int.MaxValue - 15);
                Assert.That(offset, Is.EqualTo(payload.Length));

                new BasisLz4DecompressJob { Payload = payload, Raw = raw, Result = result }.Execute();

                Assert.That(result[1], Is.EqualTo((int)BasisAnimationCodecError.Truncated));
            }
            finally
            {
                payload.Dispose();
            }
        }

        [Test]
        public void Lz4DecompressRejectsOverflowingMatchRange()
        {
            const int rawLength = 2;
            int extendedByteCount = CalculateExtendedLengthByteCount(int.MaxValue - 19);
            int compressedLength = 4 + extendedByteCount;
            var payload = new NativeArray<byte>(
                BasisBurstAnimationCodec.OuterHeaderBytes + compressedLength,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory
            );
            try
            {
                using var raw = new NativeArray<byte>(
                    rawLength,
                    Allocator.TempJob,
                    NativeArrayOptions.ClearMemory
                );
                using var result = new NativeArray<int>(
                    2,
                    Allocator.TempJob,
                    NativeArrayOptions.ClearMemory
                );

                WriteAnimationOuterHeader(payload, rawLength, compressedLength);
                int offset = BasisBurstAnimationCodec.OuterHeaderBytes;
                payload[offset++] = 0x1F;
                payload[offset++] = 0x7F;
                payload[offset++] = 1;
                payload[offset++] = 0;
                WriteExtendedLength(payload, ref offset, int.MaxValue - 19);
                Assert.That(offset, Is.EqualTo(payload.Length));

                new BasisLz4DecompressJob { Payload = payload, Raw = raw, Result = result }.Execute();

                Assert.That(result[1], Is.EqualTo((int)BasisAnimationCodecError.OutputOverflow));
            }
            finally
            {
                payload.Dispose();
            }
        }

        [Test]
        public void FrameMetadataRejectsCoordinateOverflow()
        {
            int recordOffset = BasisBurstAnimationCodec.BodyHeaderBytes;
            using var raw = new NativeArray<byte>(
                recordOffset + BasisBurstAnimationCodec.FrameRecordBytes,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var frames = new NativeArray<BasisAnimatedImageFrame>(
                1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var frameEnds = new NativeArray<long>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            using var errors = new NativeArray<int>(2, Allocator.Temp, NativeArrayOptions.ClearMemory);

            WriteInt32(raw, recordOffset, int.MaxValue);
            WriteInt32(raw, recordOffset + 8, 1);
            WriteInt32(raw, recordOffset + 12, 1);
            WriteInt32(raw, recordOffset + 20, 1);
            WriteInt64(raw, recordOffset + 24, BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds);
            WriteInt64(raw, recordOffset + 32, BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds);
            WriteByte(raw, recordOffset + 40, (byte)BasisAnimationBlend.Source);
            WriteByte(raw, recordOffset + 41, (byte)BasisAnimationDisposal.None);

            new BasisAnimationUnpackFramesJob
            {
                Raw = raw,
                Header = new BasisAnimationBodyHeader
                {
                    CanvasWidth = 1,
                    CanvasHeight = 1,
                    FrameCount = 1,
                    PixelCount = 1,
                    TotalDurationMicroseconds =
                        BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds,
                },
                Frames = frames,
                FrameEnds = frameEnds,
                Errors = errors,
            }.Execute(0);

            Assert.That(errors[0], Is.EqualTo((int)BasisAnimationCodecError.InvalidFrame));
        }

        [TestCase(16)]
        [TestCase(17)]
        public void AnimationDecodeFailureKeepsNonOwnedCallerPayload(int sourceLength)
        {
            var payload = new NativeArray<byte>(sourceLength, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                using BasisBurstAnimationDecodeRequest decode =
                    new BasisBurstAnimationDecodeRequest(payload, BasisBurstAnimationCodec.OuterHeaderBytes, false);
                BasisBurstAnimationDecodeResult result = decode.Complete();
                Assert.That(result.Ok, Is.False);
                Assert.DoesNotThrow(() => { byte first = payload[0]; Assert.That(first, Is.EqualTo(0)); });
            }
            finally
            {
                if (payload.IsCreated)
                    payload.Dispose();
            }
        }

        [Test]
        public void OuterHeaderReservedBytesAreRejected()
        {
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            using BasisBurstAnimationEncodeRequest request =
                new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult encoded = request.Complete();
            try
            {
                NativeArray<byte> payloadBytes = encoded.Payload.Bytes;
                payloadBytes[5] = 1;
                using BasisBurstAnimationDecodeRequest decode =
                    new BasisBurstAnimationDecodeRequest(payloadBytes, encoded.Payload.Length, false);
                BasisBurstAnimationDecodeResult result = decode.Complete();
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Error, Does.Contain("reserved"));
            }
            finally
            {
                encoded.Payload.Dispose();
            }
        }

        [Test]
        public void BodyHeaderReservedByteIsRejected()
        {
            int rawLength =
                BasisBurstAnimationCodec.BodyHeaderBytes
                + BasisBurstAnimationCodec.FrameRecordBytes
                + 4;
            using var raw = new NativeArray<byte>(rawLength, Allocator.Temp, NativeArrayOptions.ClearMemory);
            using var result = new NativeArray<BasisAnimationBodyHeader>(
                1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            WriteInt32(raw, 0, 1);
            WriteInt32(raw, 4, 1);
            WriteInt32(raw, 12, 1);
            WriteByte(raw, 23, 1);
            WriteInt64(raw, 24, BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds);
            WriteInt32(raw, 32, 1);
            WriteInt32(raw, 36, BasisBurstAnimationCodec.BodyHeaderBytes + BasisBurstAnimationCodec.FrameRecordBytes);

            new BasisAnimationParseBodyHeaderJob
            {
                Raw = raw,
                Result = result,
            }.Execute();

            Assert.That(result[0].Error, Is.EqualTo(BasisAnimationCodecError.InvalidHeader));
        }

        [Test]
        public void FrameReservedBytesAreRejected()
        {
            int recordOffset = BasisBurstAnimationCodec.BodyHeaderBytes;
            using var raw = new NativeArray<byte>(
                recordOffset + BasisBurstAnimationCodec.FrameRecordBytes,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var frames = new NativeArray<BasisAnimatedImageFrame>(
                1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var frameEnds = new NativeArray<long>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            using var errors = new NativeArray<int>(2, Allocator.Temp, NativeArrayOptions.ClearMemory);
            WriteInt32(raw, recordOffset + 8, 1);
            WriteInt32(raw, recordOffset + 12, 1);
            WriteInt32(raw, recordOffset + 20, 1);
            WriteInt64(raw, recordOffset + 24, BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds);
            WriteInt64(raw, recordOffset + 32, BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds);
            WriteByte(raw, recordOffset + 40, (byte)BasisAnimationBlend.Source);
            WriteByte(raw, recordOffset + 41, (byte)BasisAnimationDisposal.None);
            WriteByte(raw, recordOffset + 42, 1);

            new BasisAnimationUnpackFramesJob
            {
                Raw = raw,
                Header = new BasisAnimationBodyHeader
                {
                    CanvasWidth = 1,
                    CanvasHeight = 1,
                    FrameCount = 1,
                    PixelCount = 1,
                    TotalDurationMicroseconds =
                        BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds,
                },
                Frames = frames,
                FrameEnds = frameEnds,
                Errors = errors,
            }.Execute(0);

            Assert.That(errors[0], Is.EqualTo((int)BasisAnimationCodecError.InvalidFrame));
        }

        [Test]
        public void BurstCodecRejectsCorruptedPayload()
        {
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            using BasisBurstAnimationEncodeRequest request =
                new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult encoded = request.Complete();
            try
            {
                NativeArray<byte> payloadBytes = encoded.Payload.Bytes;
                payloadBytes[0] ^= 0x7F;
                using BasisBurstAnimationDecodeRequest decode =
                    new BasisBurstAnimationDecodeRequest(payloadBytes, encoded.Payload.Length, false);
                BasisBurstAnimationDecodeResult result = decode.Complete();
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Error, Does.Contain("magic"));
            }
            finally
            {
                encoded.Payload.Dispose();
            }
        }

        [Test]
        public void BurstPacketBuilderCreatesBoundedBatches()
        {
            using BasisAnimatedImageData source = DecodeGif(AnimatedGif);
            using BasisBurstAnimationEncodeRequest encode =
                new BasisBurstAnimationEncodeRequest(source);
            BasisBurstAnimationEncodeResult encoded = encode.Complete();
            try
            {
                using var packetRequest = BasisAnimatedImageJobs.SchedulePacketBuild(
                    Guid.NewGuid(),
                    encoded.Payload,
                    638000000000000000L,
                    2,
                    6,
                    7,
                    32,
                    0,
                    2
                );
                using BasisAnimationPacketBatch batch = packetRequest.Complete();
                Assert.That(batch.Ok, Is.True, batch.Error);
                Assert.That(batch.HasHeader, Is.True);
                Assert.That(batch.PacketCount, Is.InRange(1, 2));
                var header = new byte[batch.HeaderLength];
                batch.CopyHeaderTo(header);
                Assert.That(header[0], Is.EqualTo(6));
                var packet = new byte[batch.GetPacketLength(0)];
                batch.CopyPacketTo(0, packet);
                Assert.That(packet[0], Is.EqualTo(7));
            }
            finally
            {
                encoded.Payload.Dispose();
            }
        }

        [Test]
        public void AsynchronousGifPipelineFinalizesPoster()
        {
            string path = Path.Combine(Path.GetTempPath(), $"BasisBurstGif_{Guid.NewGuid():N}.gif");
            File.WriteAllBytes(path, Convert.FromBase64String(AnimatedGif));
            BasisImageValidationResult finalized = default;
            try
            {
                using var request = BasisAnimatedImageJobs.ScheduleGifDecode(path);
                BasisGifDecodeJobResult worker = request.Complete();
                Assert.That(worker.CleanPng, Is.Not.Null.And.Not.Empty);
                Assert.That(worker.PosterPixels, Has.Length.EqualTo(2));
                finalized = BasisAnimatedImageJobs.FinalizeGifDecode(worker);
                Assert.That(finalized.Ok, Is.True, finalized.Error);
                Assert.That(finalized.Animation.FrameCount, Is.EqualTo(2));
                Assert.That(finalized.AnimationPayload, Is.Not.Null);
            }
            finally
            {
                if (finalized.Texture != null)
                    UnityEngine.Object.DestroyImmediate(finalized.Texture);
                finalized.Animation?.Dispose();
                finalized.AnimationPayload?.Dispose();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static byte[] InsertBytes(byte[] source, int offset, byte[] inserted)
        {
            var combined = new byte[source.Length + inserted.Length];
            Buffer.BlockCopy(source, 0, combined, 0, offset);
            Buffer.BlockCopy(inserted, 0, combined, offset, inserted.Length);
            Buffer.BlockCopy(
                source,
                offset,
                combined,
                offset + inserted.Length,
                source.Length - offset
            );
            return combined;
        }

        private static int FindGraphicControlExtension(byte[] source, int startIndex)
        {
            int sourceLength = source.Length;
            for (int i = Math.Max(0, startIndex); i + 3 < sourceLength; i++)
            {
                if (source[i] == 0x21 && source[i + 1] == 0xF9 && source[i + 2] == 4)
                    return i;
            }
            return -1;
        }

        private static int CalculateExtendedLengthByteCount(int value)
        {
            return value / byte.MaxValue + 1;
        }

        private static void WriteExtendedLength(
            NativeArray<byte> destination,
            ref int offset,
            int value
        )
        {
            while (value >= byte.MaxValue)
            {
                destination[offset++] = byte.MaxValue;
                value -= byte.MaxValue;
            }
            destination[offset++] = (byte)value;
        }

        private static void WriteAnimationOuterHeader(
            NativeArray<byte> destination,
            int rawLength,
            int compressedLength
        )
        {
            WriteInt32(destination, 0, (int)BasisBurstAnimationCodec.Magic);
            destination[4] = BasisBurstAnimationCodec.Version;
            destination[5] = 0;
            destination[6] = 0;
            destination[7] = 0;
            WriteInt32(destination, 8, rawLength);
            WriteInt32(destination, 12, compressedLength);
        }

        private static void WriteByte(NativeArray<byte> destination, int offset, byte value)
        {
            destination[offset] = value;
        }

        private static void WriteInt32(NativeArray<byte> destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(NativeArray<byte> destination, int offset, long value)
        {
            ulong unsigned = (ulong)value;
            for (int i = 0; i < 8; i++)
                destination[offset + i] = (byte)(unsigned >> (i * 8));
        }

        private static BasisAnimatedImageData DecodeGif(string encoded)
        {
            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(Convert.FromBase64String(encoded));
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Ok, Is.True, result.Error);
            return result.TakeAnimation();
        }
    }
}
