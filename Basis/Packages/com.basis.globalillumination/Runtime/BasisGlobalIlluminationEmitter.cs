using System.Collections.Generic;
using UnityEngine;

// Registration runs in edit mode too, so an author placing emitters sees their light in the scene view
// rather than only after entering play mode.
[ExecuteAlways]
[AddComponentMenu("Basis/Rendering/Basis Global Illumination Emitter")]
[DisallowMultipleComponent]
public sealed class BasisGlobalIlluminationEmitter : MonoBehaviour
{
    public static readonly List<BasisGlobalIlluminationEmitter> Registered = new List<BasisGlobalIlluminationEmitter>();

    [ColorUsage(false, true)] public Color Color = Color.white;
    [Min(0f)] public float Intensity = 1f;
    [Min(0.001f)] public float Radius = 0.25f;
    [Min(0f)] public float Range = 12f;
    public bool CastsOcclusion = true;

    private void OnEnable() { Register(); }

    private void OnDisable() { Unregister(); }

    /// <summary>Joins the registry the gather reads. Public so a test can stand in for the lifecycle.</summary>
    public void Register()
    {
        if (!Registered.Contains(this)) { Registered.Add(this); }
    }

    public void Unregister()
    {
        Registered.Remove(this);
    }

    public Vector3 WorldPosition => transform.position;

    public Vector3 Radiance
    {
        get
        {
            Color linear = Color.linear;
            return new Vector3(linear.r, linear.g, linear.b) * Intensity;
        }
    }

    public bool Contributes => isActiveAndEnabled && Intensity > 0f && Range > 0f;

    public static void PruneDestroyed()
    {
        for (int index = Registered.Count - 1; index >= 0; index--)
        {
            if (Registered[index] == null) { Registered.RemoveAt(index); }
        }
    }

    /// <summary>How many emitters would contribute if the budget were unlimited.</summary>
    public static int CountContributing()
    {
        int count = 0;
        for (int index = 0; index < Registered.Count; index++)
        {
            BasisGlobalIlluminationEmitter emitter = Registered[index];
            if (emitter != null && emitter.Contributes) { count++; }
        }
        return count;
    }

    /// <summary>Brightness over distance squared: what decides which emitters a budget keeps.</summary>
    public static float Score(BasisGlobalIlluminationEmitter emitter, Vector3 viewer)
    {
        Vector3 radiance = emitter.Radiance;
        float power = Mathf.Max(radiance.x, Mathf.Max(radiance.y, radiance.z));
        float distanceSquared = (emitter.WorldPosition - viewer).sqrMagnitude;
        return power / Mathf.Max(0.01f, distanceSquared);
    }

    /// <summary>
    /// The emitters a budget of <paramref name="limit"/> keeps, brightest and nearest first, together with
    /// how much of the last one's light to use. Both modes rank through here so a world looks the same
    /// either side of the mode switch.
    /// </summary>
    public readonly struct Selection
    {
        /// <summary>How many entries at the front of the destination list were kept.</summary>
        public readonly int Count;
        /// <summary>What to scale the last kept emitter by; one when nothing was dropped.</summary>
        public readonly float BoundaryWeight;

        public Selection(int count, float boundaryWeight)
        {
            Count = count;
            BoundaryWeight = boundaryWeight;
        }

        public float WeightAt(int slot) { return slot == Count - 1 ? BoundaryWeight : 1f; }
    }

    /// <summary>
    /// Ranks the contributing emitters into <paramref name="destination"/> and returns how many fit the
    /// budget.
    ///
    /// More emitters than slots means the kept set changes as the viewer moves, and an emitter that drops
    /// out between one frame and the next takes all of its light with it - a step change in a pixel, which
    /// is seen as a blink. The emitter that gets displaced is always the lowest scoring one that was kept,
    /// so that one alone is faded by how clearly it beat the best emitter that missed the cut: by the time
    /// the two swap places they are both contributing nothing and the swap is invisible.
    /// </summary>
    public static Selection Rank(List<BasisGlobalIlluminationEmitter> destination, Vector3 viewer, int limit)
    {
        PruneDestroyed();
        destination.Clear();
        for (int index = 0; index < Registered.Count; index++)
        {
            BasisGlobalIlluminationEmitter emitter = Registered[index];
            if (emitter.Contributes) { destination.Add(emitter); }
        }

        int selected = Mathf.Clamp(limit, 0, destination.Count);
        for (int slot = 0; slot < selected; slot++)
        {
            int best = slot;
            float bestScore = Score(destination[slot], viewer);
            for (int candidate = slot + 1; candidate < destination.Count; candidate++)
            {
                float score = Score(destination[candidate], viewer);
                if (score > bestScore) { best = candidate; bestScore = score; }
            }
            if (best == slot) { continue; }
            BasisGlobalIlluminationEmitter swap = destination[slot];
            destination[slot] = destination[best];
            destination[best] = swap;
        }

        return new Selection(selected, BoundaryWeight(destination, viewer, selected));
    }

    private static float BoundaryWeight(List<BasisGlobalIlluminationEmitter> ranked, Vector3 viewer, int selected)
    {
        if (selected <= 0 || ranked.Count <= selected) { return 1f; }

        float dropped = 0f;
        for (int index = selected; index < ranked.Count; index++)
        {
            dropped = Mathf.Max(dropped, Score(ranked[index], viewer));
        }

        float kept = Score(ranked[selected - 1], viewer);
        if (kept <= 0f) { return 0f; }
        return Mathf.Clamp01(1f - dropped / kept);
    }
}
