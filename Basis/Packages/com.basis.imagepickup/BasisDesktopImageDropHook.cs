#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Windows-only OS file-drop bridge. Subclasses the player window to intercept WM_DROPFILES and forwards
    /// dropped file paths to the image pickup manager on the Unity main thread. The manager validates supported
    /// image formats. The native callbacks are static (IL2CPP cannot marshal instance-method delegates).
    /// </summary>
    public static class BasisDesktopImageDropHook
    {
        private const int GWLP_WNDPROC = -4;
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;
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

        private static readonly WndProcDelegate _wndProcDelegate = HookedWndProc;
        private static readonly ConcurrentQueue<string[]> _pendingBatches = new();
        private static IntPtr _foundWindow;
        private static IntPtr _windowHandle = IntPtr.Zero;
        private static IntPtr _previousWndProc = IntPtr.Zero;
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;

            _windowHandle = FindPlayerWindow();
            if (_windowHandle == IntPtr.Zero)
            {
                BasisDebug.LogWarning("Image pickup: could not find the player window; file drop disabled.", LogTag);
                return;
            }

            SetLastError(0);
            _previousWndProc = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            int error = Marshal.GetLastWin32Error();
            if (_previousWndProc == IntPtr.Zero && error != 0)
            {
                BasisDebug.LogWarning($"Image pickup: failed to subclass the player window ({error}); file drop disabled.", LogTag);
                _windowHandle = IntPtr.Zero;
                return;
            }

            _installed = true;
            DragAcceptFiles(_windowHandle, true);
            BasisEventDriver.OnUpdate += Simulate;
            Application.quitting += Uninstall;
        }

        public static void Uninstall()
        {
            if (!_installed)
                return;
            _installed = false;

            BasisEventDriver.OnUpdate -= Simulate;
            Application.quitting -= Uninstall;

            if (_windowHandle != IntPtr.Zero)
            {
                SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _previousWndProc);
                DragAcceptFiles(_windowHandle, false);
            }
            _windowHandle = IntPtr.Zero;
            _previousWndProc = IntPtr.Zero;
            while (_pendingBatches.TryDequeue(out _)) { }
        }

        private static void Simulate()
        {
            try
            {
                SimulateBody();
            }
            catch (Exception exception)
            {
                BasisDebug.LogErrorOnce($"Image pickup file-drop simulation failed: {exception}", LogTag);
            }
        }

        private static void SimulateBody()
        {
            int batchCount = _pendingBatches.Count;
            for (int i = 0; i < batchCount; i++)
            {
                if (!_pendingBatches.TryDequeue(out string[] paths)) break;
                try
                {
                    BasisImagePickupManager.SpawnFromFiles(paths);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"Image pickup: dropped-file batch {i + 1:N0}/{batchCount:N0} failed ({exception.Message}).", LogTag);
                }
            }
        }

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
                    BasisDebug.LogError($"Image pickup: failed to collect dropped files ({exception.Message}).", LogTag);
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
    }
}
#endif
