using Unity.Scripting.LifecycleManagement;
using UnityEngine;

/// <summary>
/// Which physical camera the mode hands you.
///
/// <para>A body is the half of a camera a slider cannot reach: how many frames are in it, how long
/// the shutter takes to come back, whether there is a flash on the front at all. Every mode built
/// around <em>where the camera goes</em> — Photo, Flying Puck, Follow Me, Cinematic — hands you the
/// same <see cref="Digital"/> body, which is the one with no constraints; the modes built around
/// <em>what the camera is</em> each hand you their own.</para>
///
/// <para>Stored on the camera in its own right rather than derived from the mode, because editing a
/// disposable's grain drops the mode to Custom and you are still holding a disposable.</para>
/// </summary>
public enum BasisCameraBodyKind
{
    /// <summary>No film, no flash, no waiting. The camera as it has always been, and the zero value.</summary>
    Digital = 0,

    /// <summary>Single-use 35mm: a fixed wide lens, a roll of 27, and a thumbwheel between shots.</summary>
    Disposable = 1,

    /// <summary>Instant film: eight square frames a pack, and a minute of watching one come up.</summary>
    Instant = 2,

    /// <summary>A tape camcorder. Nothing runs out, but everything is soft, smeared and 4:3.</summary>
    Camcorder = 3,

    /// <summary>A ceiling camera. Wide, grey, grainy, and stamped with the time.</summary>
    Security = 4,
}

/// <summary>What a body burns into the corner of the frame it produces.</summary>
public enum BasisCameraStamp
{
    None = 0,

    /// <summary>The date, in the orange seven-segment type a film camera's databack used.</summary>
    Date = 1,

    /// <summary>A running clock, the way a tape deck or a security recorder writes one.</summary>
    Timecode = 2,
}

/// <summary>The frame a finished picture is mounted in, if it is mounted in one at all.</summary>
public enum BasisCameraPrintBorder
{
    None = 0,

    /// <summary>The white surround of an instant print, with the fat strip along the bottom.</summary>
    Instant = 1,
}

/// <summary>
/// Why the shutter will not fire. Every one of these lifts on its own — a virtual camera is never
/// out of anything, so waiting is always the way on and there is nothing for the operator to do.
/// </summary>
public enum BasisCameraShutterState
{
    Ready = 0,

    /// <summary>Mid wind-on. Waiting is the only way on.</summary>
    WindingOn = 1,

    /// <summary>The last frame is still coming up. Waiting is the only way on.</summary>
    Developing = 2,
}

/// <summary>
/// One body's physical facts. Pure data with no scene dependencies, so the panel, the camera and
/// the tests all read the same table.
/// </summary>
public sealed class BasisCameraBodyTraits
{
    public readonly BasisCameraBodyKind Kind;

    /// <summary>Frames on a full load, or <see cref="BasisCameraBodies.Unlimited"/> for a body that never runs out.</summary>
    public readonly int Exposures;

    /// <summary>Seconds the shutter is locked out after a frame, while the film is advanced.</summary>
    public readonly float WindOnSeconds;

    /// <summary>Seconds a finished frame spends coming up before the next one can be taken. Instant film only.</summary>
    public readonly float DevelopSeconds;

    public readonly bool HasFlash;

    /// <summary>Seconds the flash takes to charge again. A disposable's whine, in one number.</summary>
    public readonly float FlashRecycleSeconds;

    /// <summary>How long the pop lasts. Long enough to be seen, short enough not to be a lamp.</summary>
    public readonly float FlashSeconds;

    public readonly float FlashIntensity;
    public readonly float FlashRange;
    public readonly float FlashAngle;
    public readonly Color FlashColour;

    /// <summary>
    /// Whether the body can send its feed anywhere but its own viewfinder. False on the film bodies:
    /// there is no socket on the back of a disposable, so direct-to-screen and
    /// the video output are all things this camera does not have rather than things switched off.
    /// </summary>
    public readonly bool LivePreview;

    public readonly BasisCameraStamp Stamp;

    /// <summary>The frame the finished picture is mounted in.</summary>
    public readonly BasisCameraPrintBorder PrintBorder;

    /// <summary>
    /// Whether the first and last frames of a roll come back fogged. True on a body whose film is
    /// loaded and unloaded in daylight through a back that does not seal.
    /// </summary>
    public readonly bool LeaksLight;

    /// <summary>
    /// The size of the sensitive area, in millimetres, or zero to leave the lens alone. Not a look:
    /// it is what the focal length and the aperture are measured against, so a body that shoots a
    /// 79mm square print and one that shoots a 4.8mm sensor do not mean the same thing by f/8.
    /// </summary>
    public readonly Vector2 SensorSize;

