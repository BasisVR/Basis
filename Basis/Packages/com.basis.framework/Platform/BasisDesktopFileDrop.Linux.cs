#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Platform
{
    public static partial class BasisDesktopFileDrop
    {
        private const string X11 = "libX11.so.6";
        private const int ClientMessage = 33, SelectionNotify = 31, XEventSize = 192;
        private const ulong XA_ATOM = 4, XA_WINDOW = 33, AnyPropertyType = 0;

        [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(X11)] private static extern ulong XDefaultRootWindow(IntPtr display);
        [DllImport(X11)] private static extern ulong XInternAtom(IntPtr display, string name, bool onlyIfExists);
        [DllImport(X11)] private static extern ulong XCreateSimpleWindow(IntPtr display, ulong parent, int x, int y, uint width, uint height, uint border, ulong borderColor, ulong background);
        [DllImport(X11)] private static extern int XDestroyWindow(IntPtr display, ulong window);
        [DllImport(X11)] private static extern int XChangeProperty(IntPtr display, ulong window, ulong property, ulong type, int format, int mode, long[] data, int count);
        [DllImport(X11)] private static extern int XDeleteProperty(IntPtr display, ulong window, ulong property);
        [DllImport(X11)] private static extern int XGetWindowProperty(IntPtr display, ulong window, ulong property, long offset, long length, bool delete, ulong type, out ulong actualType, out int actualFormat, out ulong count, out ulong bytesAfter, out IntPtr data);
        [DllImport(X11)] private static extern int XFree(IntPtr data);
        [DllImport(X11)] private static extern int XFetchName(IntPtr display, ulong window, out IntPtr name);
        [DllImport(X11)] private static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint count);
        [DllImport(X11)] private static extern int XPending(IntPtr display);
        [DllImport(X11)] private static extern int XNextEvent(IntPtr display, IntPtr ev);
        [DllImport(X11)] private static extern int XSendEvent(IntPtr display, ulong window, bool propagate, long mask, IntPtr ev);
        [DllImport(X11)] private static extern int XConvertSelection(IntPtr display, ulong selection, ulong target, ulong property, ulong requestor, ulong time);
        [DllImport(X11)] private static extern int XFlush(IntPtr display);

        [NoAutoStaticsCleanup] private static Thread _x11Thread;
        [NoAutoStaticsCleanup] private static volatile bool _x11Running;

        private struct XdndState
        {
            public IntPtr Display;
            public ulong Target, Proxy, Source, Aware, ProxyAtom, Enter, Position, Status, Leave, Drop, Finished, Selection, UriList, ActionCopy, Pid, ClientList;
            public bool Accept;
            public int Version;
        }

        private static void InstallPlatform()
        {
            if (_x11Thread != null) return;
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string title = Application.productName;
            _x11Running = true;
            _x11Thread = new Thread(() => RunXdnd(pid, title)) { IsBackground = true, Name = "Basis X11 file drop" };
            _x11Thread.Start();
            Application.quitting += Uninstall;
        }

        private static void UninstallPlatform()
        {
            Application.quitting -= Uninstall;
            _x11Running = false;
            _x11Thread?.Join(500);
            _x11Thread = null;
        }

        private static void RunXdnd(int pid, string title)
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                BasisDebug.LogWarning("Desktop file drop: no X11 display; file drop disabled.", LogTag);
                return;
            }
            IntPtr ev = Marshal.AllocHGlobal(XEventSize);
            XdndState s = default;
            try
            {
                s.Display = display;
                s.Aware = XInternAtom(display, "XdndAware", false);
                s.ProxyAtom = XInternAtom(display, "XdndProxy", false);
                s.Enter = XInternAtom(display, "XdndEnter", false);
                s.Position = XInternAtom(display, "XdndPosition", false);
                s.Status = XInternAtom(display, "XdndStatus", false);
                s.Leave = XInternAtom(display, "XdndLeave", false);
                s.Drop = XInternAtom(display, "XdndDrop", false);
                s.Finished = XInternAtom(display, "XdndFinished", false);
                s.Selection = XInternAtom(display, "XdndSelection", false);
                s.UriList = XInternAtom(display, "text/uri-list", false);
                s.ActionCopy = XInternAtom(display, "XdndActionCopy", false);
                s.Pid = XInternAtom(display, "_NET_WM_PID", false);
                s.ClientList = XInternAtom(display, "_NET_CLIENT_LIST", false);
                ulong root = XDefaultRootWindow(display);
                for (int attempt = 0; attempt < 50 && _x11Running && s.Target == 0; attempt++)
                {
                    s.Target = FindX11Window(ref s, root, pid, title);
                    if (s.Target == 0) Thread.Sleep(100);
                }
                if (s.Target == 0)
                {
                    BasisDebug.LogWarning("Desktop file drop: could not find the X11 player window; file drop disabled.", LogTag);
                    return;
                }
                s.Proxy = XCreateSimpleWindow(display, root, -10, -10, 1, 1, 0, 0, 0);
                long[] version = { 5 };
                XChangeProperty(display, s.Proxy, s.Aware, XA_ATOM, 32, 0, version, 1);
                XChangeProperty(display, s.Proxy, s.ProxyAtom, XA_WINDOW, 32, 0, new[] { (long)s.Proxy }, 1);
                XChangeProperty(display, s.Target, s.Aware, XA_ATOM, 32, 0, version, 1);
                XChangeProperty(display, s.Target, s.ProxyAtom, XA_WINDOW, 32, 0, new[] { (long)s.Proxy }, 1);
                XFlush(display);
                while (_x11Running)
                {
                    if (XPending(display) == 0)
                    {
                        Thread.Sleep(15);
                        continue;
                    }
                    XNextEvent(display, ev);
                    try
                    {
                        HandleX11Event(ref s, ev);
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError($"Desktop file drop: X11 drop failed ({exception.Message}).", LogTag);
                        s.Source = 0;
                    }
                }
            }
            finally
            {
                if (s.Target != 0)
                {
                    XDeleteProperty(display, s.Target, s.ProxyAtom);
                    XDeleteProperty(display, s.Target, s.Aware);
                }
                if (s.Proxy != 0) XDestroyWindow(display, s.Proxy);
                Marshal.FreeHGlobal(ev);
                XCloseDisplay(display);
            }
        }

        private static ulong FindX11Window(ref XdndState s, ulong root, int pid, string title)
        {
            var candidates = new List<ulong>();
            long[] clients = ReadLongs(s.Display, root, s.ClientList);
            if (clients != null) foreach (long c in clients) candidates.Add((ulong)c);
            if (candidates.Count == 0 && XQueryTree(s.Display, root, out _, out _, out IntPtr children, out uint count) != 0 && children != IntPtr.Zero)
            {
                for (int i = 0; i < count; i++) candidates.Add((ulong)Marshal.ReadInt64(children, i * 8));
                XFree(children);
            }
            ulong byTitle = 0;
            foreach (ulong window in candidates)
            {
                long[] owner = ReadLongs(s.Display, window, s.Pid);
                if (owner != null && owner.Length > 0 && owner[0] == pid) return window;
                if (byTitle == 0 && !string.IsNullOrEmpty(title) && XFetchName(s.Display, window, out IntPtr name) != 0 && name != IntPtr.Zero)
                {
                    if (Marshal.PtrToStringAnsi(name) == title) byTitle = window;
                    XFree(name);
                }
            }
            return byTitle;
        }

        private static long[] ReadLongs(IntPtr display, ulong window, ulong property)
        {
            if (XGetWindowProperty(display, window, property, 0, 4096, false, AnyPropertyType, out _, out int format, out ulong count, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return null;
            try
            {
                if (format != 32) return null;
                var values = new long[count];
                for (int i = 0; i < (int)count; i++) values[i] = Marshal.ReadInt64(data, i * 8);
                return values;
            }
            finally
            {
                XFree(data);
            }
        }

        private static long EventLong(IntPtr ev, int index) => Marshal.ReadInt64(ev, 56 + index * 8);

        private static void HandleX11Event(ref XdndState s, IntPtr ev)
        {
            int type = Marshal.ReadInt32(ev, 0);
            if (type == SelectionNotify)
            {
                if (s.Source == 0 || (ulong)Marshal.ReadInt64(ev, 32) != s.Proxy) return;
                ulong property = (ulong)Marshal.ReadInt64(ev, 56);
                bool ok = property != 0 && CollectUriList(ref s, property);
                SendXdnd(ref s, s.Finished, (long)s.Target, ok ? 1 : 0, ok ? (long)s.ActionCopy : 0, 0, 0);
                s.Source = 0;
                return;
            }
            if (type != ClientMessage) return;
            ulong message = (ulong)Marshal.ReadInt64(ev, 40);
            if (message == s.Enter)
            {
                s.Source = (ulong)EventLong(ev, 0);
                long flags = EventLong(ev, 1);
                s.Version = (int)((ulong)flags >> 24);
                s.Accept = false;
                if ((flags & 1) != 0)
                {
                    long[] types = ReadLongs(s.Display, s.Source, XInternAtom(s.Display, "XdndTypeList", false));
                    if (types != null) foreach (long t in types) s.Accept |= (ulong)t == s.UriList;
                }
                else
                {
                    for (int i = 2; i < 5; i++) s.Accept |= (ulong)EventLong(ev, i) == s.UriList;
                }
            }
            else if (message == s.Position)
            {
                if ((ulong)EventLong(ev, 0) != s.Source) return;
                SendXdnd(ref s, s.Status, (long)s.Target, s.Accept ? 3 : 2, 0, 0, s.Accept ? (long)s.ActionCopy : 0);
            }
            else if (message == s.Leave)
            {
                s.Source = 0;
            }
            else if (message == s.Drop)
            {
                if ((ulong)EventLong(ev, 0) != s.Source) return;
                if (!s.Accept)
                {
                    SendXdnd(ref s, s.Finished, (long)s.Target, 0, 0, 0, 0);
                    s.Source = 0;
                    return;
                }
                ulong time = s.Version >= 1 ? (ulong)EventLong(ev, 2) : 0;
                XConvertSelection(s.Display, s.Selection, s.UriList, s.Selection, s.Proxy, time);
                XFlush(s.Display);
            }
        }

        private static bool CollectUriList(ref XdndState s, ulong property)
        {
            if (XGetWindowProperty(s.Display, s.Proxy, property, 0, 1 << 20, true, AnyPropertyType, out _, out int format, out ulong count, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return false;
            string text;
            try
            {
                if (format != 8 || count == 0) return false;
                var bytes = new byte[count];
                Marshal.Copy(data, bytes, 0, (int)count);
                text = Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                XFree(data);
            }
            var paths = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim('\r', '\0', ' ');
                if (line.Length == 0 || line[0] == '#' || !line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
                int slash = line.IndexOf('/', 7);
                if (slash < 0) continue;
                string path = Uri.UnescapeDataString(line.Substring(slash));
                if (path.Length > 0) paths.Add(path);
            }
            if (paths.Count == 0) return false;
            _pendingBatches.Enqueue(paths.ToArray());
            return true;
        }

        private static void SendXdnd(ref XdndState s, ulong message, long l0, long l1, long l2, long l3, long l4)
        {
            IntPtr reply = Marshal.AllocHGlobal(XEventSize);
            try
            {
                for (int i = 0; i < XEventSize; i += 8) Marshal.WriteInt64(reply, i, 0);
                Marshal.WriteInt32(reply, 0, ClientMessage);
                Marshal.WriteInt64(reply, 24, (long)s.Display);
                Marshal.WriteInt64(reply, 32, (long)s.Source);
                Marshal.WriteInt64(reply, 40, (long)message);
                Marshal.WriteInt32(reply, 48, 32);
                Marshal.WriteInt64(reply, 56, l0);
                Marshal.WriteInt64(reply, 64, l1);
                Marshal.WriteInt64(reply, 72, l2);
                Marshal.WriteInt64(reply, 80, l3);
                Marshal.WriteInt64(reply, 88, l4);
                XSendEvent(s.Display, s.Source, false, 0, reply);
                XFlush(s.Display);
            }
            finally
            {
                Marshal.FreeHGlobal(reply);
            }
        }
    }
}
#endif
