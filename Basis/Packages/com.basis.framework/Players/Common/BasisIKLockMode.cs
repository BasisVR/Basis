/// <summary>
/// Controls how the spine IK chain resolves the relationship between head and hips.
/// Inspired by VRChat's Lock Head / Lock Hips / Lock Both system.
/// </summary>
public enum BasisIKLockMode
{
    /// <summary>
    /// Hips are the anchor. Hips position is applied directly and not clamped to the head.
    /// The spine chain solves upward from hips toward the head target.
    /// Best default for full-body tracking with a hip tracker.
    /// Prevents spine curvature caused by leg movement.
    /// </summary>
    LockHips = 0,

    /// <summary>
    /// Head is the anchor. Hips position is derived from the head position
    /// (directly below, offset by spine rest length).
    /// The spine stays relatively straight. Legs don't affect spine curvature.
    /// Best for HMD-only or 3-point tracking without a hip tracker.
    /// </summary>
    LockHead = 1,

    /// <summary>
    /// Both head and hips positions are independently applied.
    /// The spine chain must stretch/compress to connect them.
    /// Can cause undesired spine curvature when legs move.
    /// </summary>
    LockBoth = 2,
}
