using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace Basis.Scripts.Platform
{

    /// <summary>One clipboard payload, as read at paste time. Bytes are owned by the receiver.</summary>
    public sealed class BasisClipboardImage
    {
        public BasisClipboardImageFormat Format;
        public byte[] Data;
    }

    /// <summary>
    /// The client's clipboard-paste bridge, and the counterpart to <see cref="BasisDesktopFileDrop"/>:
    /// that one covers images the user drags in as files, this one covers images the user copies as
    /// data — a screenshot tool, "copy image" in a browser, a paint program — where nothing was ever
    /// written to disk and there is no path to hand anyone.
    ///
    /// Unlike the file drop this needs no window subclass. Windows delivers no paste message to a game
    /// window (WM_PASTE is an edit-control convention), so the chord is read from the input system in
    /// <see cref="Dispatch"/> and the clipboard is opened right there on the main thread. That also
    /// means it works in the editor, where the file drop's WndProc hook cannot run.
    ///
    /// Formats are tried richest-first: a real PNG or GIF keeps its alpha and its animation, while
    /// <see cref="BasisClipboardImageFormat.Bitmap"/> is the lossy floor that every source offers.
    /// Copied *files* are forwarded to <see cref="BasisDesktopFileDrop.SubmitDroppedFiles"/> instead,
    /// so pasting a file from Explorer behaves exactly like dragging it in.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisDesktopClipboard
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.System;

        /// <summary>
        /// Largest clipboard payload that will be copied out of shared memory. Deliberately generous —
        /// an uncompressed bitmap of a 4K screen is already 33 MB — because the subscriber applies the
        /// real limits and can explain them; this only stops an absurd allocation from being attempted
        /// at all. The blob never reaches the network: subscribers re-encode before sharing anything.
        /// </summary>
        public const int MaxClipboardImageBytes = 128 * 1024 * 1024;

        /// <summary>
        /// Raised on the main thread once per paste that produced image data. The payload is not
        /// validated in any way — a subscriber decides whether it can use the format, whether the
        /// bytes decode, and whether the result is within its own limits.
        /// </summary>
        public static event Action<BasisClipboardImage> OnImagePasted;

        private static readonly ConcurrentQueue<BasisClipboardImage> _pending = new();
        public static event Action<string> OnTextPasted;
        private static readonly ConcurrentQueue<string> _pendingText = new();

        /// <summary>
        /// When false the paste chord is ignored, though <see cref="SubmitImage"/> still works. Lets a
        /// mode that owns the keyboard outright suppress pasting without unsubscribing anyone.
        /// </summary>
        public static bool ChordEnabled { get; set; } = true;

        private static bool _chordHeld;

        /// <summary>
        /// Queue a payload as though the user had pasted it. Used by tests and by any UI that wants an
        /// explicit "paste image" button; safe to call from any thread.
        /// </summary>
        public static void SubmitImage(BasisClipboardImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return;
            if (image.Format == BasisClipboardImageFormat.None) return;
            _pending.Enqueue(image);
        }

        public static void SubmitText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _pendingText.Enqueue(text);
        }

        /// <summary>
        /// Reads the paste chord, captures the clipboard when it fires, and raises
        /// <see cref="OnImagePasted"/> for everything queued. Called once per frame from
        /// BasisEventDriver's central tick.
        ///
        /// Subscribers are invoked one at a time rather than through the multicast delegate, matching
        /// <see cref="BasisDesktopFileDrop.Dispatch"/>: one throwing subscriber must not deny the
        /// others a paste that was theirs.
        /// </summary>
        public static void Dispatch()
        {
            PollPasteChord();
            DispatchText();

            int count = _pending.Count;
            if (count == 0) return;

            Delegate[] subscribers = OnImagePasted?.GetInvocationList();

            for (int i = 0; i < count; i++)
            {
                if (!_pending.TryDequeue(out BasisClipboardImage image)) break;
                if (subscribers == null) continue;

                for (int s = 0; s < subscribers.Length; s++)
                {
                    try
                    {
                        ((Action<BasisClipboardImage>)subscribers[s]).Invoke(image);
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError(
                            $"Clipboard paste: {subscribers[s].Method?.DeclaringType?.Name} failed on a "
                                + $"{image.Format} payload ({exception.Message}).",
                            LogTag);
                    }
                }
            }
        }

        private static void DispatchText()
        {
            if (_pendingText.IsEmpty) return;
            int count = _pendingText.Count;
            Delegate[] subscribers = OnTextPasted?.GetInvocationList();
            for (int i = 0; i < count; i++)
            {
                if (!_pendingText.TryDequeue(out string text)) break;
                if (subscribers == null) continue;
                for (int s = 0; s < subscribers.Length; s++)
                {
                    try
                    {
                        ((Action<string>)subscribers[s]).Invoke(text);
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError($"Clipboard paste: {subscribers[s].Method?.DeclaringType?.Name} failed on pasted text ({exception.Message}).", LogTag);
                    }
                }
            }
        }

        private static void CaptureText()
        {
            string text = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(text)) SubmitText(text);
        }

        private static void PollPasteChord()
        {
            if (!ChordEnabled)
            {
                _chordHeld = false;
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                _chordHeld = false;
                return;
            }

            // Ctrl+V on Windows and Linux, Cmd+V on macOS, matching every other paste on the platform.
            bool modifier = keyboard.ctrlKey.isPressed || keyboard.leftCommandKey.isPressed || keyboard.rightCommandKey.isPressed;
            bool held = modifier && keyboard.vKey.isPressed;
            if (!held)
            {
                _chordHeld = false;
                return;
            }

            // Edge-triggered: holding the chord down pastes once, not once per frame.
            if (_chordHeld) return;
            _chordHeld = true;

            // A focused text field owns Ctrl+V — the user is pasting a URL or a name, and raising an
            // image card on top of that would be both surprising and unrecoverable.
            if (IsTextInputFocused()) return;
            if (!Application.isFocused) return;

            Capture();
        }

        /// <summary>
        /// True when a UI text field currently has focus. Checked against the field's own focus state
        /// rather than mere selection, because a selected-but-unfocused field does not take keystrokes.
        /// </summary>
        private static bool IsTextInputFocused()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null) return false;

            if (selected.TryGetComponent(out TMPro.TMP_InputField tmp)) return tmp.isFocused;
            if (selected.TryGetComponent(out InputField legacy)) return legacy.isFocused;
            return false;
        }

