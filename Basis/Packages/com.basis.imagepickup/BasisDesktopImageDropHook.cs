#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Windows-only OS file-drop bridge. Subclasses the player window to intercept WM_DROPFILES and forwards
    /// dropped .png paths to the image pickup manager on the Unity main thread. The native callbacks are static
    /// (IL2CPP cannot marshal instance-method delegates) and reach instance state through <see cref="_instance"/>.
    /// </summary>
    public class BasisDesktopImageDropHook : MonoBehaviour
    {
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

        private static BasisDesktopImageDropHook _instance;
        private static readonly WndProcDelegate _wndProcDelegate = HookedWndProc;
        private static IntPtr _foundWindow;
        private static IntPtr _activePreviousWndProc = IntPtr.Zero;
        private static IntPtr _activeWindowHandle = IntPtr.Zero;

        private IntPtr _windowHandle = IntPtr.Zero;
        private IntPtr _previousWndProc = IntPtr.Zero;
        private bool _hookInstalled;

        private readonly List<string> _pending = new();
        private readonly object _pendingLock = new();

        private void OnEnable()
        {
            _instance = this;
            _windowHandle = FindPlayerWindow();
            if (_windowHandle == IntPtr.Zero)
            {
                BasisDebug.LogWarning("Image pickup: could not find the player window; file drop disabled.");
                enabled = false;
                return;
            }

            SetLastError(0);
            _previousWndProc = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            int error = Marshal.GetLastWin32Error();
            if (_previousWndProc == IntPtr.Zero && error != 0)
            {
                BasisDebug.LogWarning($"Image pickup: failed to subclass the player window ({error}); file drop disabled.");
                _windowHandle = IntPtr.Zero;
                if (_instance == this) _instance = null;
                enabled = false;
                return;
            }

            _hookInstalled = true;
            _activeWindowHandle = _windowHandle;
            _activePreviousWndProc = _previousWndProc;
            DragAcceptFiles(_windowHandle, true);
        }

        private void OnDisable()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                if (_hookInstalled)
                {
                    SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _previousWndProc);
                }
                DragAcceptFiles(_windowHandle, false);
            }
            if (_activeWindowHandle == _windowHandle)
            {
                _activeWindowHandle = IntPtr.Zero;
                _activePreviousWndProc = IntPtr.Zero;
            }
            _windowHandle = IntPtr.Zero;
            _previousWndProc = IntPtr.Zero;
            _hookInstalled = false;
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (BasisImagePickupManager.Instance == null) return;

            lock (_pendingLock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    BasisImagePickupManager.Instance.SpawnFromFile(_pending[i]);
                }
                _pending.Clear();
            }
        }

        [AOT.MonoPInvokeCallback(typeof(WndProcDelegate))]
        private static IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            BasisDesktopImageDropHook instance = _instance;
            IntPtr previous = instance != null ? instance._previousWndProc : _activePreviousWndProc;

            if (instance != null && msg == WM_DROPFILES)
            {
                try
                {
                    instance.CollectDroppedFiles(wParam);
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Image pickup: file drop handling failed ({e.Message}).");
                }
            }
            return ForwardWindowMessage(previous, hWnd, msg, wParam, lParam);
        }

        private static IntPtr ForwardWindowMessage(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return previous != IntPtr.Zero
                ? CallWindowProc(previous, hWnd, msg, wParam, lParam)
                : DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void CollectDroppedFiles(IntPtr hDrop)
        {
            try
            {
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var builder = new StringBuilder(1024);
                    DragQueryFile(hDrop, i, builder, (uint)builder.Capacity);
                    string path = builder.ToString();
                    if (BasisImageSecurity.HasPngExtension(path))
                    {
                        lock (_pendingLock) _pending.Add(path);
                    }
                }
            }
            finally
            {
                DragFinish(hDrop);
            }
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
