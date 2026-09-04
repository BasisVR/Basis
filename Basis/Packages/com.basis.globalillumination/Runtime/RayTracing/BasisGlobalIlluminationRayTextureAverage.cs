using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Average colour of a base map or emission map, resolved per texture and cached until it goes stale.
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
    /// <summary>
    /// How long a texture that can change under us keeps its average before it is read again - a
    /// RenderTexture, which is what a video feed, a camera output and AudioLink all write into, or
    /// anything else something has demonstrably written to since. See <see cref="TtlFor"/>.
    /// </summary>
    private const float ResolvedTtlSeconds = 2f;
    /// <summary>
    /// The same for a texture that has not been written to since it was last read - an imported Texture2D,
    /// a cubemap face, anything the world simply put on a wall. Re-reading one on the live cadence bought
    /// nothing and cost a blit, a mip chain and one of the four readback slots every two seconds for the
    /// life of the session; replacing such a texture hands the scene a new EntityId and a fresh resolve
    /// anyway. Long rather than never, because updateCount is only as reliable as whatever wrote the
    /// texture - a backend that rewrites one without incrementing it is 60 seconds stale rather than
    /// permanently so.
    /// </summary>
    private const float StaticTtlSeconds = 60f;
    /// <summary>
    /// How far an average has to move before consumers are told to re-read. Below this the bounce colour
    /// is the same colour; see the note on <see cref="Resolve"/> for why that matters so much.
    /// </summary>
    private const float ChangeEpsilon = 1e-4f;

    private readonly struct ResolvedEntry
    {
        public readonly Color Average;
        public readonly float ResolvedAt;
        /// <summary>
        /// Texture.updateCount as it stood when this average was taken. Unity increments it on Apply,
        /// LoadRawTextureData and the like, so it is the direct answer to "has anything written to this
        /// since I last looked" - which is the only reason to look again.
        /// </summary>
        public readonly uint UpdateCount;

        public ResolvedEntry(Color average, float resolvedAt, uint updateCount)
        {
            Average = average;
            ResolvedAt = resolvedAt;
            UpdateCount = updateCount;
        }
    }

    private readonly Dictionary<EntityId, ResolvedEntry> resolved = new Dictionary<EntityId, ResolvedEntry>();
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
        if (resolved.TryGetValue(key, out ResolvedEntry entry))
        {
            if (!pending.Contains(key) && Time.unscaledTime - entry.ResolvedAt >= TtlFor(texture, entry))
            {
                queued[key] = texture;
            }
            return entry.Average;
        }
        if (!pending.Contains(key)) { queued[key] = texture; }
        return Color.white;
    }

    /// <summary>
    /// How soon a texture that already has an average is worth reading again.
    ///
    /// A RenderTexture is always on the live cadence: a render pass writing into one does not have to
    /// touch updateCount, so that counter cannot be trusted to notice a camera feed or an AudioLink
    /// surface changing. Everything else is asked whether anything has actually written to it - a wall
    /// texture that nobody has touched since it was imported has nothing new to report, and reading it
    /// again every two seconds for the whole session is a blit, a mip chain and a readback slot spent to
    /// re-learn the same colour.
    /// </summary>
    private static float TtlFor(Texture texture, in ResolvedEntry entry)
    {
        if (texture is RenderTexture) { return ResolvedTtlSeconds; }
        return texture.updateCount != entry.UpdateCount ? ResolvedTtlSeconds : StaticTtlSeconds;
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
            if (texture == null) { Resolve(key, Color.white, 0u); continue; }
            Request(texture, key);
        }
        drainScratch.Clear();
    }

    private void Request(Texture texture, EntityId key)
    {
        // Sampled BEFORE the blit, so a write that lands between the blit and the readback landing leaves
        // the stored count behind the texture's own and the next Get asks for it again. The other order
        // would record a write the average never actually saw.
        uint updateCount = texture.updateCount;

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Resolve(key, Color.white, updateCount);
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
            Resolve(key, Color.white, updateCount);
            return;
        }

        pending.Add(key);
        AsyncGPUReadback.Request(scratch, ScratchMip, TextureFormat.RGBAFloat, request =>
        {
            RenderTexture.ReleaseTemporary(scratch);
            if (disposed) { return; }
            pending.Remove(key);
            Resolve(key, Complete(request), updateCount);
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

    /// <summary>
    /// Stores an average, and bumps the version ONLY when it actually moved.
    ///
    /// The version is what tells the scene to re-read every material it holds, and a re-read walks every
    /// instance in the structure through the whole material surface query. A texture whose average comes
    /// back the same number it came back last time gives that walk nothing to find: the value consumers
    /// would re-read is already the value they hold. Bumping regardless is what turned a scan-cadence cost
    /// into a per frame one - the TTL re-queue above keeps four readbacks landing every frame for as long
    /// as the world has more than a handful of textures, so the scene was re-reading all of its materials
    /// on essentially every frame to discover that nothing had changed.
    ///
    /// An epsilon rather than an exact compare because the average is reduced off a freshly blitted mip
    /// chain each time, and a static source is only bit-identical to the extent the GPU's filtering is.
    /// </summary>
    private void Resolve(EntityId key, Color average, uint updateCount)
    {
        bool changed = !resolved.TryGetValue(key, out ResolvedEntry previous) || AverageChanged(previous.Average, average);
        resolved[key] = new ResolvedEntry(average, Time.unscaledTime, updateCount);
        if (changed) { version++; }
    }

    /// <summary>
    /// Whether a freshly read average is far enough from the one already held to be worth telling the
    /// scene about. Public for the same reason <see cref="Complete"/> is: it is the whole of the decision
    /// and it is worth being able to test it without a GPU.
    /// </summary>
    public static bool AverageChanged(Color previous, Color candidate)
    {
        return Mathf.Abs(previous.r - candidate.r) > ChangeEpsilon
            || Mathf.Abs(previous.g - candidate.g) > ChangeEpsilon
            || Mathf.Abs(previous.b - candidate.b) > ChangeEpsilon
            || Mathf.Abs(previous.a - candidate.a) > ChangeEpsilon;
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
