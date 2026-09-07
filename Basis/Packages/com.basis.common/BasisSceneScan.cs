using System;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// One shared answer to "what is in the scene", so that the several systems which have to discover
/// their own content by type do not each pay for the discovery.
///
/// <c>FindObjectsByType</c> walks every loaded object of a type and allocates twice - once natively and
/// once more through <c>Resources.ConvertObjects</c> to hand back a typed array - so it is the sort of
/// call that should happen once for the frame and be read by everybody, not once per interested party.
/// Global illumination and ray traced ambient occlusion want exactly the same two sets (every renderer,
/// every animator) on exactly the same cadence, and before this they each took their own copy: five
/// full-scene walks per rescan window where three would do.
///
/// The cache is aged rather than framed on purpose. Tying it to the frame would only dedupe consumers
/// that happen to scan on the SAME frame, which would force them into lockstep - the opposite of what is
/// wanted, since spreading the scans across different frames is what keeps any one frame cheap. An age
/// lets a consumer say "anything from the last two seconds will do", so whoever asks first pays and
/// everyone asking later in the window reads the same array for nothing, whatever frame they are on.
///
/// The arrays are shared and must be treated as read only. They also hold references for as long as they
/// are cached, so an entry may be a destroyed object by the time it is read: every consumer already null
/// checks, because that was equally true of a freshly taken array the moment anything was destroyed.
/// </summary>
public static class BasisSceneScan
{
    /// <summary>
    /// Per-type storage. A closed generic type gets its own statics, which is the whole trick: one field
    /// per type asked for, without a dictionary lookup or a boxed key on the read path.
    /// </summary>
    private static class Slot<T> where T : Object
    {
        public static T[] Items = Array.Empty<T>();
        public static float TakenAt = float.NegativeInfinity;
        public static int Generation = -1;
    }

    private static int generation;

    /// <summary>
    /// How many times <see cref="Invalidate"/> has been called. Consumers that keep a snapshot across
    /// frames can compare this to know that what they are holding is from before a forced refresh.
    /// </summary>
    public static int Generation => generation;

    /// <summary>
    /// Every active object of this type, from a walk no older than <paramref name="maxAge"/> seconds.
    ///
    /// Inactive objects are excluded, matching every existing caller: a renderer on a disabled object
    /// draws nothing and an animator on one poses nothing, so neither belongs in a structure describing
    /// what is actually there.
    /// </summary>
    public static T[] Take<T>(float maxAge) where T : Object
    {
        float now = Time.unscaledTime;
        // NegativeInfinity as the initial stamp makes the first call scan without needing a separate
        // "never taken" flag, and makes an empty scene cache its emptiness rather than re-walking for it.
        if (Slot<T>.Generation != generation || now - Slot<T>.TakenAt > Mathf.Max(0f, maxAge))
        {
            Slot<T>.Items = Object.FindObjectsByType<T>(FindObjectsInactive.Exclude);
            Slot<T>.TakenAt = now;
            Slot<T>.Generation = generation;
        }
        return Slot<T>.Items;
    }

    /// <summary>
    /// Drops every cached walk, so the next <see cref="Take{T}"/> takes a fresh one however recently it
    /// last did. For the paths that already know the scene changed under them - a world load, a forced
    /// rescan - where waiting out the age would hand back an answer from before the change.
    /// </summary>
    public static void Invalidate()
    {
        generation++;
    }
}
