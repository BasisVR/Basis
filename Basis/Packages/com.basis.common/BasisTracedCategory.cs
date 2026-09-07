/// <summary>
/// Which half of the room one instance in a ray tracing acceleration structure belongs to.
///
/// Ambient occlusion and global illumination trace the same avatars and the same world, and each has its
/// own idea of which of the two it wants - the panel offers Avatars, World, or both to each effect
/// separately. Tagging the instance and masking at trace time is what lets ONE structure answer both
/// questions: the structure holds the union of what the two asked for, and each trace walks only its own
/// half of it.
///
/// Without this the only way to give the two effects different content is a structure each, which is what
/// the frame paid for before: two scans of the scene, two transform sweeps and two full builds every
/// frame, over the same geometry, with every avatar's capsules registered twice.
///
/// Lives in Common because both effects name it and neither should have to depend on the other.
/// </summary>
public static class BasisTracedCategory
{
    /// <summary>Rigid geometry standing on an avatar layer: the props, accessories and shells people carry.</summary>
    public const byte AvatarMesh = 1 << 0;

    /// <summary>Everything else the trace is allowed to see.</summary>
    public const byte World = 1 << 1;

    /// <summary>
    /// The capsules that stand in for a body, as opposed to geometry anybody drew.
    ///
    /// Its own bit because a proxy is the one thing in the structure that does NOT match what is on screen.
    /// Rays start from the depth buffer - the avatar's real rendered surface - and a surface sitting inside
    /// its own capsule fires every ray into the inside of it and reads as fully enclosed. Global illumination
    /// recognises that case by reading a flag off the instance it hit; ambient occlusion has no instance
    /// buffer to read, so it separates the two by MASK instead and traces the capsules on their own. Inside
    /// a proxy-only trace every hit is a proxy by construction, which is what lets the same back-face test
    /// work there without an instance to check it against.
    /// </summary>
    public const byte AvatarProxy = 1 << 2;

    /// <summary>
    /// Everything that is a person. What a trace asks for when it wants avatars - both the capsules standing
    /// in for bodies and the rigid things those bodies carry, which is what "Avatars" has always meant to
    /// anyone choosing it.
    /// </summary>
    public const byte Avatar = AvatarMesh | AvatarProxy;

    /// <summary>Both halves - what a trace asks for when it wants the whole structure.</summary>
    public const byte All = Avatar | World;

    /// <summary>
    /// Which half <paramref name="layer"/> belongs to, given the layers avatars render on.
    ///
    /// The LAYER decides it rather than "is it a skinned mesh": an avatar carries rigid props, accessories
    /// and shells that are ordinary MeshRenderers, and those are still part of the person standing there.
    /// </summary>
    public static byte For(int layer, int avatarLayerMask)
    {
        // AvatarMesh, never the combined Avatar: this classifies RENDERERS, and a renderer is never a proxy.
        // Handing back the combined value would put real geometry inside the proxy-only trace, where the
        // back-face rule would step straight through the inside of a double sided wall.
        return (avatarLayerMask & (1 << layer)) != 0 ? AvatarMesh : World;
    }
}
