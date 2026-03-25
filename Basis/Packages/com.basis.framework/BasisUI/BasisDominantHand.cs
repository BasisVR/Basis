using Basis.BasisUI;
using Basis.Scripts.TransformBinders.BoneControl;

/// <summary>
/// Provides the user's dominant/non-dominant hand roles based on the settings system.
/// </summary>
public static class BasisDominantHand
{
    public const string Right = "right";
    public const string Left = "left";

    /// <summary>
    /// Returns <c>true</c> when the user's dominant hand is the left hand.
    /// </summary>
    public static bool IsLeftHanded => BasisSettingsDefaults.DominantHand.RawValue == Left;

    /// <summary>
    /// The tracked role of the user's dominant hand.
    /// </summary>
    public static BasisBoneTrackedRole DominantRole =>
        IsLeftHanded ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand;

    /// <summary>
    /// The tracked role of the user's non-dominant hand.
    /// </summary>
    public static BasisBoneTrackedRole NonDominantRole =>
        IsLeftHanded ? BasisBoneTrackedRole.RightHand : BasisBoneTrackedRole.LeftHand;
}
