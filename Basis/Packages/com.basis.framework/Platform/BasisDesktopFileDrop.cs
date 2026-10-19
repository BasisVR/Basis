using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace Basis.Scripts.Platform
{
    /// <summary>
    /// The client's single OS file-drop bridge. On Windows players it subclasses the player window
    /// to intercept WM_DROPFILES; every dropped batch is queued and replayed from
    /// <see cref="Dispatch"/> on the main thread, where <see cref="OnFilesDropped"/> hands the raw
    /// paths to whoever wants them. Subscribers filter by extension themselves — images go to the
    /// image pickup manager, BEE files go to the library.
    ///
    /// There can only ever be one window subclass: the WndProc consumes the HDROP with DragFinish,
    /// so a second hook chained behind it would either see nothing or query freed native memory.
    /// That is why this lives in the framework rather than in whichever package wanted drops first.
    ///
    /// The native callbacks are static because IL2CPP cannot marshal instance-method delegates.
    /// Off Windows (and in the editor, where the player window hook cannot run) the install is a
    /// no-op, but the queue still works — the editor scene-view bridge feeds it through
    /// <see cref="SubmitDroppedFiles"/> so play-mode drops behave the same as shipped ones.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisDesktopFileDrop
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.System;

        /// <summary>
        /// Raised on the main thread once per dropped batch with the absolute paths the OS reported.
        /// Paths are not filtered or validated — a subscriber that only handles one file type must
        /// check for itself, and must not assume the batch is non-empty of anything but paths.
        /// </summary>
        public static event Action<string[]> OnFilesDropped;

        private static readonly ConcurrentQueue<string[]> _pendingBatches = new();
        private static readonly HashSet<string> _acceptedExtensions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Queue a batch as though the OS had dropped it. Used by the editor scene-view bridge, and
        /// safe to call from any thread — the batch is replayed from <see cref="Dispatch"/>.
        /// </summary>
        public static void SubmitDroppedFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            _pendingBatches.Enqueue(paths);
        }

        /// <summary>
        /// Declares an extension (".png", ".BEE") that some subscriber acts on. The Windows player
        /// takes every drop regardless — the OS has already decided the window is the target — so
        /// this exists purely for the editor bridge, which shares the scene view with Unity's own
        /// asset drags and must not consume one it has no use for.
        /// </summary>
        public static void RegisterAcceptedExtensions(params string[] extensions)
        {
            if (extensions == null) return;
            for (int i = 0; i < extensions.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(extensions[i])) continue;
                _acceptedExtensions.Add(extensions[i].Trim());
            }
        }

        /// <summary>True when some subscriber has claimed this file's extension.</summary>
        public static bool IsAcceptedFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string extension = Path.GetExtension(path);
                return !string.IsNullOrEmpty(extension) && _acceptedExtensions.Contains(extension);
            }
            catch (ArgumentException)
            {
                // Path.GetExtension rejects paths with invalid characters; a drag can carry one.
                return false;
            }
        }

        /// <summary>
        /// Drains queued drops and raises <see cref="OnFilesDropped"/> for each. Called once per
        /// frame from BasisEventDriver's central tick; a throwing subscriber is logged and skipped
        /// so it cannot take the rest of the batch — or the frame — down with it.
        ///
        /// Subscribers are invoked one at a time rather than through the multicast delegate: they
        /// are independent consumers of the same drop, and one of them throwing must not stop the
        /// others from seeing files that were theirs.
        /// </summary>
        public static void Dispatch()
        {
            int batchCount = _pendingBatches.Count;
            if (batchCount == 0) return;

            Delegate[] subscribers = OnFilesDropped?.GetInvocationList();

            for (int i = 0; i < batchCount; i++)
            {
                if (!_pendingBatches.TryDequeue(out string[] paths)) break;
                if (subscribers == null) continue;

                for (int s = 0; s < subscribers.Length; s++)
                {
                    try
                    {
                        ((Action<string[]>)subscribers[s]).Invoke(paths);
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError(
                            $"Desktop file drop: {subscribers[s].Method?.DeclaringType?.Name} failed on batch {i + 1:N0}/{batchCount:N0} ({exception.Message}).",
                            LogTag);
                    }
                }
            }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const int GWLP_WNDPROC = -4;
        private const uint WM_DROPFILES = 0x0233;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumThreadWindowProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint dwErrCode);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Auto)] private static extern uint DragQueryFile(IntPtr hDrop, uint file, StringBuilder buffer, uint length);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);

        [NoAutoStaticsCleanup] private static readonly WndProcDelegate _wndProcDelegate = HookedWndProc;
        [NoAutoStaticsCleanup] private static IntPtr _foundWindow;
        [NoAutoStaticsCleanup] private static IntPtr _windowHandle = IntPtr.Zero;
        [NoAutoStaticsCleanup] private static IntPtr _previousWndProc = IntPtr.Zero;
        private static bool _installed;
