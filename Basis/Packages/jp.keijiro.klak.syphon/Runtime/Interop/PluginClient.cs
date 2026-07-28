using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Klak.Syphon.Interop {

public sealed class PluginClient : SafeHandleZeroOrMinusOneIsInvalid
{
    #region SafeHandle implementation

    PluginClient() : base(true) {}

    protected override bool ReleaseHandle()
    {
        _Destroy(handle);
        return true;
    }

    #endregion

    #region Factory method

    public static PluginClient Create(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var split = name.IndexOf('/');
        if (split < 0) return _Create(null, name);
        return _Create(name.Substring(split + 1), name.Substring(0, split));
    }

    public static PluginClient Create(string appName, string name)
      => _Create(name, appName);

    #endregion

    #region Public methods and properties

    public bool IsValid => _IsValid(this);
    public IntPtr TexturePointer => _GetTexture(this);
    public int Width => _GetTextureWidth(this);
    public int Height => _GetTextureHeight(this);
    public void Update() => _Update(this);

    #endregion

    #region Helper methods

    public bool HasSameTexture(Texture2D texture)
      => texture != null && texture.GetNativeTexturePtr() == TexturePointer;

    public Texture2D CreateTexture()
    {
        var pointer = TexturePointer;
        if (pointer == IntPtr.Zero) return null;
        var tex = Texture2D.CreateExternalTexture
           (Width, Height, TextureFormat.RGBA32, false, false, pointer);
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    #endregion

    #region Unmanaged interface

    #if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX

    [DllImport("KlakSyphon", EntryPoint = "Plugin_CreateClient")]
    static extern PluginClient _Create(string name, string appName);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_DestroyClient")]
    static extern void _Destroy(IntPtr instance);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_IsClientValid")]
    static extern bool _IsValid(PluginClient instance);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_GetClientTexture")]
    static extern IntPtr _GetTexture(PluginClient instance);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_GetClientTextureWidth")]
    static extern int _GetTextureWidth(PluginClient instance);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_GetClientTextureHeight")]
    static extern int _GetTextureHeight(PluginClient instance);

    [DllImport("KlakSyphon", EntryPoint = "Plugin_UpdateClient")]
    static extern void _Update(PluginClient instance);

    #else

    static PluginClient _Create(string name, string appName) => null;
    static void _Destroy(IntPtr instance) {}
    static bool _IsValid(PluginClient instance) => false;
    static IntPtr _GetTexture(PluginClient instance) => IntPtr.Zero;
    static int _GetTextureWidth(PluginClient instance) => 0;
    static int _GetTextureHeight(PluginClient instance) => 0;
    static void _Update(PluginClient instance) {}

    #endif

    #endregion
}

} // namespace Klak.Syphon.Interop
