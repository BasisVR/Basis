using Basis.BasisUI;
using Basis.Scripts.Platform;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Arms the image pickup service at startup and subscribes it to the client's shared OS bridges:
    /// the file-drop bridge for images dragged in as files, and the clipboard bridge for images pasted
    /// as data. The drop bridge owns the one window subclass there can be; this package only says
    /// which of the dropped paths are its own — <see cref="BasisImagePickupManager.SpawnFromFiles"/>
    /// keeps the supported image formats and ignores everything else in the batch.
    ///
    /// A paste arrives as bytes with no name attached, and often in no file format at all: "copy
    /// image" in most Windows applications yields a raw device-independent bitmap. Turning that into
    /// something the import pipeline accepts is this class's other job.
    /// </summary>
    public static class BasisImagePickupBootstrap
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.System;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisImagePickupManager.Initialize();

            BasisDesktopFileDrop.RegisterAcceptedExtensions(".png", ".jpg", ".jpeg", ".gif");
            BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
            BasisDesktopFileDrop.OnFilesDropped += OnFilesDropped;

            BasisDesktopClipboard.OnImagePasted -= OnImagePasted;
            BasisDesktopClipboard.OnImagePasted += OnImagePasted;
        }

        private static void OnFilesDropped(string[] paths)
        {
            BasisImagePickupManager.SpawnFromFiles(paths);
        }

        private static void OnImagePasted(BasisClipboardImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0)
                return;

            switch (image.Format)
            {
                case BasisClipboardImageFormat.Png:
                case BasisClipboardImageFormat.Gif:
                case BasisClipboardImageFormat.Jpeg:
                    // Already a file in everything but name; the import queue sniffs the signature and
                    // applies exactly the caps a dropped file of the same format would get.
                    BasisImagePickupManager.SpawnFromImageData(image.Data, DescribeFormat(image.Format));
                    break;

                case BasisClipboardImageFormat.Bitmap:
                    SpawnFromClipboardBitmap(image.Data);
                    break;
            }
        }

        /// <summary>
        /// Unpacks a clipboard bitmap and hands the pixels to the manager. A DIB has no signature for
        /// the import queue to sniff, so it is converted here — to a PNG when the header hid one, and
        /// to raw pixels otherwise, which skips a decode that would only undo the encode.
        ///
        /// The unpack runs on the paste rather than in the paced import queue on purpose: it is a
        /// single linear pass over the rows, far cheaper than the decode and re-encode that follow it,
        /// and doing it here is what lets the queue see a normal image and stay one code path.
        /// </summary>
        private static void SpawnFromClipboardBitmap(byte[] dib)
        {
            BasisDibDecodeResult decoded = BasisDibImage.Decode(dib, BasisImagePickupSettings.MaxSourceDimension, BasisImagePickupSettings.MaxSourceTotalPixels);
            if (!decoded.Ok)
            {
                BasisImagePickupRejectionPopup.Show(DescribeFormat(BasisClipboardImageFormat.Bitmap), decoded.Error);
                BasisDebug.LogWarning($"Image pickup rejected: {decoded.Error}", LogTag);
                return;
            }

            if (decoded.Kind != BasisDibPayloadKind.Pixels)
            {
                BasisImagePickupManager.SpawnFromImageData(
                    decoded.Encoded,
                    DescribeFormat(BasisClipboardImageFormat.Bitmap)
                );
                return;
            }

            BasisImagePickupManager.SpawnFromRgba32(
                decoded.Rgba,
                decoded.Width,
                decoded.Height,
                DescribeFormat(BasisClipboardImageFormat.Bitmap)
            );
        }

        private static string DescribeFormat(BasisClipboardImageFormat format)
        {
            return BasisLocalization.Get(
                format switch
                {
                    BasisClipboardImageFormat.Png => "imagePickup.clipboard.png",
                    BasisClipboardImageFormat.Gif => "imagePickup.clipboard.gif",
                    BasisClipboardImageFormat.Jpeg => "imagePickup.clipboard.jpeg",
                    _ => "imagePickup.clipboard.bitmap",
                }
            );
        }
    }
}
