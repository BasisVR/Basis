using System;
using UnityEngine;

// Uploads RGBA32 frames straight into a Texture2D exposed as OutputTexture.
// Rebuilds the texture if frame dimensions change and raises OnOutputTextureChanged
// so consumers (BasisVideoMaterialOutput, BasisVideoDisplay) can rebind.
public sealed class BasisRgba32FrameRenderer : IBasisFrameRenderer
{
    private Texture2D texture;
    private int currentWidth;
    private int currentHeight;

    public Texture OutputTexture => texture;

    public event Action<Texture> OnOutputTextureChanged;

    public bool SupportsFormat(BasisVideoFrameFormat format) => format == BasisVideoFrameFormat.Rgba32;

    public bool Present(BasisVideoFrame frame)
    {
        if (frame == null || frame.Data == null) return false;
        if (frame.Width <= 0 || frame.Height <= 0) return false;
        // LoadRawTextureData overreads a short buffer, so the decoder's declared size has to agree
        // with the texture we are about to build before any of it reaches native code.
        if ((long)frame.Width * frame.Height * 4 != frame.Data.Length) return false;
        EnsureTexture(frame.Width, frame.Height);

        texture.LoadRawTextureData(frame.Data);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return true;
    }

    public void Dispose()
    {
        if (texture != null)
        {
            DestroyObject(texture);
            texture = null;
            OnOutputTextureChanged?.Invoke(null);
        }
    }

    private void EnsureTexture(int w, int h)
    {
        if (texture != null && currentWidth == w && currentHeight == h) return;
        if (texture != null) DestroyObject(texture);
        texture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "BasisMediaPlayerFrame",
        };
        currentWidth = w;
        currentHeight = h;
        OnOutputTextureChanged?.Invoke(texture);
    }

    private static void DestroyObject(UnityEngine.Object o)
    {
        // Object.Destroy is editor/runtime aware; DestroyImmediate is needed
        // when disposal happens outside Play mode (e.g., domain reload).
        if (Application.isPlaying) UnityEngine.Object.Destroy(o);
        else UnityEngine.Object.DestroyImmediate(o);
    }
}
