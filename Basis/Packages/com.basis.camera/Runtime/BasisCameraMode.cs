using Unity.Scripting.LifecycleManagement;
/// <summary>
/// A named bundle of camera settings for one job. The handheld camera carries far more controls
/// than any single use needs, so a mode writes the settings that job wants. Nothing is ever locked —
/// editing a value a mode writes drops the camera to <see cref="Custom"/>, which is how the panel
/// says "these are no longer the preset's numbers" without taking the control away.
/// </summary>
public enum BasisCameraMode
{
    /// <summary>Hand-tuned. Only ever detected, never applied — there is nothing to apply.</summary>
    Custom = 0,

    /// <summary>The ordinary handheld camera: held in your hand, shallow depth of field, stills.</summary>
    Photo = 1,

    /// <summary>Parked in the world and flown by hand. Steady and wide, for streaming a room.</summary>
    FlyingPuck = 2,

    /// <summary>Flies itself, keeping you in frame and in focus.</summary>
    FollowMe = 3,

    /// <summary>Driven by the shot rig — dolly, orbit and framing move the camera, not you.</summary>
    Cinematic = 4,

    // The four below are camera <em>kinds</em> rather than jobs: each hands you a different body,
    // and the body is the half of them a slider cannot undo. See <see cref="BasisCameraBodyKind"/>.

    /// <summary>A single-use 35mm: twenty-seven warm, grainy frames and a flash you wait for.</summary>
    Disposable = 5,

    /// <summary>Instant film: eight square prints a pack, each one a minute in coming up.</summary>
    Instant = 6,

    /// <summary>A tape camcorder. Soft, smeared, desaturated 4:3 with the clock burned in.</summary>
    Camcorder = 7,

    /// <summary>A ceiling camera: very wide, nearly grey, and stamped with the time.</summary>
    Security = 8,
}

/// <summary>Everything the panel needs to present one mode: its name and what it is for.</summary>
public sealed class BasisCameraModeDescriptor
{
    public readonly BasisCameraMode Mode;
    public readonly string TitleKey;
    public readonly string DescriptionKey;

    internal BasisCameraModeDescriptor(BasisCameraMode mode, string titleKey, string descriptionKey)
    {
        Mode = mode;
        TitleKey = titleKey;
        DescriptionKey = descriptionKey;
    }
}

/// <summary>
/// The mode table. Pure data with no scene dependencies, so both the panel and the tests can read
/// it without a camera in hand. The values each mode <em>writes</em> live next to the code that
/// writes them, in the camera's own mode partial.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisCameraModes
{
    /// <summary>Presentation order, and the order of the panel's mode dropdown.</summary>
    public static readonly BasisCameraMode[] Ordered =
    {
        BasisCameraMode.Photo,
        BasisCameraMode.FlyingPuck,
        BasisCameraMode.FollowMe,
        BasisCameraMode.Cinematic,

        // The kinds come after the jobs, and in the order they were made: a roll of film, then a
        // pack of instant, then tape, then whatever a security camera runs on.
        BasisCameraMode.Disposable,
        BasisCameraMode.Instant,
        BasisCameraMode.Camcorder,
        BasisCameraMode.Security,

        BasisCameraMode.Custom,
    };

    private static readonly BasisCameraModeDescriptor[] Descriptors = BuildDescriptors();

    public static BasisCameraModeDescriptor Get(BasisCameraMode mode)
    {
        for (int Index = 0; Index < Descriptors.Length; Index++)
        {
            if (Descriptors[Index].Mode == mode) return Descriptors[Index];
        }

        return Descriptors[0];
    }

    private static BasisCameraModeDescriptor[] BuildDescriptors()
    {
        return new[]
        {
            new BasisCameraModeDescriptor(BasisCameraMode.Custom, "camera.modePreset.custom", "camera.modePreset.custom.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Photo, "camera.modePreset.photo", "camera.modePreset.photo.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.FlyingPuck, "camera.modePreset.flyingPuck", "camera.modePreset.flyingPuck.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.FollowMe, "camera.modePreset.followMe", "camera.modePreset.followMe.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Cinematic, "camera.modePreset.cinematic", "camera.modePreset.cinematic.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Disposable, "camera.modePreset.disposable", "camera.modePreset.disposable.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Instant, "camera.modePreset.instant", "camera.modePreset.instant.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Camcorder, "camera.modePreset.camcorder", "camera.modePreset.camcorder.description"),
            new BasisCameraModeDescriptor(BasisCameraMode.Security, "camera.modePreset.security", "camera.modePreset.security.description"),
        };
    }
}
