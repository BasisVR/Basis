using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class BasisGlobalIlluminationHistory
{
    private static readonly Dictionary<int, BasisGlobalIlluminationHistory> stores = new Dictionary<int, BasisGlobalIlluminationHistory>();
    private static readonly List<int> pruneScratch = new List<int>();
    public static IReadOnlyDictionary<int, BasisGlobalIlluminationHistory> Stores => stores;

    public RTHandle[] Indirect = new RTHandle[2];
    public RTHandle[] Stats = new RTHandle[2];
    public int Write;
    public bool Valid;
    public int Width, Height;
    public Matrix4x4[] PreviousViewProjection = new Matrix4x4[2] { Matrix4x4.identity, Matrix4x4.identity };
    public int LastFrame = -1;

    public int Read => 1 - Write;

    public static int ComputeHash(Camera camera, XRPass xr)
    {
        int hash = camera.GetHashCode();
        if (xr != null && xr.enabled && !xr.singlePassEnabled) { hash = unchecked(hash * 397) ^ (xr.multipassId + 1); }
        return hash;
    }

    public static BasisGlobalIlluminationHistory Get(int hash)
    {
        if (!stores.TryGetValue(hash, out BasisGlobalIlluminationHistory store))
        {
            store = new BasisGlobalIlluminationHistory();
            stores.Add(hash, store);
        }
        return store;
    }

    public static void PruneStale(int frame, int maxAge)
    {
        pruneScratch.Clear();
        foreach (KeyValuePair<int, BasisGlobalIlluminationHistory> entry in stores)
        {
            if (entry.Value.LastFrame >= 0 && frame - entry.Value.LastFrame > maxAge) { pruneScratch.Add(entry.Key); }
        }
        for (int index = 0; index < pruneScratch.Count; index++)
        {
            stores[pruneScratch[index]].Release();
            stores.Remove(pruneScratch[index]);
        }
        pruneScratch.Clear();
    }

    public static void ReleaseAll()
    {
        foreach (KeyValuePair<int, BasisGlobalIlluminationHistory> entry in stores) { entry.Value.Release(); }
        stores.Clear();
    }

    public bool EnsureAllocated(in RenderTextureDescriptor cameraDescriptor, int width, int height)
    {
        bool reallocated = width != Width || height != Height;
        Width = width;
        Height = height;

        RenderTextureDescriptor indirectDescriptor = cameraDescriptor;
        indirectDescriptor.width = width;
        indirectDescriptor.height = height;
        indirectDescriptor.msaaSamples = 1;
        indirectDescriptor.depthStencilFormat = GraphicsFormat.None;
        indirectDescriptor.depthBufferBits = 0;
        indirectDescriptor.useMipMap = false;
        indirectDescriptor.autoGenerateMips = false;
        indirectDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

        // Depth in red, frames accumulated in green, and the running mean and variance of the
        // accumulated luminance in blue and alpha. The temporal filter needs the first two to decide how
        // much of this frame to let in; the spatial filter needs the variance, because it is what tells
        // it whether a pixel has settled enough to be left alone or is still so sparse that it has to
        // take its neighbours' word for what it is.
        // Half float throughout, which keeps this target the same size it was when it only held depth
        // and a frame count. Depth is compared as a relative difference against a rejection threshold
        // whose smallest setting is ten times half's relative precision, and the moments only ever have
        // to answer whether a pixel has settled.
        RenderTextureDescriptor statsDescriptor = indirectDescriptor;
        statsDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

        for (int slot = 0; slot < 2; slot++)
        {
            reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref Indirect[slot], in indirectDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BasisGIHistoryIndirect" + slot);
            reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref Stats[slot], in statsDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_BasisGIHistoryStats" + slot);
        }

        if (reallocated) { Valid = false; }
        return reallocated;
    }

    public void Release()
    {
        for (int slot = 0; slot < 2; slot++)
        {
            Indirect[slot]?.Release();
            Indirect[slot] = null;
            Stats[slot]?.Release();
            Stats[slot] = null;
        }
        Width = Height = 0;
        Valid = false;
    }
}