    /// <summary>
    /// The frame this body shoots, or zero to leave the chosen resolution alone. A body that has a
    /// size of its own owns the whole of it — a square instant frame and a 4:3 tape frame are not
    /// resolutions on a list, they are the shape of the thing.
    /// </summary>
    public readonly Vector2Int CaptureSize;

    internal BasisCameraBodyTraits(
        BasisCameraBodyKind kind,
        int exposures,
        float windOnSeconds,
        float developSeconds,
        bool hasFlash,
        float flashRecycleSeconds,
        float flashSeconds,
        float flashIntensity,
        float flashRange,
        float flashAngle,
        Color flashColour,
        bool livePreview,
        BasisCameraStamp stamp,
        BasisCameraPrintBorder printBorder,
        bool leaksLight,
        Vector2 sensorSize,
        Vector2Int captureSize)
    {
        Kind = kind;
        Exposures = exposures;
        WindOnSeconds = windOnSeconds;
        DevelopSeconds = developSeconds;
        HasFlash = hasFlash;
        FlashRecycleSeconds = flashRecycleSeconds;
        FlashSeconds = flashSeconds;
        FlashIntensity = flashIntensity;
        FlashRange = flashRange;
        FlashAngle = flashAngle;
        FlashColour = flashColour;
        LivePreview = livePreview;
        Stamp = stamp;
        PrintBorder = printBorder;
        LeaksLight = leaksLight;
        SensorSize = sensorSize;
        CaptureSize = captureSize;
    }

    /// <summary>True where the frame count is real, and so worth showing and worth spending.</summary>
    public bool HasFilm => Exposures > 0;

    /// <summary>True where anything at all about this body can refuse or delay a shot.</summary>
    public bool Constrains => HasFilm || WindOnSeconds > 0f || DevelopSeconds > 0f;
}

/// <summary>The body table, and the one lookup everything goes through.</summary>
[NoAutoStaticsCleanup]
public static class BasisCameraBodies
{
    /// <summary>A frame count that never goes down. Tape and disk, as opposed to film.</summary>
    public const int Unlimited = 0;

    /// <summary>
    /// A camera with nothing in its way: the four placement modes all hand this out, and it is what
    /// a body-less field reads as, so nothing has to null-check a kind it does not recognise.
    /// </summary>
    private static readonly BasisCameraBodyTraits DigitalBody = new BasisCameraBodyTraits(
        BasisCameraBodyKind.Digital,
        exposures: Unlimited,
        windOnSeconds: 0f,
        developSeconds: 0f,
        hasFlash: false,
        flashRecycleSeconds: 0f,
        flashSeconds: 0f,
        flashIntensity: 0f,
        flashRange: 0f,
        flashAngle: 0f,
        flashColour: Color.white,
        livePreview: true,
        stamp: BasisCameraStamp.None,
        printBorder: BasisCameraPrintBorder.None,
        leaksLight: false,
        sensorSize: Vector2.zero,
        captureSize: Vector2Int.zero);

    /// <summary>
    /// Twenty-seven frames, a thumbwheel, and a flash that has to be waited for. The wind-on is the
    /// whole character of the thing: it is what stops a disposable being a camera you spray with,
    /// and it is why the shot you did take is the one you get.
    /// </summary>
    private static readonly BasisCameraBodyTraits DisposableBody = new BasisCameraBodyTraits(
        BasisCameraBodyKind.Disposable,
        exposures: 27,
        windOnSeconds: 1.1f,
        developSeconds: 0f,
        hasFlash: true,
        flashRecycleSeconds: 6f,
        flashSeconds: 0.14f,
        flashIntensity: 55f,
        flashRange: 9f,
        flashAngle: 96f,
        // Slightly cool against the warm film stock, which is what makes a flash frame read as
        // flashed rather than as a brighter version of the same picture.
        flashColour: new Color(0.94f, 0.96f, 1f),
        livePreview: false,
        stamp: BasisCameraStamp.Date,
        printBorder: BasisCameraPrintBorder.None,

        // The back is a plastic clamshell that gets opened in daylight to take the film out, and
        // the leader was wound in daylight too, so the ends of a roll come back fogged.
        leaksLight: true,

        // A full 35mm frame.
        sensorSize: new Vector2(36f, 24f),

        // 3:2, the shape of a 35mm frame, at the size a drugstore scan came back at.
        captureSize: new Vector2Int(1296, 864));

