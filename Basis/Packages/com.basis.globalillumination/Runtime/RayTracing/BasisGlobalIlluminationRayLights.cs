using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>One analytic light the trace kernel shades a hit with. Must match BasisGIRtLight in the kernel.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BasisGlobalIlluminationRayLight
{
    public const int Stride = 64;
    public const float TypeDirectional = 0f, TypePoint = 1f, TypeSpot = 2f;

    public Vector4 position;
    public Vector4 direction;
    public Vector4 color;
    public Vector4 spot;
}

[Serializable]
public struct BasisGlobalIlluminationRayLightSettings
{
    public LayerMask layerMask;
    public int limit;
    public bool shadowRays;
    public bool emitters;
    public float emitterIntensity;
    public float rescanInterval;

    public static BasisGlobalIlluminationRayLightSettings Default => new BasisGlobalIlluminationRayLightSettings
    {
        layerMask = ~0,
        limit = 16,
        shadowRays = true,
        emitters = true,
        emitterIntensity = 1f,
        rescanInterval = 2f
    };
}

/// <summary>
/// The lights a ray hit is shaded by. A hit can be anywhere - behind the camera, outside the frustum, inside
/// a room the player is not in - so this is a scene wide list rather than the culled visible light list, kept
/// on the same rescan cadence as the geometry and re-read every frame so moving lights stay in step.
/// </summary>
public sealed class BasisGlobalIlluminationRayLights : IDisposable
{
    public const int MaxLights = 64;

    private readonly List<Light> scanned = new List<Light>();
    private readonly List<Light> candidates = new List<Light>();
    private readonly List<float> scores = new List<float>();
    private readonly List<BasisGlobalIlluminationEmitter> emitterScratch = new List<BasisGlobalIlluminationEmitter>();
    private readonly BasisGlobalIlluminationRayLight[] data = new BasisGlobalIlluminationRayLight[MaxLights];
    private GraphicsBuffer buffer;
    private float nextScanTime;
    private bool scanPhased;
    private int count;

    public GraphicsBuffer Buffer => buffer;
    public int Count => count;
    public int ScannedCount => scanned.Count;

    /// <summary>The entry the trace kernel would read at this slot. For tests and the debug window.</summary>
    public BasisGlobalIlluminationRayLight At(int slot)
    {
        return slot >= 0 && slot < MaxLights ? data[slot] : default;
    }

    /// <summary>Runs the selection without touching the GPU, so a test can ask what made the budget.</summary>
    public int GatherForTest(in BasisGlobalIlluminationRayLightSettings settings, in BasisGlobalIlluminationRayViewers viewers)
    {
        count = Gather(settings, viewers);
        return count;
    }

    /// <summary>Adds a light to the scanned set without a scene scan, so a test can control the input.</summary>
    public void AddScannedForTest(Light light)
    {
        if (IsSupportedType(light)) { scanned.Add(light); }
    }

    public void MarkDirty() { nextScanTime = 0f; scanPhased = false; }

    public void Refresh(in BasisGlobalIlluminationRayLightSettings settings, in BasisGlobalIlluminationRayViewers viewers, float time)
    {
        if (time >= nextScanTime)
        {
            float interval = Mathf.Max(0.1f, settings.rescanInterval);
            // A quarter interval of extra delay the first time, so the light walk settles between the
            // geometry pass and the avatar pass rather than on top of either. The scene's three discovery
            // walks otherwise fire from timers that all started at zero and advance by the same interval,
            // which keeps them in lockstep on one frame out of every hundred and idle on the rest.
            nextScanTime = time + (scanPhased ? interval : interval * 1.25f);
            scanPhased = true;
            Rescan(interval);
        }

        count = Gather(settings, viewers);
        Upload();
    }

    private void Rescan(float interval)
    {
        scanned.Clear();
        Light[] found = BasisSceneScan.Take<Light>(interval);
        for (int index = 0; index < found.Length; index++)
        {
            if (IsSupportedType(found[index])) { scanned.Add(found[index]); }
        }
    }

    public static bool IsSupportedType(Light light)
    {
        if (light == null) { return false; }
        return light.type == LightType.Directional || light.type == LightType.Point || light.type == LightType.Spot;
    }

    public static bool Contributes(Light light, in BasisGlobalIlluminationRayLightSettings settings)
    {
        if (light == null || !light.isActiveAndEnabled) { return false; }
        if ((settings.layerMask.value & (1 << light.gameObject.layer)) == 0) { return false; }
        if (light.intensity <= 0f || light.bounceIntensity <= 0f) { return false; }
        return light.type == LightType.Directional || light.range > 0f;
    }

