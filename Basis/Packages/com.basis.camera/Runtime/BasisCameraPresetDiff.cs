/// <summary>
/// One value a camera mode writes, named so a report of what has drifted can be read row by row
/// rather than as a single yes or no.
///
/// <para>The list is exactly what <c>BasisHandHeldCamera.CompareToMode</c> compares — a mode only
/// owns a fraction of the settings file, and a value no mode writes can never be off its preset.
/// The ordinals back a bitmask, so entries may be added to the end but not reordered.</para>
/// </summary>
public enum BasisCameraPresetField
{
    Body,
    PositionModifier,
    RotationModifier,
    Effects,
    AutoLevel,
    VrStabilisation,
    Capture360,
    AutoFocusSubject,
    AnchorToBody,
    FollowOffset,
    FieldOfView,
    DepthOfField,
    DepthStyle,
    DepthAperture,
    FocalLength,
    MotionBlur,
    FilmGrain,
    GrainType,
    GrainResponse,
    Vignette,
    VignetteSmoothness,
    VignetteColour,
    VignetteRounded,
    ChromaticAberration,
    WhiteBalanceTemperature,
    WhiteBalanceTint,
    Contrast,
    Saturation,
    BloomIntensity,
    BloomThreshold,
    BloomTint,
    SplitShadows,
    SplitHighlights,
    SplitBalance,
    FilmLift,
    LensDistortion,
    LensDistortionScale,
    Tonemapping,
}

/// <summary>
/// What a camera is holding that its mode did not put there.
///
/// <para>The mode label answers this as one bit — you are in Photo, or you are Custom — which is
/// the right answer for a dropdown and no answer at all for "so what did I change". This is the
/// same comparison kept per value, so the settings readout can colour the rows that moved.</para>
///
/// <para>A default instance is "nothing was compared", which is what a mode with no preset behind
/// it returns. It reports neither a match nor a difference: an uncompared camera has not left
/// anything, and colouring every row of one would be worse than colouring none.</para>
/// </summary>
public readonly struct BasisCameraPresetDiff
{
    /// <summary>The mode compared against, and so the one the coloured rows have drifted from.</summary>
    public readonly BasisCameraMode Mode;

    private readonly ulong Fields;
    private readonly bool Ran;

    internal BasisCameraPresetDiff(BasisCameraMode mode, ulong fields)
    {
        Mode = mode;
        Fields = fields;
        Ran = true;
    }

    /// <summary>Whether there was a preset to compare against at all.</summary>
    public bool Compared => Ran;

    /// <summary>True while every value the mode writes still holds.</summary>
    public bool Matches => Ran && Fields == 0;

    /// <summary>True where at least one of them does not — the case the readout has to explain.</summary>
    public bool HasChanges => Ran && Fields != 0;

    public bool Differs(BasisCameraPresetField field) => Ran && (Fields & Bit(field)) != 0;

    public static ulong Bit(BasisCameraPresetField field) => 1UL << (int)field;
}