#endif

        /// <summary>
        /// Arms the window hook. Runs itself at startup so drops work regardless of which optional
        /// packages are installed; calling it again is harmless.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Install()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (_installed)
                return;

            _windowHandle = FindPlayerWindow();
            if (_windowHandle == IntPtr.Zero)
            {
                BasisDebug.LogWarning("Desktop file drop: could not find the player window; file drop disabled.", LogTag);
                return;
            }

            SetLastError(0);
            _previousWndProc = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            int error = Marshal.GetLastWin32Error();
            if (_previousWndProc == IntPtr.Zero && error != 0)
            {
                BasisDebug.LogWarning($"Desktop file drop: failed to subclass the player window ({error}); file drop disabled.", LogTag);
                _windowHandle = IntPtr.Zero;
                return;
            }

            _installed = true;
            DragAcceptFiles(_windowHandle, true);
            Application.quitting += Uninstall;
#endif
        }

        public static void Uninstall()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!_installed)
                return;
            _installed = false;

            Application.quitting -= Uninstall;

            if (_windowHandle != IntPtr.Zero)
            {
                SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _previousWndProc);
                DragAcceptFiles(_windowHandle, false);
            }
            _windowHandle = IntPtr.Zero;
            _previousWndProc = IntPtr.Zero;
#endif
            while (_pendingBatches.TryDequeue(out _)) { }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [AOT.MonoPInvokeCallback(typeof(WndProcDelegate))]
        private static IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr previous = _previousWndProc;

            if (_installed && msg == WM_DROPFILES)
            {
                try
                {
                    CollectDroppedFiles(wParam);
                }
                catch (Exception exception)
                {
                    // Never allow a managed exception to unwind through the native window procedure.
                    BasisDebug.LogError($"Desktop file drop: failed to collect dropped files ({exception.Message}).", LogTag);
                }

                // CollectDroppedFiles owns and releases the HDROP with DragFinish. Forwarding the consumed
                // handle can make the previous WndProc query or release freed native memory.
                return IntPtr.Zero;
            }
            return ForwardWindowMessage(previous, hWnd, msg, wParam, lParam);
        }

        private static IntPtr ForwardWindowMessage(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return previous != IntPtr.Zero
                ? CallWindowProc(previous, hWnd, msg, wParam, lParam)
                : DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void CollectDroppedFiles(IntPtr hDrop)
        {
            var droppedPaths = new List<string>();
            try
            {
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    uint pathLength = DragQueryFile(hDrop, i, null, 0);
                    if (pathLength == 0 || pathLength >= int.MaxValue) continue;

                    var builder = new StringBuilder(checked((int)pathLength + 1));
                    uint copiedLength = DragQueryFile(hDrop, i, builder, (uint)builder.Capacity);
                    if (copiedLength == 0) continue;

                    string path = builder.ToString();
                    if (!string.IsNullOrEmpty(path)) droppedPaths.Add(path);
                }
            }
            finally
            {
                DragFinish(hDrop);
            }

            if (droppedPaths.Count == 0) return;
            _pendingBatches.Enqueue(droppedPaths.ToArray());
        }

        [AOT.MonoPInvokeCallback(typeof(EnumThreadWindowProc))]
        private static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (IsWindowVisible(hWnd))
            {
                _foundWindow = hWnd;
                return false;
            }
            return true;
        }

        private static IntPtr FindPlayerWindow()
        {
            _foundWindow = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), EnumWindowCallback, IntPtr.Zero);
            return _foundWindow;
        }
#endif
    }
}
