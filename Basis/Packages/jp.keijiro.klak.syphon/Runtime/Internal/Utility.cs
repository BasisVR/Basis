using UnityEngine;
using UnityEngine.Rendering;
using System;

namespace Klak.Syphon {

static class Utility
{
    #region Object lifecycle

    public static void Destroy(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(obj);
        else
            UnityEngine.Object.DestroyImmediate(obj);
    }

    #endregion

    #region Material property

    static MaterialPropertyBlock _sharedPropertyBlock;
    static Texture2D _blackTexture;

    static MaterialPropertyBlock GetSharedPropertyBlock()
    {
        if (_sharedPropertyBlock == null)
            _sharedPropertyBlock = new MaterialPropertyBlock();
        return _sharedPropertyBlock;
    }

    static Texture2D GetBlackTexture()
    {
        if (_blackTexture == null) _blackTexture = new Texture2D(8, 8);
        return _blackTexture;
    }

    public static void SetTexture(Renderer renderer, string name, Texture texture)
    {
        if (renderer == null || string.IsNullOrEmpty(name)) return;
        var sheet = GetSharedPropertyBlock();
        renderer.GetPropertyBlock(sheet);
        sheet.SetTexture(name, texture != null ? texture : GetBlackTexture());
        renderer.SetPropertyBlock(sheet);
    }

    #endregion

    #region Screen capture

    public static RenderTexture CaptureScreenAsTempRT()
    {
        RenderTexture.active = null;
        var rt = RenderTexture.GetTemporary(Screen.width, Screen.height, 0);
        ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
        return rt;
    }

    #endregion
}

// Shared custom blitter
static class Blitter
{
    static Material _material;

    static class ID
    {
        public static int KeepAlpha = Shader.PropertyToID("_KeepAlpha");
        public static int Temp = Shader.PropertyToID("_SyphonTemp");
        public static int VFlip = Shader.PropertyToID("_VFlip");
    }

    public static void Prepare(SyphonResources resources)
    {
        if (_material != null) return;
        _material = new Material(resources.blitShader);
        _material.hideFlags = HideFlags.DontSave;
    }

    public static void Blit
      (Texture source, Texture destination,
       bool keepAlpha = false, bool vflip = false)
    {
        _material.SetFloat(ID.KeepAlpha, keepAlpha ? 1 : 0);
        _material.SetFloat(ID.VFlip, vflip ? 1 : 0);
        var rt = RenderTexture.GetTemporary(destination.width, destination.height, 0);
        Graphics.Blit(source, rt, _material);
        Graphics.CopyTexture(rt, destination);
        RenderTexture.ReleaseTemporary(rt);
    }

    public static void Blit
      (CommandBuffer cb,
       RenderTargetIdentifier source, Texture destination,
       bool keepAlpha = false, bool vflip = false)
    {
        _material.SetFloat(ID.KeepAlpha, keepAlpha ? 1 : 0);
        _material.SetFloat(ID.VFlip, vflip ? 1 : 0);
        cb.GetTemporaryRT(ID.Temp, destination.width, destination.height, 0);
        cb.Blit(source, ID.Temp, _material);
        cb.CopyTexture(ID.Temp, destination);
        cb.ReleaseTemporaryRT(ID.Temp);
    }
}

} // namespace Klak.Syphon