    public static float Score(Light light, in BasisGlobalIlluminationRayViewers viewers)
    {
        if (light.type == LightType.Directional) { return float.MaxValue; }
        float power = light.intensity * Mathf.Max(light.color.r, Mathf.Max(light.color.g, light.color.b));
        float distanceSquared = viewers.DistanceSquared(light.transform.position);
        return power / Mathf.Max(0.01f, distanceSquared);
    }

    private int Gather(in BasisGlobalIlluminationRayLightSettings settings, in BasisGlobalIlluminationRayViewers viewers)
    {
        candidates.Clear();
        for (int index = scanned.Count - 1; index >= 0; index--)
        {
            if (scanned[index] == null) { scanned.RemoveAt(index); continue; }
            if (Contributes(scanned[index], settings)) { candidates.Add(scanned[index]); }
        }

        int limit = Mathf.Clamp(settings.limit, 0, MaxLights);

        // Emitters are placed by a world author exactly where the bounce needs help, so they do not queue
        // behind the scene lights for whatever slots happen to be left over. Half the budget is held back
        // for them when there are enough to want it, and anything they do not take goes to the lights.
        // Without this a room with more lights than the budget drops every emitter, and one light going
        // out of range hands a slot back and makes an emitter reappear - a blink with no cause on screen.
        int emitterCount = settings.emitters ? BasisGlobalIlluminationEmitter.CountContributing() : 0;
        int reserved = Mathf.Min(emitterCount, limit / 2);
        int lightLimit = Mathf.Max(0, limit - reserved);

        scores.Clear();
        for (int index = 0; index < candidates.Count; index++)
        {
            scores.Add(Score(candidates[index], viewers));
        }

        int selected = Mathf.Min(candidates.Count, lightLimit);
        for (int slot = 0; slot < selected; slot++)
        {
            int best = slot;
            float bestScore = scores[slot];
            for (int candidate = slot + 1; candidate < candidates.Count; candidate++)
            {
                if (scores[candidate] > bestScore) { best = candidate; bestScore = scores[candidate]; }
            }
            if (best == slot) { continue; }
            Light swap = candidates[slot];
            candidates[slot] = candidates[best];
            candidates[best] = swap;
            float swapScore = scores[slot];
            scores[slot] = scores[best];
            scores[best] = swapScore;
        }

        float boundary = BoundaryWeight(scores, selected);
        for (int slot = 0; slot < selected; slot++)
        {
            BasisGlobalIlluminationRayLight described = Describe(candidates[slot], settings);
            if (slot == selected - 1 && boundary < 1f)
            {
                described.color = new Vector4(described.color.x * boundary, described.color.y * boundary, described.color.z * boundary, described.color.w);
            }
            data[slot] = described;
        }
        candidates.Clear();

        int total = selected;
        if (settings.emitters) { total = AppendEmitters(settings, viewers, total, limit); }
        for (int slot = total; slot < MaxLights; slot++) { data[slot] = default; }
        return total;
    }

    /// <summary>
    /// How much of the last kept light to use. A light that drops out of the budget between one frame and
    /// the next takes all of its light with it, which is seen as a blink; the one that gets displaced is
    /// always the lowest scoring light that was kept, so that one alone is faded by how clearly it beat the
    /// best light that missed the cut. By the time the two swap places they are both worth nothing.
    ///
    /// A directional light is exempt: its score does not depend on where the viewer is standing, so its
    /// place in the budget cannot change from one frame to the next and there is nothing to smooth over.
    /// </summary>
    public static float BoundaryWeight(List<Light> ranked, in BasisGlobalIlluminationRayViewers viewers, int selected)
    {
        if (selected <= 0 || ranked.Count <= selected) { return 1f; }

        float kept = Score(ranked[selected - 1], viewers);
        if (float.IsInfinity(kept) || kept >= float.MaxValue) { return 1f; }
        if (kept <= 0f) { return 0f; }

        float dropped = 0f;
        for (int index = selected; index < ranked.Count; index++)
        {
            dropped = Mathf.Max(dropped, Score(ranked[index], viewers));
        }
        return Mathf.Clamp01(1f - dropped / kept);
    }

    private static float BoundaryWeight(List<float> ranked, int selected)
    {
        if (selected <= 0 || ranked.Count <= selected) { return 1f; }

        float kept = ranked[selected - 1];
        if (float.IsInfinity(kept) || kept >= float.MaxValue) { return 1f; }
        if (kept <= 0f) { return 0f; }

        float dropped = 0f;
        for (int index = selected; index < ranked.Count; index++)
        {
            dropped = Mathf.Max(dropped, ranked[index]);
        }
        return Mathf.Clamp01(1f - dropped / kept);
    }

