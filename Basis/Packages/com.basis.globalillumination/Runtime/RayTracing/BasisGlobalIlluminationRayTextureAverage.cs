using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Average colour of a base map or emission map, resolved once per texture and cached forever.
/// A hit only carries a per-instance colour, so without this every textured surface would bounce its
/// material tint instead of what it actually looks like - and almost every lit material ships white.
/// The average is read off the smallest mip of a scratch copy, which works for compressed and
/// non-readable textures alike, so it costs one tiny async readback per unique texture.
///
/// The copy is a blit, and a blit is not something that can be issued while a render graph is being
/// recorded - which is exactly when the scene walk that asks for these averages runs. Requests are
/// therefore queued as they are asked for and drained once the frame's contexts have been submitted.
/// </summary>
public sealed class BasisGlobalIlluminationRayTextureAverage : IDisposable
{
    public const int RequestsInFlightLimit = 4;
    private const int ScratchSize = 16;
    private const int ScratchMip = 4;

    private readonly Dictionary<EntityId, Color> resolved = new Dictionary<EntityId, Color>();
    private readonly HashSet<EntityId> pending = new HashSet<EntityId>();
    private readonly Dictionary<EntityId, Texture> queued = new Dictionary<EntityId, Texture>();
    private readonly List<EntityId> drainScratch = new List<EntityId>();
    private int version;
    private bool disposed;

    /// <summary>Bumped whenever an average lands, so the scene knows to re-read its materials.</summary>
    public int Version => version;
    public int ResolvedCount => resolved.Count;
    public int PendingCount => pending.Count;
    public int QueuedCount => queued.Count;

    public BasisGlobalIlluminationRayTextureAverage()
    {
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
    }

    /// <summary>
    /// The texture's average, or white until it has been read back. White is the honest placeholder: it is
    /// what a material with no map at all resolves to, so a surface never darkens and then brightens on
    /// the frame its average lands.
    /// </summary>
    public Color Get(Texture texture)
    {
        if (texture == null || disposed) { return Color.white; }

        EntityId key = texture.GetEntityId();
        if (resolved.TryGetValue(key, out Color average)) { return average; }
        if (!pending.Contains(key)) { queued[key] = texture; }
        return Color.white;
    }

    private void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        Pump();
    }

    /// <summary>
    /// Issues as many queued readbacks as the in-flight budget allows. Safe to call whenever a blit is
    /// legal; the scene walk calls nothing here, it only adds to the queue.
    /// </summary>
    public void Pump()
    {
        if (disposed || queued.Count == 0) { return; }

        drainScratch.Clear();
        foreach (KeyValuePair<EntityId, Texture> entry in queued)
        {
            if (pending.Count + drainScratch.Count >= RequestsInFlightLimit) { break; }
            drainScratch.Add(entry.Key);
        }

        for (int index = 0; index < drainScratch.Count; index++)
        {
            EntityId key = drainScratch[index];
            Texture texture = queued[key];
            queued.Remove(key);
            if (texture == null) { resolved[key] = Color.white; version++; continue; }
            Request(texture, key);
        }
        drainScratch.Clear();
    }

    private void Request(Texture texture, EntityId key)
    {
        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            resolved[key] = Color.white;
            version++;
            return;
        }

        RenderTexture scratch = RenderTexture.GetTemporary(new RenderTextureDescriptor(ScratchSize, ScratchSize, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None)
        {
            useMipMap = true,
            autoGenerateMips = false,
            sRGB = false
        });

        try
        {
            Graphics.Blit(texture, scratch);
            scratch.GenerateMips();
        }
        catch (Exception)
        {
            RenderTexture.ReleaseTemporary(scratch);
            resolved[key] = Color.white;
            version++;
            return;
        }

        pending.Add(key);
        AsyncGPUReadback.Request(scratch, ScratchMip, TextureFormat.RGBAFloat, request =>
        {
            RenderTexture.ReleaseTemporary(scratch);
            if (disposed) { return; }
            pending.Remove(key);
            resolved[key] = Complete(request);
            version++;
        });
    }

    public static Color Complete(AsyncGPUReadbackRequest request)
    {
        if (request.hasError) { return Color.white; }
        Unity.Collections.NativeArray<Color> pixels = request.GetData<Color>();
        if (!pixels.IsCreated || pixels.Length == 0) { return Color.white; }

        Color total = Color.clear;
        for (int index = 0; index < pixels.Length; index++) { total += pixels[index]; }
        Color average = total / pixels.Length;
        return new Color(Mathf.Max(0f, average.r), Mathf.Max(0f, average.g), Mathf.Max(0f, average.b), 1f);
    }

    public void Clear()
    {
        resolved.Clear();
        pending.Clear();
        queued.Clear();
        version++;
    }

    public void Dispose()
    {
        disposed = true;
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        resolved.Clear();
        pending.Clear();
        queued.Clear();
    }
}
