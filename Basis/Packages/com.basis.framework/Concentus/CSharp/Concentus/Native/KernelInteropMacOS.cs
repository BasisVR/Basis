using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Concentus.Native
{
    internal partial class KernelInteropMacOS
    {
        internal const int RTLD_NOW = 2;

#if NET8_0_OR_GREATER
        [LibraryImport("libSystem.dylib", StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
        internal static partial IntPtr dlopen(string fileName, int flags);

        [LibraryImport("libSystem.dylib")]
        internal static partial int dlclose(IntPtr handle);

        [LibraryImport("libSystem.dylib")]
        internal static partial IntPtr dlerror(); // returns static pointer to null terminated CString
#else
        [DllImport("libSystem.dylib")]
        internal static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libSystem.dylib")]
        internal static extern int dlclose(IntPtr handle);

        [DllImport("libSystem.dylib")]
        internal static extern IntPtr dlerror(); // returns static pointer to null terminated CString
#endif
    }
}
