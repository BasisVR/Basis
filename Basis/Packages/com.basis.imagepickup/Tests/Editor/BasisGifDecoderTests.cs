using System;
using System.IO;
using System.Text;
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

        [Test]
        public void GifPipelineShipsSourceBytesAsPayload()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            using (var request = BasisAnimatedImageJobs.ScheduleGifDecode(source))
            {
                BasisGifDecodeJobResult worker = request.Complete();
                Assert.That(worker.Ok, Is.True, worker.Error);
                Assert.That(worker.AnimationNetworkError, Is.Null);
                Assert.That(worker.AnimationPayload, Is.Not.Null);
                Assert.That(worker.AnimationPayload.Format, Is.EqualTo(BasisNativeAnimationPayload.FormatGif));
                Assert.That(worker.AnimationPayload.Length, Is.EqualTo(source.Length));
                Assert.That(worker.AnimationPayload.AllocatedBytes, Is.EqualTo(source.Length));
                Assert.That(worker.AnimationPayload.Bytes.ToArray(), Is.EqualTo(source));
                Assert.That(
                    BasisNativeAnimationPayload.TotalAllocatedBytes,
                    Is.EqualTo(payloadBytesBefore + source.Length)
                );
            }
            Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
        }

        [Test]
        public void GifPayloadRestoresThroughGifDecoder()
        {
            byte[] source = Convert.FromBase64String(InterlacedPreviousGif);
            using var request = BasisAnimatedImageJobs.ScheduleGifDecode(source);
            BasisGifDecodeJobResult worker = request.Complete();
            Assert.That(worker.Ok, Is.True, worker.Error);
            using BasisNativeAnimationPayload payload = worker.TakeAnimationPayload();
            Assert.That(payload.Format, Is.EqualTo(BasisNativeAnimationPayload.FormatGif));
            using IBasisAnimationDecodeRequest reload = BasisAnimatedImageJobs.ScheduleAnimationDecode(
                payload,
                BasisAnimationDecodeTrust.UntrustedRemote
            );
            BasisBurstAnimationDecodeResult restored;
            while (!reload.TryComplete(out restored))
                Thread.Yield();
            Assert.That(restored.Ok, Is.True, restored.Error);
            using (restored.Animation)
            {
                Assert.That(restored.Animation.FrameCount, Is.EqualTo(worker.Animation.FrameCount));
                Assert.That(restored.Animation.GetFrame(1).Disposal, Is.EqualTo(BasisAnimationDisposal.Previous));
                Assert.That(
                    restored.Animation.CopyFramePixelsToManaged(1),
                    Is.EqualTo(worker.Animation.CopyFramePixelsToManaged(1))
                );
            }
        }

        [Test]
        public void GifScanReportsDecodedSizeAndHonorsPixelLimit()
        {
            byte[] source = Convert.FromBase64String(InterlacedPreviousGif);
            using var native = new NativeArray<byte>(source, Allocator.Persistent);
            Assert.That(
                BasisBurstGifDecoder.TryScan(
                    native,
                    native.Length,
                    BasisImagePickupSettings.MaxAnimationDecodedFramePixels,
                    out int frameCount,
                    out int pixelCount,
                    out string error
                ),
                Is.True,
                error
            );
            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult decoded = request.Complete();
            Assert.That(decoded.Ok, Is.True, decoded.Error);
            Assert.That(frameCount, Is.EqualTo(decoded.Animation.FrameCount));
            Assert.That(pixelCount, Is.EqualTo(decoded.Animation.DecodedFramePixels));
            Assert.That(
                BasisBurstGifDecoder.TryScan(native, native.Length, pixelCount - 1, out _, out _, out error),
                Is.False
            );
            Assert.That(error, Does.Contain("frame-pixel budget"));
        }

        [Test]
        public void PosterlessGifRequestSkipsPosterAndReturnsSource()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            using var native = new NativeArray<byte>(source, Allocator.Persistent);
            using var request = new BasisBurstGifDecodeRequest(
                native,
                native.Length,
                false,
                BasisAnimationDecodeTrust.TrustedLocal
            );
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.PosterPixels.IsCreated, Is.False);
            Assert.That(result.Source.IsCreated, Is.True);
            Assert.That(result.Source.ToArray(), Is.EqualTo(source));
            Assert.That(result.Animation.FrameCount, Is.EqualTo(2));
        }

        [Test]
        public void RemoteGifDecodedPixelLimitNeverExceedsLocal()
        {
            long local = BasisBurstGifDecoder.ResolveDecodedPixelLimit(BasisAnimationDecodeTrust.TrustedLocal);
            long remote = BasisBurstGifDecoder.ResolveDecodedPixelLimit(BasisAnimationDecodeTrust.UntrustedRemote);
            Assert.That(local, Is.EqualTo(BasisImagePickupSettings.MaxAnimationDecodedFramePixels));
            Assert.That(remote, Is.InRange(1L, local));
        }

        [Test]
        public void DecodedFramesMatchTheirSourceIndicesAcrossPalettesInterlaceAndDictionaryResets()
        {
            var random = new System.Random(20260914);
            var builder = new GifBuilder(200, 150);
            builder.AddFrame(0, 0, 200, 150, RandomPalette(random, 256), 8, RandomIndices(random, 200 * 150, 256), 4);
            builder.AddFrame(10, 20, 97, 61, RandomPalette(random, 16), 4, RunIndices(random, 97 * 61, 16), 4, interlaced: true);
            builder.AddFrame(150, 100, 33, 17, RandomPalette(random, 2), 2, RandomIndices(random, 33 * 17, 2), 4, transparentIndex: 1, disposal: 2);
            builder.AddFrame(5, 5, 180, 140, RandomPalette(random, 16), 4, RandomIndices(random, 180 * 140, 16), 4, disposal: 3);

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(builder.Finish());
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Animation.FrameCount, Is.EqualTo(builder.Frames.Count));
            for (int frame = 0; frame < builder.Frames.Count; frame++)
            {
                Assert.That(
                    result.Animation.CopyFramePixelsToManaged(frame),
                    Is.EqualTo(builder.Frames[frame].ExpectedPixels()),
                    $"frame {frame}"
                );
            }
            Assert.That(result.Animation.GetFrame(2).Blend, Is.EqualTo(BasisAnimationBlend.Over));
            Assert.That(result.Animation.GetFrame(2).Disposal, Is.EqualTo(BasisAnimationDisposal.Background));
            Assert.That(result.Animation.GetFrame(3).Disposal, Is.EqualTo(BasisAnimationDisposal.Previous));
            Assert.That(result.PosterPixels.ToArray(), Is.EqualTo(builder.Frames[0].ExpectedPixels()));
        }

        [Test]
        public void AnIndexPastASmallColorTableFailsTheDecode()
        {
            var builder = new GifBuilder(4, 1);
            builder.AddFrame(0, 0, 4, 1, new byte[] { 0, 0, 0, 255, 255, 255 }, 2, new byte[] { 0, 1, 3, 1 }, 4);

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(builder.Finish());
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("palette index"));
        }

        [TestCase(1, "exceeds the frame size")]
        [TestCase(-1, "does not match the frame")]
        public void LzwOutputThatDoesNotFillTheFrameExactlyFailsTheDecode(int extraPixels, string expectedError)
        {
            var random = new System.Random(7);
            var builder = new GifBuilder(16, 8);
            builder.AddFrame(0, 0, 16, 8, RandomPalette(random, 4), 2, RandomIndices(random, 16 * 8 + extraPixels, 4), 4);

            using BasisBurstGifDecodeRequest request = BasisBurstGifDecoder.Schedule(builder.Finish());
            using BasisBurstGifDecodeResult result = request.Complete();
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain(expectedError));
        }

        [Test]
        public void SavedGifDecodesToTheSameAnimationAsItsSource()
        {
            var random = new System.Random(1070);
            var builder = new GifBuilder(200, 150);
            builder.AddFrame(0, 0, 200, 150, RandomPalette(random, 256), 8, RandomIndices(random, 200 * 150, 256), 4);
            builder.AddFrame(10, 20, 97, 61, RandomPalette(random, 16), 4, RunIndices(random, 97 * 61, 16), 4, interlaced: true);
            builder.AddFrame(150, 100, 33, 17, RandomPalette(random, 2), 2, RandomIndices(random, 33 * 17, 2), 4, transparentIndex: 1, disposal: 2);
            builder.AddFrame(5, 5, 180, 140, RandomPalette(random, 16), 4, RandomIndices(random, 180 * 140, 16), 4, disposal: 3);

            AssertSavedGifMatchesSource(builder.Finish());
        }

        [TestCase(AnimatedGif)]
        [TestCase(InterlacedPreviousGif)]
        public void SavedSampleGifDecodesToTheSameAnimationAsItsSource(string encoded)
        {
            AssertSavedGifMatchesSource(Convert.FromBase64String(encoded));
        }

        [Test]
        public void ClipboardCopyOffersTheSanitizedGifAheadOfThePoster()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] poster;
            try
            {
                poster = texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            var formats = BasisImagePickupObject.BuildClipboardFormats(poster, source);

            Assert.That(formats.Count, Is.EqualTo(3));
            Assert.That(formats[0].Format, Is.EqualTo(Basis.Scripts.Platform.BasisClipboardImageFormat.Gif));
            Assert.That(formats[0].Data, Is.EqualTo(SaveGif(source)));
            Assert.That(formats[1].Format, Is.EqualTo(Basis.Scripts.Platform.BasisClipboardImageFormat.Png));
            Assert.That(formats[2].Format, Is.EqualTo(Basis.Scripts.Platform.BasisClipboardImageFormat.Bitmap));
        }

        [Test]
        public void SavedGifDropsEverythingTheDecoderSkips()
        {
            byte[] marker = Encoding.ASCII.GetBytes("BASIS-SAVE-MARKER");
            var random = new System.Random(7);
            var builder = new GifBuilder(16, 8);
            builder.AddFrame(0, 0, 16, 8, RandomPalette(random, 4), 2, RandomIndices(random, 16 * 8, 4), 4, dataAfterEnd: marker);
            byte[] source = builder.Finish();
            int graphicControl = FindGraphicControlExtension(source, 0);
            Assert.That(graphicControl, Is.GreaterThanOrEqualTo(0));
            source = InsertBytes(source, graphicControl, Extension(0xFE, marker));
            source = InsertBytes(source, graphicControl, ApplicationExtension("XMP DataXMP", marker));
            source = InsertBytes(source, source.Length, marker);

            byte[] saved = AssertSavedGifMatchesSource(source);
            Assert.That(IndexOf(saved, marker), Is.EqualTo(-1));
            Assert.That(saved.Length, Is.LessThan(source.Length));
        }

        [Test]
        public void SavedGifDropsAGraphicControlThatPlainTextConsumed()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            int graphicControl = FindGraphicControlExtension(source, 0);
            Assert.That(graphicControl, Is.GreaterThanOrEqualTo(0));
            source = InsertBytes(source, graphicControl + 8, Extension(0x01, new byte[] { 0, 0, 0, 0, 1, 0, 1, 0, 8, 8, 1, 0 }));

            byte[] saved = AssertSavedGifMatchesSource(source);
            int savedControl = FindGraphicControlExtension(saved, 0);
            Assert.That(savedControl, Is.GreaterThanOrEqualTo(0));
            Assert.That(FindGraphicControlExtension(saved, savedControl + 1), Is.EqualTo(-1));
        }

        [Test]
        public void SavedGifKeepsAFiniteLoopCount()
        {
            byte[] source = Convert.FromBase64String(AnimatedGif);
            int identifier = IndexOf(source, Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            Assert.That(identifier, Is.GreaterThan(0));
            source[identifier + 13] = 3;

            byte[] saved = AssertSavedGifMatchesSource(source);
            using BasisAnimatedImageData animation = DecodeGif(Convert.ToBase64String(saved));
            Assert.That(animation.TotalPlayCount, Is.EqualTo(4));
        }

        [Test]
        public void SavingRejectsAGifTheDecoderRejects()
        {
            var builder = new GifBuilder(4, 1);
            builder.AddFrame(0, 0, 4, 1, new byte[] { 0, 0, 0, 255, 255, 255 }, 2, new byte[] { 0, 1, 3, 1 }, 4);
            AssertSaveRejected(builder.Finish());

            var random = new System.Random(7);
            builder = new GifBuilder(16, 8);
            builder.AddFrame(0, 0, 16, 8, RandomPalette(random, 4), 2, RandomIndices(random, 16 * 8 + 1, 4), 4);
            AssertSaveRejected(builder.Finish());

            byte[] sample = Convert.FromBase64String(AnimatedGif);
            Array.Resize(ref sample, sample.Length / 2);
            AssertSaveRejected(sample);
            AssertSaveRejected(Array.Empty<byte>());
        }

        private static byte[] SaveGif(byte[] source)
        {
            using var stream = new MemoryStream();
            Assert.That(BasisGifSanitizer.TryWrite(source, stream, out string error), Is.True, error);
            return stream.ToArray();
        }

        private static void AssertSaveRejected(byte[] source)
        {
            using var stream = new MemoryStream();
            Assert.That(BasisGifSanitizer.TryWrite(source, stream, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        private static byte[] AssertSavedGifMatchesSource(byte[] source)
        {
            byte[] saved = SaveGif(source);
            Assert.That(saved, Is.EqualTo(SaveGif(saved)), "saving a saved GIF must reproduce it");

            using BasisBurstGifDecodeRequest sourceRequest = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult expected = sourceRequest.Complete();
            using BasisBurstGifDecodeRequest savedRequest = BasisBurstGifDecoder.Schedule(saved);
            using BasisBurstGifDecodeResult actual = savedRequest.Complete();
            Assert.That(expected.Ok, Is.True, expected.Error);
            Assert.That(actual.Ok, Is.True, actual.Error);

            BasisAnimatedImageData expectedAnimation = expected.Animation;
            BasisAnimatedImageData actualAnimation = actual.Animation;
            Assert.That(actualAnimation.CanvasWidth, Is.EqualTo(expectedAnimation.CanvasWidth));
            Assert.That(actualAnimation.CanvasHeight, Is.EqualTo(expectedAnimation.CanvasHeight));
            Assert.That(actualAnimation.TotalPlayCount, Is.EqualTo(expectedAnimation.TotalPlayCount));
            Assert.That(actualAnimation.TotalDurationMicroseconds, Is.EqualTo(expectedAnimation.TotalDurationMicroseconds));
            Assert.That(actualAnimation.BackgroundColor, Is.EqualTo(expectedAnimation.BackgroundColor));
            Assert.That(actualAnimation.HasAnyAlpha, Is.EqualTo(expectedAnimation.HasAnyAlpha));
            Assert.That(actualAnimation.RequiresPreviousCanvas, Is.EqualTo(expectedAnimation.RequiresPreviousCanvas));
            Assert.That(actualAnimation.FrameCount, Is.EqualTo(expectedAnimation.FrameCount));
            for (int frame = 0; frame < expectedAnimation.FrameCount; frame++)
            {
                BasisAnimatedImageFrame expectedFrame = expectedAnimation.GetFrame(frame);
                BasisAnimatedImageFrame actualFrame = actualAnimation.GetFrame(frame);
                Assert.That(
                    (actualFrame.X, actualFrame.Y, actualFrame.Width, actualFrame.Height, actualFrame.DurationMicroseconds, actualFrame.Blend, actualFrame.Disposal),
                    Is.EqualTo((expectedFrame.X, expectedFrame.Y, expectedFrame.Width, expectedFrame.Height, expectedFrame.DurationMicroseconds, expectedFrame.Blend, expectedFrame.Disposal)),
                    $"frame {frame}"
                );
                Assert.That(
                    actualAnimation.CopyFramePixelsToManaged(frame),
                    Is.EqualTo(expectedAnimation.CopyFramePixelsToManaged(frame)),
                    $"frame {frame} pixels"
                );
            }
            Assert.That(actual.PosterPixels.ToArray(), Is.EqualTo(expected.PosterPixels.ToArray()));
            return saved;
        }

        private static byte[] Extension(byte label, byte[] data)
        {
            var extension = new byte[data.Length + 4];
            extension[0] = 0x21;
            extension[1] = label;
            extension[2] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, extension, 3, data.Length);
            return extension;
        }

        private static byte[] ApplicationExtension(string identifier, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(identifier);
            var extension = new byte[name.Length + data.Length + 5];
            extension[0] = 0x21;
            extension[1] = 0xFF;
            extension[2] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, extension, 3, name.Length);
            extension[3 + name.Length] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, extension, 4 + name.Length, data.Length);
            return extension;
        }

        private static int IndexOf(byte[] source, byte[] value)
        {
            for (int i = 0; i + value.Length <= source.Length; i++)
            {
                int matched = 0;
                while (matched < value.Length && source[i + matched] == value[matched])
                    matched++;
                if (matched == value.Length)
                    return i;
            }
            return -1;
        }

        private static byte[] RandomPalette(System.Random random, int colors)
        {
            var palette = new byte[colors * 3];
            random.NextBytes(palette);
            return palette;
        }

        private static byte[] RandomIndices(System.Random random, int count, int colors)
        {
            var indices = new byte[count];
            for (int i = 0; i < count; i++)
                indices[i] = (byte)random.Next(colors);
            return indices;
        }

        private static byte[] RunIndices(System.Random random, int count, int colors)
        {
            var indices = new byte[count];
            int written = 0;
            while (written < count)
            {
                byte color = (byte)random.Next(colors);
                int run = Math.Min(count - written, 1 + random.Next(40));
                for (int i = 0; i < run; i++)
                    indices[written++] = color;
            }
            return indices;
        }

        private sealed class GifFrame
        {
            public int Width;
            public int Height;
            public int TransparentIndex;
            public byte[] Indices;
            public byte[] PaletteRgb;

            public Color32[] ExpectedPixels()
            {
                var pixels = new Color32[Width * Height];
                for (int row = 0; row < Height; row++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int index = Indices[row * Width + x];
                        pixels[(Height - 1 - row) * Width + x] =
                            index == TransparentIndex
                                ? new Color32(0, 0, 0, 0)
                                : new Color32(PaletteRgb[index * 3], PaletteRgb[index * 3 + 1], PaletteRgb[index * 3 + 2], 255);
                    }
                }
                return pixels;
            }
        }

        private sealed class GifBuilder
        {
            private readonly MemoryStream _stream = new MemoryStream();
            public readonly System.Collections.Generic.List<GifFrame> Frames = new System.Collections.Generic.List<GifFrame>();

            public GifBuilder(int width, int height)
            {
                _stream.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, 0, 6);
                WriteUInt16(width);
                WriteUInt16(height);
                _stream.WriteByte(0);
                _stream.WriteByte(0);
                _stream.WriteByte(0);
            }

            public void AddFrame(
                int left,
                int top,
                int width,
                int height,
                byte[] paletteRgb,
                int minimumCodeSize,
                byte[] indices,
                int delay,
                bool interlaced = false,
                int transparentIndex = -1,
                int disposal = 1,
                byte[] dataAfterEnd = null
            )
            {
                Frames.Add(new GifFrame
                {
                    Width = width,
                    Height = height,
                    TransparentIndex = transparentIndex,
                    Indices = indices,
                    PaletteRgb = paletteRgb,
                });

                _stream.WriteByte(0x21);
                _stream.WriteByte(0xF9);
                _stream.WriteByte(4);
                _stream.WriteByte((byte)((disposal << 2) | (transparentIndex >= 0 ? 1 : 0)));
                WriteUInt16(delay);
                _stream.WriteByte((byte)Math.Max(0, transparentIndex));
                _stream.WriteByte(0);

                int colors = paletteRgb.Length / 3;
                int tableBits = 1;
                while ((1 << tableBits) < colors)
                    tableBits++;
                _stream.WriteByte(0x2C);
                WriteUInt16(left);
                WriteUInt16(top);
                WriteUInt16(width);
                WriteUInt16(height);
                _stream.WriteByte((byte)(0x80 | (interlaced ? 0x40 : 0) | (tableBits - 1)));
                _stream.Write(paletteRgb, 0, paletteRgb.Length);
                for (int pad = paletteRgb.Length; pad < (1 << tableBits) * 3; pad++)
                    _stream.WriteByte(0);

                _stream.WriteByte((byte)minimumCodeSize);
                byte[] data = EncodeLzw(interlaced ? Interlace(indices, width, height) : indices, minimumCodeSize);
                if (dataAfterEnd != null)
                    data = InsertBytes(data, data.Length, dataAfterEnd);
                for (int offset = 0; offset < data.Length; offset += 255)
                {
                    int count = Math.Min(255, data.Length - offset);
                    _stream.WriteByte((byte)count);
                    _stream.Write(data, offset, count);
                }
                _stream.WriteByte(0);
            }

            public byte[] Finish()
            {
                _stream.WriteByte(0x3B);
                return _stream.ToArray();
            }

            private void WriteUInt16(int value)
            {
                _stream.WriteByte((byte)value);
                _stream.WriteByte((byte)(value >> 8));
            }

            private static byte[] Interlace(byte[] indices, int width, int height)
            {
                var stream = new byte[indices.Length];
                int written = 0;
                int[] starts = { 0, 4, 2, 1 };
                int[] steps = { 8, 8, 4, 2 };
                for (int pass = 0; pass < 4; pass++)
                {
                    for (int row = starts[pass]; row < height; row += steps[pass])
                    {
                        Buffer.BlockCopy(indices, row * width, stream, written, width);
                        written += width;
                    }
                }
                return stream;
            }

            private static byte[] EncodeLzw(byte[] indices, int minimumCodeSize)
            {
                var output = new System.Collections.Generic.List<byte>();
                var dictionary = new System.Collections.Generic.Dictionary<int, int>();
                int clearCode = 1 << minimumCodeSize;
                int endCode = clearCode + 1;
                int codeSize = minimumCodeSize + 1;
                int nextCode = endCode + 1;
                int bitBuffer = 0;
                int bitCount = 0;

                void Emit(int code)
                {
                    bitBuffer |= code << bitCount;
                    bitCount += codeSize;
                    while (bitCount >= 8)
                    {
                        output.Add((byte)bitBuffer);
                        bitBuffer >>= 8;
                        bitCount -= 8;
                    }
                }

                Emit(clearCode);
                int prefix = indices[0];
                for (int i = 1; i < indices.Length; i++)
                {
                    int key = (prefix << 8) | indices[i];
                    if (dictionary.TryGetValue(key, out int existing))
                    {
                        prefix = existing;
                        continue;
                    }
                    Emit(prefix);
                    if (nextCode < 4096)
                    {
                        dictionary[key] = nextCode++;
                        if (nextCode > (1 << codeSize) && codeSize < 12)
                            codeSize++;
                    }
                    else
                    {
                        Emit(clearCode);
                        dictionary.Clear();
                        codeSize = minimumCodeSize + 1;
                        nextCode = endCode + 1;
                    }
                    prefix = indices[i];
                }
                Emit(prefix);
                Emit(endCode);
                if (bitCount > 0)
                    output.Add((byte)bitBuffer);
                return output.ToArray();
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