    /// <summary>
    /// Eight square frames and a wait. The develop time is the point of the body — the picture is
    /// not yours the moment you press the button, and a pack of eight makes that cost something.
    /// </summary>
    private static readonly BasisCameraBodyTraits InstantBody = new BasisCameraBodyTraits(
        BasisCameraBodyKind.Instant,
        exposures: 8,
        windOnSeconds: 1.6f,
        developSeconds: 11f,
        hasFlash: true,
        flashRecycleSeconds: 5f,
        flashSeconds: 0.12f,
        flashIntensity: 48f,
        flashRange: 7f,
        flashAngle: 104f,
        flashColour: new Color(0.98f, 0.97f, 1f),
        livePreview: false,
        stamp: BasisCameraStamp.None,

        // The border is the whole reason an instant photograph is recognisable across a room.
        printBorder: BasisCameraPrintBorder.Instant,

        // Nothing to fog: the pack is sealed and each sheet is pushed out through rollers, so the
        // film is never in the light until it is already developing.
        leaksLight: false,

        // The 3.1 inch square image area, in millimetres.
        sensorSize: new Vector2(79f, 79f),
        captureSize: new Vector2Int(1024, 1024));

    /// <summary>
    /// Tape. Nothing runs out and nothing waits, so the constraints are all in the picture: the
    /// zoom still works, which is the one thing this body has that the film ones do not.
    /// </summary>
    private static readonly BasisCameraBodyTraits CamcorderBody = new BasisCameraBodyTraits(
        BasisCameraBodyKind.Camcorder,
        exposures: Unlimited,
        windOnSeconds: 0f,
        developSeconds: 0f,
        hasFlash: false,
        flashRecycleSeconds: 0f,
        flashSeconds: 0f,
        flashIntensity: 0f,
        flashRange: 0f,
        flashAngle: 0f,
        flashColour: Color.white,
        livePreview: true,
        stamp: BasisCameraStamp.Timecode,
        printBorder: BasisCameraPrintBorder.None,
        leaksLight: false,

        // A quarter-inch CCD, which is why a camcorder's zoom reaches so far on so little glass.
        sensorSize: new Vector2(6.4f, 4.8f),
        captureSize: new Vector2Int(640, 480));

    /// <summary>
    /// A camera bolted to a ceiling. Wide, fixed, and stamped — it is the only body here that was
    /// never meant to be held, which is most of the joke.
    /// </summary>
    private static readonly BasisCameraBodyTraits SecurityBody = new BasisCameraBodyTraits(
        BasisCameraBodyKind.Security,
        exposures: Unlimited,
        windOnSeconds: 0f,
        developSeconds: 0f,
        hasFlash: false,
        flashRecycleSeconds: 0f,
        flashSeconds: 0f,
        flashIntensity: 0f,
        flashRange: 0f,
        flashAngle: 0f,
        flashColour: Color.white,
        livePreview: true,
        stamp: BasisCameraStamp.Timecode,
        printBorder: BasisCameraPrintBorder.None,
        leaksLight: false,

        // Smaller again, and behind a very short lens — the two together are what give a ceiling
        // camera its everything-at-once depth of field.
        sensorSize: new Vector2(4.8f, 3.6f),
        captureSize: new Vector2Int(800, 600));

    /// <summary>
    /// The traits for a body. Never null and never throws: a kind off disk that this build does not
    /// have is a digital camera, which is the one answer that cannot strand anybody.
    /// </summary>
    public static BasisCameraBodyTraits Get(BasisCameraBodyKind kind)
    {
        switch (kind)
        {
            case BasisCameraBodyKind.Disposable: return DisposableBody;
            case BasisCameraBodyKind.Instant: return InstantBody;
            case BasisCameraBodyKind.Camcorder: return CamcorderBody;
            case BasisCameraBodyKind.Security: return SecurityBody;
            default: return DigitalBody;
        }
    }

    /// <summary>Clamps a raw int — a settings file, or a network field — onto a kind that exists.</summary>
    public static BasisCameraBodyKind Sanitize(int kind) =>
        kind >= (int)BasisCameraBodyKind.Digital && kind <= (int)BasisCameraBodyKind.Security
            ? (BasisCameraBodyKind)kind
            : BasisCameraBodyKind.Digital;

    /// <summary>The localization key naming a body, for the panel's status line.</summary>
    public static string TitleKey(BasisCameraBodyKind kind)
    {
        switch (kind)
        {
            case BasisCameraBodyKind.Disposable: return "camera.modePreset.disposable";
            case BasisCameraBodyKind.Instant: return "camera.modePreset.instant";
            case BasisCameraBodyKind.Camcorder: return "camera.modePreset.camcorder";
            case BasisCameraBodyKind.Security: return "camera.modePreset.security";
            default: return "camera.body.digital";
        }
    }
}
