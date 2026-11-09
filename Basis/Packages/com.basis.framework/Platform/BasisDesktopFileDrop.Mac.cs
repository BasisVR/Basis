#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Platform
{
    public static partial class BasisDesktopFileDrop
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const ulong NSDragOperationCopy = 1;

        private delegate ulong DragOperationImp(IntPtr self, IntPtr cmd, IntPtr sender);
        private delegate byte DragBoolImp(IntPtr self, IntPtr cmd, IntPtr sender);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr instance);
        [DllImport(ObjC)] private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr sel, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIndex(IntPtr receiver, IntPtr sel, ulong index);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong MsgCount(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte MsgBool(IntPtr receiver, IntPtr sel);

        [NoAutoStaticsCleanup] private static readonly DragOperationImp _draggingEntered = DraggingEntered;
        [NoAutoStaticsCleanup] private static readonly DragBoolImp _prepareForDrag = PrepareForDragOperation;
        [NoAutoStaticsCleanup] private static readonly DragBoolImp _performDrag = PerformDragOperation;
        [NoAutoStaticsCleanup] private static IntPtr _dropView;

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSString(string value)
        {
            IntPtr utf8 = Marshal.StringToHGlobalAnsi(value);
            try
            {
                return Msg(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8);
            }
            finally
            {
                Marshal.FreeHGlobal(utf8);
            }
        }

        private static void InstallPlatform()
        {
            if (_dropView != IntPtr.Zero) return;
            IntPtr app = Msg(objc_getClass("NSApplication"), Sel("sharedApplication"));
            IntPtr window = app == IntPtr.Zero ? IntPtr.Zero : Msg(app, Sel("mainWindow"));
            if (window == IntPtr.Zero && app != IntPtr.Zero) window = Msg(app, Sel("keyWindow"));
            if (window == IntPtr.Zero && app != IntPtr.Zero) window = Msg(Msg(app, Sel("windows")), Sel("firstObject"));
            IntPtr view = window == IntPtr.Zero ? IntPtr.Zero : Msg(window, Sel("contentView"));
            if (view == IntPtr.Zero)
            {
                BasisDebug.LogWarning("Desktop file drop: could not find the player view; file drop disabled.", LogTag);
                return;
            }
            IntPtr cls = object_getClass(view);
            class_replaceMethod(cls, Sel("draggingEntered:"), Marshal.GetFunctionPointerForDelegate(_draggingEntered), "Q@:@");
            class_replaceMethod(cls, Sel("draggingUpdated:"), Marshal.GetFunctionPointerForDelegate(_draggingEntered), "Q@:@");
            class_replaceMethod(cls, Sel("prepareForDragOperation:"), Marshal.GetFunctionPointerForDelegate(_prepareForDrag), "c@:@");
            class_replaceMethod(cls, Sel("performDragOperation:"), Marshal.GetFunctionPointerForDelegate(_performDrag), "c@:@");
            Msg(view, Sel("registerForDraggedTypes:"), Msg(objc_getClass("NSArray"), Sel("arrayWithObject:"), NSString("public.file-url")));
            _dropView = view;
            Application.quitting += Uninstall;
        }

        private static void UninstallPlatform()
        {
            Application.quitting -= Uninstall;
            if (_dropView != IntPtr.Zero) Msg(_dropView, Sel("unregisterDraggedTypes"));
            _dropView = IntPtr.Zero;
        }

        [AOT.MonoPInvokeCallback(typeof(DragOperationImp))]
        private static ulong DraggingEntered(IntPtr self, IntPtr cmd, IntPtr sender) => NSDragOperationCopy;

        [AOT.MonoPInvokeCallback(typeof(DragBoolImp))]
        private static byte PrepareForDragOperation(IntPtr self, IntPtr cmd, IntPtr sender) => 1;

        [AOT.MonoPInvokeCallback(typeof(DragBoolImp))]
        private static byte PerformDragOperation(IntPtr self, IntPtr cmd, IntPtr sender)
        {
            try
            {
                IntPtr pasteboard = Msg(sender, Sel("draggingPasteboard"));
                IntPtr classes = Msg(objc_getClass("NSArray"), Sel("arrayWithObject:"), objc_getClass("NSURL"));
                IntPtr urls = Msg(pasteboard, Sel("readObjectsForClasses:options:"), classes, IntPtr.Zero);
                if (urls == IntPtr.Zero) return 0;
                var paths = new List<string>();
                ulong count = MsgCount(urls, Sel("count"));
                for (ulong i = 0; i < count; i++)
                {
                    IntPtr url = MsgIndex(urls, Sel("objectAtIndex:"), i);
                    if (url == IntPtr.Zero || MsgBool(url, Sel("isFileURL")) == 0) continue;
                    IntPtr path = Msg(url, Sel("path"));
                    string value = path == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Msg(path, Sel("UTF8String")));
                    if (!string.IsNullOrEmpty(value)) paths.Add(value);
                }
                if (paths.Count == 0) return 0;
                _pendingBatches.Enqueue(paths.ToArray());
                return 1;
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"Desktop file drop: failed to collect dropped files ({exception.Message}).", LogTag);
                return 0;
            }
        }
    }
}
#endif
