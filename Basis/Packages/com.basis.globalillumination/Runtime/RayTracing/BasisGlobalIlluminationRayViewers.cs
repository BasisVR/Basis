using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where the ray traced mode is being looked from - EVERY camera drawing the effect this frame, not just
/// whichever one happened to render first.
///
/// The acceleration structure, the light budget and the emitter budget are all built once per frame and
/// shared by every camera, which is what keeps a mirror, a photo camera and the player's eye from each
/// rebuilding one. What they were shared AROUND, though, was a single position: the first camera of the
/// frame. Everything that ranks or culls by distance - skinned meshes past Skinned Max Distance, the
/// lights that fit the budget, the emitters that fit what is left - therefore answered for that camera
/// and no other.
///
/// The handheld camera is the case where the two positions come apart by more than a rounding error. It
/// can be flown across the room, set to follow from behind, or simply left on a table pointing back at
/// the player, and the moment it is further than Skinned Max Distance from the player's head every avatar
/// standing in front of it is missing from the structure it traces: the photo shows a room where nobody
/// bounces any light and nobody casts a traced shadow, beside a direct view of the same room where they
/// all do. No setting names it, because both cameras are running the same enabled, correctly configured
/// effect.
///
/// So distance is measured to the NEAREST viewer rather than to THE viewer. One structure, one budget,
/// built to cover everything anybody is looking at. The cost is that the budgets are now shared in the
/// literal sense - a light close to the handheld camera can take a slot from one that was only just
/// making the cut for the player - which is the honest trade for two viewpoints out of one list, and far
/// cheaper than a second structure per camera per frame.
/// </summary>
public readonly struct BasisGlobalIlluminationRayViewers
{
    private readonly Vector3 single;
    private readonly List<Vector3> positions;

    /// <summary>
    /// Wraps a live list rather than copying it. The tracer owns the only one and refills it in place each
    /// refresh; nothing here outlives the call it was passed to.
    /// </summary>
    public BasisGlobalIlluminationRayViewers(List<Vector3> positions)
    {
        single = positions != null && positions.Count > 0 ? positions[0] : Vector3.zero;
        this.positions = positions != null && positions.Count > 0 ? positions : null;
    }

    public BasisGlobalIlluminationRayViewers(Vector3 position)
    {
        single = position;
        positions = null;
    }

    /// <summary>
    /// One viewer, spelled as a position. Every caller that genuinely has one - the screen space mode,
    /// which ranks emitters per camera and always did, and the tests - reads exactly as it did before.
    /// </summary>
    public static implicit operator BasisGlobalIlluminationRayViewers(Vector3 position)
    {
        return new BasisGlobalIlluminationRayViewers(position);
    }

    public int Count => positions == null ? 1 : positions.Count;

    public Vector3 this[int index] => positions == null ? single : positions[index];

    /// <summary>
    /// Distance to the nearest viewer. Every rank and every cull goes through here, so adding a viewer can
    /// only ever pull something closer - nothing that was in the structure for one camera leaves it
    /// because another camera appeared.
    /// </summary>
    public float DistanceSquared(Vector3 point)
    {
        if (positions == null) { return (point - single).sqrMagnitude; }

        float nearest = float.MaxValue;
        for (int index = 0; index < positions.Count; index++)
        {
            float distance = (point - positions[index]).sqrMagnitude;
            if (distance < nearest) { nearest = distance; }
        }
        return nearest;
    }
}

/// <summary>
/// Which cameras are drawing the ray traced mode, and where they were the last time each one did.
///
/// The tracer builds the shared structure on whichever camera reaches it first in a frame, so that camera
/// is the only one that can be asked where it is. Everyone else has to have said so already - which is why
/// every camera registers here on every frame it renders, including the frames where the refresh early-outs
/// because another camera had already run it.
/// </summary>
public sealed class BasisGlobalIlluminationRayViewerSet
{
    /// <summary>
    /// How many rendered frames a camera that has stopped rendering the effect keeps its place.
    ///
    /// This has to outlast the slowest camera's own cadence rather than the application's frame rate. The
    /// handheld camera limited to 10Hz on a 90Hz headset renders one frame in nine, and dropping it between
    /// its own renders would rebuild the structure without it on eight frames out of nine - the avatars in
    /// front of it entering and leaving the trace at 10Hz, which is worse than never having had them. A
    /// camera that has genuinely gone quiet costs a few skinned bakes for the second it takes to age out,
    /// and a destroyed one leaves at once.
    /// </summary>
    public const int MaxAge = 60;

    private readonly struct Viewer
    {
        public readonly Camera Camera;
        public readonly Vector3 Position;
        public readonly int Frame;

        public Viewer(Camera camera, Vector3 position, int frame)
        {
            Camera = camera;
            Position = position;
            Frame = frame;
        }
    }

    private readonly Dictionary<EntityId, Viewer> viewers = new Dictionary<EntityId, Viewer>();
    private readonly List<EntityId> pruneScratch = new List<EntityId>();
    private readonly List<Vector3> resolved = new List<Vector3>();

    public int Count => viewers.Count;

    /// <summary>Records that this camera is drawing the effect, and where it is drawing it from.</summary>
    public void Submit(Camera camera, int frame)
    {
        if (camera == null) { return; }
        viewers[camera.GetEntityId()] = new Viewer(camera, camera.transform.position, frame);
    }

    /// <summary>
    /// The viewer set to build this frame's structure around: every camera that has drawn the effect
    /// recently, with <paramref name="camera"/> - the one that got here first, and so the only one whose
    /// position is current rather than remembered - at the front.
    /// </summary>
    public BasisGlobalIlluminationRayViewers Resolve(Camera camera, int frame)
    {
        pruneScratch.Clear();
        resolved.Clear();
        if (camera != null) { resolved.Add(camera.transform.position); }

        EntityId current = camera != null ? camera.GetEntityId() : default;
        foreach (KeyValuePair<EntityId, Viewer> entry in viewers)
        {
            if (entry.Value.Camera == null || frame - entry.Value.Frame > MaxAge)
            {
                pruneScratch.Add(entry.Key);
                continue;
            }
            if (camera != null && entry.Key == current) { continue; }
            resolved.Add(entry.Value.Position);
        }

        for (int index = 0; index < pruneScratch.Count; index++) { viewers.Remove(pruneScratch[index]); }
        pruneScratch.Clear();
        return new BasisGlobalIlluminationRayViewers(resolved);
    }

    public void Clear()
    {
        viewers.Clear();
        pruneScratch.Clear();
        resolved.Clear();
    }
}