    /// <summary>
    /// Emitters are the same registry the screen space mode uses, so a world that placed them for the bounce
    /// it could not see keeps working when the player switches to the ray traced mode - and they are ranked
    /// through the same call, so the two modes agree on which ones made the cut.
    /// </summary>
    private int AppendEmitters(in BasisGlobalIlluminationRayLightSettings settings, in BasisGlobalIlluminationRayViewers viewers, int start, int limit)
    {
        int room = Mathf.Max(0, limit - start);
        if (room <= 0) { return start; }

        BasisGlobalIlluminationEmitter.Selection selection = BasisGlobalIlluminationEmitter.Rank(emitterScratch, viewers, room);
        int slot = start;
        for (int index = 0; index < selection.Count; index++)
        {
            BasisGlobalIlluminationEmitter emitter = emitterScratch[index];
            Vector3 radiance = emitter.Radiance * (Mathf.Max(0f, settings.emitterIntensity) * selection.WeightAt(index));
            Vector3 position = emitter.WorldPosition;
            data[slot] = new BasisGlobalIlluminationRayLight
            {
                position = new Vector4(position.x, position.y, position.z, emitter.Range),
                direction = new Vector4(0f, -1f, 0f, BasisGlobalIlluminationRayLight.TypePoint),
                color = new Vector4(radiance.x, radiance.y, radiance.z, emitter.CastsOcclusion && settings.shadowRays ? 1f : 0f),
                spot = new Vector4(1f, 0f, 1f / Mathf.Max(0.0001f, emitter.Range * emitter.Range), Mathf.Max(0.001f, emitter.Radius))
            };
            slot++;
        }
        emitterScratch.Clear();
        return slot;
    }

    public static BasisGlobalIlluminationRayLight Describe(Light light, in BasisGlobalIlluminationRayLightSettings settings)
    {
        Transform transform = light.transform;
        Color linear = light.color.linear;
        if (light.useColorTemperature)
        {
            Color temperature = Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature).linear;
            linear *= temperature;
        }

        float intensity = light.intensity * Mathf.Max(0f, light.bounceIntensity);
        Vector3 radiance = new Vector3(linear.r, linear.g, linear.b) * intensity;
        Vector3 position = transform.position;
        // Normalised HERE, once per light per frame, because the kernel no longer does it: both the
        // directional branch of BasisGIRtDirectLighting and BasisGIRtSpotAttenuation read direction.xyz as
        // a unit vector. transform.forward already is one, and this only costs something on the rare rig
        // whose transform is scaled to zero - which normalized answers with the zero vector rather than a
        // NaN, so a degenerate light goes dark instead of poisoning the gather.
        Vector3 forward = transform.forward.normalized;
        float range = light.type == LightType.Directional ? 0f : Mathf.Max(0.0001f, light.range);
        float shadow = settings.shadowRays && light.shadows != LightShadows.None ? 1f : 0f;

        float spotScale = 1f, spotOffset = 0f;
        if (light.type == LightType.Spot)
        {
            float cosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
            float cosInner = Mathf.Cos(Mathf.Min(light.spotAngle, light.innerSpotAngle) * 0.5f * Mathf.Deg2Rad);
            spotScale = 1f / Mathf.Max(0.001f, cosInner - cosOuter);
            spotOffset = -cosOuter * spotScale;
        }

        float type = light.type == LightType.Directional
            ? BasisGlobalIlluminationRayLight.TypeDirectional
            : light.type == LightType.Spot ? BasisGlobalIlluminationRayLight.TypeSpot : BasisGlobalIlluminationRayLight.TypePoint;

        return new BasisGlobalIlluminationRayLight
        {
            position = new Vector4(position.x, position.y, position.z, range),
            direction = new Vector4(forward.x, forward.y, forward.z, type),
            color = new Vector4(radiance.x, radiance.y, radiance.z, shadow),
            spot = new Vector4(spotScale, spotOffset, range > 0f ? 1f / (range * range) : 0f, 0f)
        };
    }

    private void Upload()
    {
        if (buffer == null)
        {
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxLights, BasisGlobalIlluminationRayLight.Stride)
            {
                name = "_BasisGIRtLights"
            };
        }
        buffer.SetData(data);
    }

    public void Dispose()
    {
        buffer?.Dispose();
        buffer = null;
        scanned.Clear();
        candidates.Clear();
        count = 0;
    }
}