#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        /// <summary>
        /// Reads the clipboard text. Images are Windows only: the other desktop platforms need their own
        /// native clipboard access, and there is none to build on here.
        /// </summary>
        public static void Capture()
        {
            CaptureText();
        }

        public static bool CanCopyImages => false;

        public static bool CopyImage(IReadOnlyList<BasisClipboardImage> formats) => false;
#else
        private const uint CF_DIB = 8;
        private const uint CF_DIBV5 = 17;
        private const uint CF_HDROP = 15;

        private const int OpenClipboardAttempts = 8;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("shell32.dll", CharSet = CharSet.Auto)] private static extern uint DragQueryFile(IntPtr hDrop, uint file, StringBuilder buffer, uint length);

        private static bool _formatsRegistered;
        private static uint _pngFormat;
        private static uint _gifFormat;
        private static uint _jpegFormat;
        private static uint _mimePngFormat;
        private static uint _mimeGifFormat;
        private static uint _mimeJpegFormat;

        /// <summary>
        /// Reads the clipboard and queues whatever image it holds, or forwards copied files to the
        /// file-drop bridge, or queues its text when it holds neither. Public so a UI paste button can
        /// bypass the chord entirely.
        /// </summary>
        public static void Capture()
        {
            if (CaptureImageOrFiles()) return;
            CaptureText();
        }

        private static bool CaptureImageOrFiles()
        {
            if (!TryOpenClipboard(IntPtr.Zero))
            {
                BasisDebug.LogWarning("Clipboard paste: another application is holding the clipboard open.", LogTag);
                return true;
            }

            try
            {
                RegisterFormats();

                // Order matters. GIF first because it is the only format that survives as an animation;
                // a browser that offers both GIF and PNG has already flattened the PNG to one frame.
                // Then the lossless file formats, and only then the raw bitmap every source can produce.
                if (TryQueue(_gifFormat, BasisClipboardImageFormat.Gif)) return true;
                if (TryQueue(_mimeGifFormat, BasisClipboardImageFormat.Gif)) return true;
                if (TryQueue(_pngFormat, BasisClipboardImageFormat.Png)) return true;
                if (TryQueue(_mimePngFormat, BasisClipboardImageFormat.Png)) return true;
                if (TryQueue(_jpegFormat, BasisClipboardImageFormat.Jpeg)) return true;
                if (TryQueue(_mimeJpegFormat, BasisClipboardImageFormat.Jpeg)) return true;

                // CF_DIBV5 before CF_DIB: same pixels, but the V5 header carries an explicit alpha mask
                // instead of leaving the fourth channel's meaning to guesswork.
                if (TryQueue(CF_DIBV5, BasisClipboardImageFormat.Bitmap)) return true;
                if (TryQueue(CF_DIB, BasisClipboardImageFormat.Bitmap)) return true;

                return TryForwardCopiedFiles();
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"Clipboard paste: reading the clipboard failed ({exception.Message}).", LogTag);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// The clipboard is a single global lock that any process can be holding for a moment, so a
        /// first failure means "busy", not "unavailable". Retried a few times before giving up.
        /// </summary>
        private static bool TryOpenClipboard(IntPtr owner)
        {
            for (int attempt = 0; attempt < OpenClipboardAttempts; attempt++)
            {
                if (OpenClipboard(owner)) return true;
            }
            return false;
        }

        public static bool CanCopyImages => true;

        public static bool CopyImage(IReadOnlyList<BasisClipboardImage> formats)
        {
            if (formats == null || formats.Count == 0) return false;
            if (!TryOpenClipboard(GetActiveWindow()))
            {
                BasisDebug.LogWarning("Clipboard copy: another application is holding the clipboard open.", LogTag);
                return false;
            }

            try
            {
                if (!EmptyClipboard()) return false;
                RegisterFormats();
                int written = 0;
                for (int i = 0; i < formats.Count; i++)
                {
                    BasisClipboardImage image = formats[i];
                    if (image == null || image.Data == null || image.Data.Length == 0) continue;
                    uint format = image.Format switch
                    {
                        BasisClipboardImageFormat.Png => _pngFormat,
                        BasisClipboardImageFormat.Gif => _gifFormat,
                        BasisClipboardImageFormat.Bitmap => CF_DIB,
                        _ => 0,
                    };
                    if (format != 0 && WriteGlobal(format, image.Data)) written++;
                }
                return written > 0;
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"Clipboard copy: writing the clipboard failed ({exception.Message}).", LogTag);
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool WriteGlobal(uint format, byte[] data)
        {
            IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
            if (handle == IntPtr.Zero) return false;
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            try
            {
                Marshal.Copy(data, 0, pointer, data.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }
            if (SetClipboardData(format, handle) != IntPtr.Zero) return true;
            GlobalFree(handle);
            return false;
        }

        private static void RegisterFormats()
        {
            if (_formatsRegistered) return;
            _formatsRegistered = true;

            // Names registered by the applications people actually copy images out of: "PNG" and "GIF"
            // are the long-standing Windows convention (browsers, paint programs), while the MIME
            // spellings come from ports of applications that grew up on X11 or macOS.
            _pngFormat = RegisterClipboardFormat("PNG");
            _gifFormat = RegisterClipboardFormat("GIF");
            _jpegFormat = RegisterClipboardFormat("JFIF");
            _mimePngFormat = RegisterClipboardFormat("image/png");
            _mimeGifFormat = RegisterClipboardFormat("image/gif");
            _mimeJpegFormat = RegisterClipboardFormat("image/jpeg");
        }

        private static bool TryQueue(uint format, BasisClipboardImageFormat kind)
        {
            if (format == 0 || !IsClipboardFormatAvailable(format)) return false;

            byte[] data = ReadGlobal(format, out string error);
            if (data == null)
            {
                if (error != null)
                {
                    BasisDebug.LogWarning($"Clipboard paste: {kind} data could not be read ({error}).", LogTag);
                }
                return false;
            }

            SubmitImage(new BasisClipboardImage { Format = kind, Data = data });
            return true;
        }

        /// <summary>
        /// Copies one clipboard format out of the shared memory block that owns it. The handle belongs
        /// to the clipboard, so it is locked for the copy and never freed here.
        /// </summary>
        private static byte[] ReadGlobal(uint format, out string error)
        {
            error = null;

            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;

            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0) return null;
            if (size > MaxClipboardImageBytes)
            {
                error = $"{size:N0} bytes exceeds the {MaxClipboardImageBytes:N0} byte clipboard limit";
                return null;
            }

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                error = "the clipboard memory block could not be locked";
                return null;
            }

            try
            {
                byte[] data = new byte[size];
                Marshal.Copy(pointer, data, 0, data.Length);
                return data;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        /// <summary>
        /// Handles pasting files copied in Explorer. The clipboard's CF_HDROP is queried exactly like a
        /// dragged one, but it is not ours to release — DragFinish here would free memory the clipboard
        /// still owns — so the handle is only read.
        /// </summary>
        private static bool TryForwardCopiedFiles()
        {
            if (!IsClipboardFormatAvailable(CF_HDROP)) return false;

            IntPtr hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero) return true;

            var paths = new List<string>();
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint pathLength = DragQueryFile(hDrop, i, null, 0);
                if (pathLength == 0 || pathLength >= int.MaxValue) continue;

                var builder = new StringBuilder(checked((int)pathLength + 1));
                if (DragQueryFile(hDrop, i, builder, (uint)builder.Capacity) == 0) continue;

                string path = builder.ToString();
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }

            if (paths.Count == 0) return true;
            BasisDesktopFileDrop.SubmitDroppedFiles(paths.ToArray());
            return true;
        }
#endif
    }
}
