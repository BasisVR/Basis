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
    // Reflections accumulate separately from the diffuse bounce and only exist while they are switched on,
    // so a world that never asked for them never pays the two extra targets.
    public RTHandle[] Specular = new RTHandle[2];
    public RTHandle[] SpecularStats = new RTHandle[2];
    public bool SpecularAllocated;
    public bool SpecularValid;
    public int SpecularWrite;
    public Matrix4x4[] PreviousSpecularViewProjection = new Matrix4x4[2] { Matrix4x4.identity, Matrix4x4.identity };
    // Reflections and the diffuse bounce are written by two passes at different points in the frame, and
    // either can be running without the other, so they cannot share a frame stamp or a buffer parity.
    public int LastSpecularFrame = -1;
    public int Write;
    public bool Valid;
    public int Width, Height;
    public Matrix4x4[] PreviousViewProjection = new Matrix4x4[2] { Matrix4x4.identity, Matrix4x4.identity };
    public int LastFrame = -1;

    // How many rendered frames apart this camera's last two renders were. A camera that renders every
    // frame leaves these at one; a rate limited one - the handheld camera capped to 30Hz on a 90Hz
    // headset - leaves them at three, and that is the whole reason they exist. See AllowedGap.
    public int Stride;
    public int SpecularStride;

    public int Read => 1 - Write;
    public int SpecularRead => 1 - SpecularWrite;

    /// <summary>
    /// How many rendered frames may separate two renders of the same camera before the accumulation
    /// between them is thrown away.
    ///
    /// A fixed window of two was the whole gate, which reads as "the camera rendered last frame or the
    /// one before". Every camera that renders EVERY frame satisfies it and no other camera ever can: the
    /// handheld camera limited to 30Hz on a 90Hz headset renders one frame in three, so the gate failed
    /// on every single render and the temporal filter was discarded every time. Its feed stayed a one
    /// sample per pixel trace - visibly noisier than the direct view of the same room beside it - and
    /// nothing named the cause, because a render rate limiter and a denoiser have nothing to do with
    /// each other on paper.
    ///
    /// What the window is really protecting is the reprojection. Camera motion is carried by the stored
    /// view projection and survives any gap; scene motion is only valid for about one step, so the
    /// budget has to be counted in the camera's OWN renders rather than the application's frames. One
    /// stride plus a frame of slack absorbs the limiter's jitter - its accumulator alternates 3,3,4 -
    /// while still failing on a genuinely dropped render. The ceiling is what stops a camera that
    /// stopped for a second and came back from reprojecting through the second: under roughly 6Hz on a
    /// 90Hz display the history resets, by which point the feed is a slideshow anyway.
    /// </summary>
    public const int MaxGap = 16;

    public static int AllowedGap(int stride)
    {
        return Mathf.Clamp(stride + 1, 2, MaxGap);
    }

    public bool Contiguous(int frame)
    {
        return LastFrame >= 0 && frame - LastFrame <= AllowedGap(Stride);
    }

    public bool SpecularContiguous(int frame)
    {
        return LastSpecularFrame >= 0 && frame - LastSpecularFrame <= AllowedGap(SpecularStride);
    }

    /// <summary>
    /// Stamps this render and records how far it was from the one before it.
    ///
    /// A SECOND render of the same camera inside one frame is not a cadence sample and is deliberately not
    /// counted: a still capture brackets an explicit Render() call at the end of a frame the live preview
    /// has already drawn, and reading that as a stride of zero would drop the camera back to the window it
    /// had before it learned its own rate - one wasted reset on the next real render, i.e. a visibly noisy
    /// preview frame every time a photo is taken.
    /// </summary>
    public void RecordFrame(int frame)
    {
        if (LastFrame >= 0 && frame > LastFrame) { Stride = Mathf.Clamp(frame - LastFrame, 0, MaxGap); }
        LastFrame = frame;
    }

    public void RecordSpecularFrame(int frame)
    {
        if (LastSpecularFrame >= 0 && frame > LastSpecularFrame) { SpecularStride = Mathf.Clamp(frame - LastSpecularFrame, 0, MaxGap); }
        LastSpecularFrame = frame;
    }

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
            // Whichever of the two passes touched this camera most recently keeps it alive. A camera running
            // reflections with the diffuse gather switched off never moves LastFrame, and pruning it would
            // release the accumulation out from under a pass that is still using it every frame.
            BasisGlobalIlluminationHistory store = entry.Value;
            int touched = Mathf.Max(store.LastFrame, store.LastSpecularFrame);
            if (touched >= 0 && frame - touched > maxAge) { pruneScratch.Add(entry.Key); continue; }

            // A camera that is still rendering but stopped asking for reflections - the volume switched off,
            // the player walked out of it - hands those two targets back on the same timer, without taking
            // the diffuse accumulation with them.
            if (store.SpecularAllocated && store.LastSpecularFrame >= 0 && frame - store.LastSpecularFrame > maxAge)
            {
                store.ReleaseSpecular();
            }
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
        return EnsureAllocated(cameraDescriptor, width, height, false);
    }

    public bool EnsureAllocated(in RenderTextureDescriptor cameraDescriptor, int width, int height, bool needsSpecular)
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

        if (needsSpecular)
        {
            bool specularReallocated = !SpecularAllocated;
            for (int slot = 0; slot < 2; slot++)
            {
                specularReallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref Specular[slot], in indirectDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BasisGIHistorySpecular" + slot);
                specularReallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref SpecularStats[slot], in statsDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_BasisGIHistorySpecularStats" + slot);
            }
            SpecularAllocated = true;
            // A resize invalidates reflections for the same reason it invalidates the diffuse history, and
            // so does switching them back on after they were released - the targets are new either way.
            if (specularReallocated || reallocated) { SpecularValid = false; }
        }

        // Deliberately no release on the false branch. Two passes share this store and both call through
        // here in the same frame - the reflection pass asking for the reflection targets, the diffuse pass
        // not asking for them - so releasing on "not asked for" would free targets the reflection pass had
        // already imported into this frame's render graph. Reclaiming them is PruneStale's job, which
        // decides on how long it has actually been since anything wanted them.
        if (reallocated) { Valid = false; }
        return reallocated;
    }

    internal void ReleaseSpecular()
    {
        for (int slot = 0; slot < 2; slot++)
        {
            Specular[slot]?.Release();
            Specular[slot] = null;
            SpecularStats[slot]?.Release();
            SpecularStats[slot] = null;
        }
        SpecularAllocated = false;
        SpecularValid = false;
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
        ReleaseSpecular();
        Width = Height = 0;
        Valid = false;
        Stride = 0;
        SpecularStride = 0;
    }
}
