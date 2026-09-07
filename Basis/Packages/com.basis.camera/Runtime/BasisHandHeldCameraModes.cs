using Basis.Cinematics;
using UnityEngine;
using CameraAnchorKind = BasisHandHeldCameraInteractable.CameraAnchorKind;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;

/// <summary>
/// Camera modes: applying one, and noticing when the camera has drifted off it.
///
/// <para>The values a mode writes live in a single <see cref="BasisCameraModePreset"/> table that
/// both <see cref="ApplyCameraMode"/> and <see cref="MatchesCameraMode"/> read, so "what the mode
/// sets" and "what counts as still being in the mode" cannot drift apart. A round-trip test asserts
/// that applying any mode leaves the camera matching it.</para>
///
/// <para>Detection is by comparison rather than by hooking the ~60 setters the panel and the prop
/// HUD share. That costs a handful of float compares on the panel tick and in exchange it catches
/// changes made from either surface, including ones made while the panel was shut.</para>
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>
    /// The mode the camera is in. Written when a mode is applied and re-derived whenever the
    /// settings are polled, so it is never stale by more than a tick.
    /// </summary>
    public BasisCameraMode CameraMode { get; private set; } = BasisCameraMode.Photo;

    /// <summary>
    /// The mode the camera's values are read against — the last one it was actually put into,
    /// which is what a Custom camera has drifted <em>from</em>.
    ///
    /// <para>It has to outlive <see cref="CameraMode"/> going Custom, because that is the only
    /// moment it is worth anything: the settings readout answers "what have I changed" by
    /// comparing against this, and a camera still sitting exactly on a preset has changed nothing
    /// to show. Only ever a real mode — Custom is where the camera ends up, never what it is
    /// measured against.</para>
    /// </summary>
    public BasisCameraMode ComparedMode { get; private set; } = BasisCameraMode.Photo;

    /// <summary>
    /// The picture a camera kind makes: grain, cast, contrast and the rest of the post-processing
    /// that turns a clean render into a photograph off a particular machine.
    ///
    /// <para>Optional, and absent from the four placement modes on purpose. Photo, Flying Puck,
    /// Follow Me and Cinematic have opinions about where the camera goes and what lens it wears,
    /// and none at all about grain — so they neither write these nor compare them, and adding
    /// film grain to a Photo camera leaves it a Photo camera. A kind writes all of them, which is
    /// also what makes a kind a kind: it owns the picture end to end.</para>
    /// </summary>
    private readonly struct BasisCameraLook
    {
        /// <summary>False on a mode that has no opinion about the picture. Gates the write and the compare together.</summary>
        public readonly bool Active;

        public readonly float FilmGrain;
        public readonly float Vignette;
        public readonly float VignetteSmoothness;
        public readonly float ChromaticAberration;
        public readonly float WhiteBalanceTemperature;
        public readonly float WhiteBalanceTint;
        public readonly float Contrast;
        public readonly float Saturation;
        public readonly float BloomIntensity;
        public readonly float BloomThreshold;
        public readonly float LensDistortion;
        public readonly float LensDistortionScale;

        /// <summary>How the still is graded, as <c>TonemappingMode</c>. The viewfinder is always Neutral.</summary>
        public readonly int Tonemapping;

        // ---- The grading that separates a stock from a filter ----------------------------------

        /// <summary>Which grain texture, as <c>FilmGrainLookup</c>. Size, not strength.</summary>
        public readonly int GrainType;

        /// <summary>How far the grain backs off in the highlights.</summary>
        public readonly float GrainResponse;

        /// <summary>The colour of the glow around a highlight — halation on film, plain bloom otherwise.</summary>
        public readonly Color BloomTint;

        public readonly Color VignetteColour;
        public readonly bool VignetteRounded;

        /// <summary>Neutral is grey at both ends, not black.</summary>
        public readonly Color SplitShadows;
        public readonly Color SplitHighlights;
        public readonly float SplitBalance;

        /// <summary>The raised black point. The single strongest lever a film look has.</summary>
        public readonly float Lift;

        public BasisCameraLook(
            float filmGrain,
            int grainType,
            float grainResponse,
            float vignette,
            float vignetteSmoothness,
            Color vignetteColour,
            bool vignetteRounded,
            float chromaticAberration,
            float whiteBalanceTemperature,
            float whiteBalanceTint,
            float contrast,
            float saturation,
            float lift,
            Color splitShadows,
            Color splitHighlights,
            float splitBalance,
            float bloomIntensity,
            float bloomThreshold,
            Color bloomTint,
            float lensDistortion,
            float lensDistortionScale,
            int tonemapping)
        {
            Active = true;
            FilmGrain = filmGrain;
            GrainType = grainType;
            GrainResponse = grainResponse;
            Vignette = vignette;
            VignetteSmoothness = vignetteSmoothness;
            VignetteColour = vignetteColour;
            VignetteRounded = vignetteRounded;
            ChromaticAberration = chromaticAberration;
            WhiteBalanceTemperature = whiteBalanceTemperature;
            WhiteBalanceTint = whiteBalanceTint;
            Contrast = contrast;
            Saturation = saturation;
            Lift = lift;
            SplitShadows = splitShadows;
            SplitHighlights = splitHighlights;
            SplitBalance = splitBalance;
            BloomIntensity = bloomIntensity;
            BloomThreshold = bloomThreshold;
            BloomTint = bloomTint;
            LensDistortion = lensDistortion;
            LensDistortionScale = lensDistortionScale;
            Tonemapping = tonemapping;
        }
    }

    /// <summary>The values one mode writes. Read by both the apply and the match, never duplicated.</summary>
    private readonly struct BasisCameraModePreset
    {
        // Placement: the three values a settings file cannot carry, because saving them would have
        // a camera fly out of your hand the moment it spawned. These are what a restore re-arms.
        public readonly CameraPinSpace Pin;
        public readonly BasisCameraPositionModifier Position;
        public readonly BasisCameraRotationModifier Rotation;
        public readonly BasisCameraEffectModifier[] Effects;

        // Everything below is persisted in its own right, so a restore must leave it alone and let
        // the file speak. Only an explicit mode selection writes these.
        public readonly bool AutoLevel;
        public readonly bool VrStabilisation;
        public readonly bool Capture360;
        public readonly bool AutoFocusSubject;
        public readonly bool AnchorToBody;
        public readonly Vector3 FollowOffset;
        public readonly float Fov;

        /// <summary>
        /// Whether depth of field runs, kept separate from the blur style below it. The camera
        /// stores the two independently — a style of Bokeh with the effect switched off is the
        /// shipped default — and folding them into one value would make picking a mode either
        /// switch the effect on or forget which style the user had.
        /// </summary>
        public readonly bool DoFEnabled;

        /// <summary>1 = Gaussian, 2 = Bokeh.</summary>
        public readonly int DoFStyle;
        public readonly float Aperture;
        public readonly float FocalLength;
        public readonly float MotionBlur;

        /// <summary>
        /// The physical camera this mode hands you. Compared as well as written, and the only value
        /// here that survives a camera with no volume profile — which is what tells two film bodies
        /// apart in a test fixture where every optical compare is skipped.
        /// </summary>
        public readonly BasisCameraBodyKind Body;

        /// <summary>The picture this mode makes, or an inactive look on a mode with no opinion.</summary>
        public readonly BasisCameraLook Look;

        public BasisCameraModePreset(
            CameraPinSpace pin,
            BasisCameraPositionModifier position,
            BasisCameraRotationModifier rotation,
            BasisCameraEffectModifier[] effects,
            bool autoLevel,
            bool vrStabilisation,
            bool capture360,
            bool autoFocusSubject,
            bool anchorToBody,
            Vector3 followOffset,
            float fov,
            bool dofEnabled,
            int dofStyle,
            float aperture,
            float focalLength,
            float motionBlur,
            BasisCameraBodyKind body = BasisCameraBodyKind.Digital,
            BasisCameraLook look = default)
        {
            Pin = pin;
            Position = position;
            Rotation = rotation;
            Effects = effects;
            AutoLevel = autoLevel;
            VrStabilisation = vrStabilisation;
            Capture360 = capture360;
            AutoFocusSubject = autoFocusSubject;
            AnchorToBody = anchorToBody;
            FollowOffset = followOffset;
            Fov = fov;
            DoFEnabled = dofEnabled;
            DoFStyle = dofStyle;
            Aperture = aperture;
            FocalLength = focalLength;
            MotionBlur = motionBlur;
            Body = body;
            Look = look;
        }

        /// <summary>Whether the fitted slots need somebody to film, and so own the subject settings.</summary>
        public bool DrivesSubject =>
            BasisCameraModifiers.NeedsSubject(Position) || BasisCameraModifiers.NeedsSubject(Rotation);

        /// <summary>
        /// The stack this mode runs, built on top of whatever the camera already carries.
        ///
        /// <para>A preset owns the two slots and the effects list — the placement a settings file
        /// deliberately never stores — but it only writes the framing when it actually films
        /// somebody. A mode that greys the position section out has no business resetting the
        /// values in it: the user's framing is still theirs when they come back to Follow Me.</para>
        /// </summary>
        public BasisCameraModifierStack BuildStack(BasisCameraModifierStack current)
        {
            BasisCameraModifierStack stack = current != null
                ? current.Clone()
                : new BasisCameraModifierStack();

            stack.positionModifier = Position;
            stack.rotationModifier = Rotation;

            bool needsSubject = BasisCameraModifiers.NeedsSubject(Position) || BasisCameraModifiers.NeedsSubject(Rotation);
            for (int Index = 0; !needsSubject && Effects != null && Index < Effects.Length; Index++)
            {
                needsSubject = BasisCameraModifiers.NeedsSubject(Effects[Index]);
            }
            if (needsSubject && !stack.ResolvesSubject)
            {
                stack.subject.modifier = BasisCameraSubjectModifier.FollowPlayer;
            }

            stack.ClearEffects();
            if (Effects != null)
            {
                for (int Index = 0; Index < Effects.Length; Index++)
                {
                    stack.AddEffect(Effects[Index]);
                }
            }

            if (DrivesSubject)
            {
                stack.follow.positionOffset = FollowOffset;
                stack.framing.directionOffset = FollowOffset;
            }
            return stack;
        }

        public bool EffectsMatch(BasisCameraModifierStack stack)
        {
            int wanted = Effects?.Length ?? 0;
            if (stack == null || stack.EffectCount != wanted) return false;

            for (int Index = 0; Index < wanted; Index++)
            {
                if (!stack.HasEffect(Effects[Index])) return false;
            }
            return true;
        }
    }

    private static readonly BasisCameraEffectModifier[] NoEffects = new BasisCameraEffectModifier[0];

    /// <summary>
    /// The framing every mode that does not film anybody carries. Only written where follow is
    /// armed, so for most of the table it is the value the section keeps rather than one it sets —
    /// but it has to be the same value in each, or two modes would differ by a number neither uses.
    /// </summary>
    private static readonly Vector3 DefaultFollowOffset = new Vector3(0.5f, 0f, 1.4f);

    // The four grain textures this table picks between, named for what they are rather than for
    // URP's numbering — the difference between a fast negative and a sensor at high gain is the
    // size of the texture, and a bare 8 in a preset says nothing about which is which.
    private const int FineGrain = (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Medium3;
    private const int MediumGrain = (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Medium1;
    private const int LargeGrain = (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large01;
    private const int CoarseGrain = (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large02;

    /// <summary>
    /// <c>TonemappingMode.Neutral</c>, as the int the preset table stores. Every camera kind grades
    /// its still with it rather than with ACES: the contrast belongs to the film or the tape, and a
    /// filmic curve on top of one only flattens what the look just spent its budget saying.
    /// </summary>
    private const int NeutralTonemapping = (int)UnityEngine.Rendering.Universal.TonemappingMode.Neutral;

    private static readonly BasisCameraEffectModifier[] CinematicEffects =
    {
        BasisCameraEffectModifier.LookAhead,
        BasisCameraEffectModifier.Shake,
    };

    /// <summary>
    /// Photo is the camera as it has always behaved, so every number here is the shipped default —
    /// including depth of field being off with Bokeh waiting behind it. Picking Photo after any
    /// other mode has to be a clean return, not a new look, and a fresh install has to already be
    /// in it rather than one edit away.
    /// </summary>
    private static readonly BasisCameraModePreset PhotoPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.HandHeld,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 40f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 2.8f,
        focalLength: 50f,
        motionBlur: 0f);

    /// <summary>
    /// A puck parked in the world and flown by hand. Wide enough to hold a room, levelled and
    /// stabilised because the shot is watched live, and deliberately deep-focus: a stream where
    /// half the room is a blur is a worse stream, however good the still would look.
    /// </summary>
    private static readonly BasisCameraModePreset FlyingPuckPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.WorldSpace,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: true,
        vrStabilisation: true,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 55f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 5.6f,
        focalLength: 35f,
        motionBlur: 0f);

    /// <summary>
    /// Flies itself and keeps you sharp. This is the first mode to switch depth of field on,
    /// because it is the first one that knows what the subject is: auto focus tracks you, so the
    /// aperture can stay open enough to lift you off the background without the focus hunting.
    /// </summary>
    private static readonly BasisCameraModePreset FollowMePreset = new BasisCameraModePreset(
        pin: CameraPinSpace.WorldSpace,
        position: BasisCameraPositionModifier.FollowSubject,
        rotation: BasisCameraRotationModifier.LookAtSubject,
        effects: NoEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: true,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 45f,
        dofEnabled: true,
        dofStyle: 2,
        aperture: 2.8f,
        focalLength: 50f,
        motionBlur: 0f);

    /// <summary>
    /// The shot rig drives the camera. A longer lens and a wider aperture give the shallow,
    /// compressed look the dolly and orbit moves are there to show off, and a little motion blur
    /// stops a slow push reading as a slideshow.
    /// </summary>
    private static readonly BasisCameraModePreset CinematicPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.WorldSpace,
        position: BasisCameraPositionModifier.FollowSubject,
        rotation: BasisCameraRotationModifier.Compose,
        effects: CinematicEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: new Vector3(1.2f, 0.4f, 3f),
        fov: 35f,
        dofEnabled: true,
        dofStyle: 2,
        aperture: 2.0f,
        focalLength: 85f,
        motionBlur: 0.35f);

    /// <summary>
    /// A single-use 35mm. Warm, contrasty and grainy because that is what a one-speed film pushed
    /// through a plastic lens and a drugstore scanner comes back as — and deep-focus at f/8 because
    /// the lens does not focus at all. Neutral tonemapping rather than ACES: the contrast in a film
    /// print is the print's, and grading it filmically on top of that only greys it back down.
    /// </summary>
    private static readonly BasisCameraModePreset DisposablePreset = new BasisCameraModePreset(
        pin: CameraPinSpace.HandHeld,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 45f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 8f,
        focalLength: 32f,
        motionBlur: 0f,
        body: BasisCameraBodyKind.Disposable,
        look: new BasisCameraLook(
            // ISO 400/800 consumer negative, enlarged from a 35mm frame by a machine scanner: the
            // grain is big enough to count, and it lives in the midtones and shadows rather than in
            // the sky, which is what Large01 at a high response gives and Thin1 cannot.
            filmGrain: 0.55f,
            grainType: LargeGrain,
            grainResponse: 0.85f,

            // A single moulded plastic element has a round image circle and simply stops delivering
            // light at the edge of it, so the falloff is a circle rather than the frame's shape —
            // and it darkens toward the warm brown of the light still getting through, not to black.
            vignette: 0.45f,
            vignetteSmoothness: 0.32f,
            vignetteColour: new Color(0.09f, 0.05f, 0.03f),
            vignetteRounded: true,

            chromaticAberration: 0.32f,

            // Daylight film under whatever light was actually there. Gold 400 and Superia are both
            // built to flatter skin, which reads as warm with a shade of magenta.
            whiteBalanceTemperature: 20f,
            whiteBalanceTint: -5f,
            contrast: 14f,
            saturation: 16f,

            // Lifted blacks. A machine print off a negative has no true black in it, and this is
            // most of what separates the look from a contrasty digital photo with grain on top.
            lift: 0.055f,

            // The colour signature of the stock: gold in the highlights, teal in the shade. Held
            // near grey because the effect tints toward these — the distance from grey IS the
            // strength — and weighted to the highlight end, which is where the cast is seen.
            splitShadows: new Color(0.40f, 0.53f, 0.58f),
            splitHighlights: new Color(0.60f, 0.53f, 0.40f),
            splitBalance: 12f,

            // Halation, not bloom: light that got through the emulsion, bounced off the base behind
            // it and exposed the grains a second time. The anti-halation layer stops that least at
            // the red end, so every bulb on a negative wears an orange ring.
            bloomIntensity: 0.75f,
            bloomThreshold: 0.85f,
            bloomTint: new Color(1.00f, 0.42f, 0.26f),

            lensDistortion: 0.06f,
            lensDistortionScale: 1f,
            tonemapping: NeutralTonemapping));

    /// <summary>
    /// Instant film: the opposite of the disposable in every direction. Low contrast and milky
    /// rather than punchy, a wide soft bloom instead of grain, and a magenta-warm cast — the look of
    /// a chemistry that never quite reaches black.
    /// </summary>
    private static readonly BasisCameraModePreset InstantPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.HandHeld,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 42f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 11f,
        focalLength: 40f,
        motionBlur: 0f,
        body: BasisCameraBodyKind.Instant,
        look: new BasisCameraLook(
            // Instant film is a dye layer developed against a sheet, not silver grains in gelatin,
            // so its texture is a fine mottle rather than the coarse pepper of a fast negative.
            filmGrain: 0.22f,
            grainType: MediumGrain,
            grainResponse: 0.7f,

            vignette: 0.5f,
            vignetteSmoothness: 0.55f,
            vignetteColour: new Color(0.10f, 0.09f, 0.11f),
            vignetteRounded: true,

            chromaticAberration: 0.12f,
            whiteBalanceTemperature: 12f,
            whiteBalanceTint: 9f,

            // Pastel and soft. The contrast comes down further than any other body here because an
            // instant print has a genuinely short tonal range — it is a one-shot chemistry with no
            // negative behind it to hold the ends.
            contrast: -24f,
            saturation: -14f,

            // The single most important number in this table. A 600 print has no true black in it
            // at all — the shadows sit at a milky grey — and every other value here is decoration
            // on top of that one fact.
            lift: 0.115f,

            // Pink in the highlights, green in the shade: the shift every 600 pack has and the one
            // thing that tells an instant print from a warm faded photograph.
            splitShadows: new Color(0.42f, 0.55f, 0.48f),
            splitHighlights: new Color(0.62f, 0.50f, 0.52f),
            splitBalance: -10f,

            // Wide and creamy rather than a ring: an instant print glows because the dyes scatter
            // through the layers, which spreads light instead of reflecting it back as a hot edge.
            bloomIntensity: 1.25f,
            bloomThreshold: 0.62f,
            bloomTint: new Color(1.00f, 0.88f, 0.80f),

            lensDistortion: 0.02f,
            lensDistortionScale: 1f,
            tonemapping: NeutralTonemapping));

    /// <summary>
    /// Tape. The chromatic aberration is doing most of the work here — colour smearing sideways off
    /// an edge is the single thing that says "recorded" rather than "photographed" — and the motion
    /// blur is the rest of it, because a tape frame was a field-interlaced smear of the one before.
    /// </summary>
    private static readonly BasisCameraModePreset CamcorderPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.HandHeld,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: false,
        vrStabilisation: false,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 52f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 8f,
        focalLength: 28f,
        motionBlur: 0.5f,
        body: BasisCameraBodyKind.Camcorder,
        look: new BasisCameraLook(
            // Sensor noise rather than grain — finer than a negative, and spread evenly rather than
            // hiding in the shadows, because it comes from the amplifier and not from the picture.
            filmGrain: 0.34f,
            grainType: FineGrain,
            grainResponse: 0.35f,

            // A rectangular sensor behind a corrected zoom falls off with the frame, not in a
            // circle — the one body here that does.
            vignette: 0.26f,
            vignetteSmoothness: 0.45f,
            vignetteColour: new Color(0.05f, 0.06f, 0.08f),
            vignetteRounded: false,

            chromaticAberration: 0.6f,
            whiteBalanceTemperature: -8f,
            whiteBalanceTint: 4f,
            contrast: -6f,
            saturation: -22f,

            // Tape blacks are notoriously grey: the signal never reaches the bottom of its range
            // and every generation of copying lifts it further.
            lift: 0.07f,

            splitShadows: new Color(0.45f, 0.50f, 0.58f),
            splitHighlights: new Color(0.56f, 0.54f, 0.47f),
            splitBalance: 0f,

            bloomIntensity: 0.9f,
            bloomThreshold: 0.55f,
            bloomTint: new Color(0.86f, 0.92f, 1.00f),

            lensDistortion: 0.04f,
            lensDistortionScale: 1f,
            tonemapping: NeutralTonemapping));

    /// <summary>
    /// A ceiling camera. Very wide and barrel-distorted so the room bends at the edges, crushed
    /// almost to grey rather than all the way — a security picture is not black and white, it is a
    /// colour picture of a room lit by one bad lamp — and levelled, because nobody is holding it.
    /// </summary>
    private static readonly BasisCameraModePreset SecurityPreset = new BasisCameraModePreset(
        pin: CameraPinSpace.HandHeld,
        position: BasisCameraPositionModifier.FreeFly,
        rotation: BasisCameraRotationModifier.Hold,
        effects: NoEffects,
        autoLevel: true,
        vrStabilisation: true,
        capture360: false,
        autoFocusSubject: false,
        anchorToBody: true,
        followOffset: DefaultFollowOffset,
        fov: 82f,
        dofEnabled: false,
        dofStyle: 2,
        aperture: 11f,
        focalLength: 18f,
        motionBlur: 0.15f,
        body: BasisCameraBodyKind.Security,
        look: new BasisCameraLook(
            // A small sensor run at whatever gain the room needs. The coarsest texture in the table,
            // and evenly spread, because it is amplification rather than emulsion.
            filmGrain: 0.48f,
            grainType: CoarseGrain,
            grainResponse: 0.25f,

            vignette: 0.55f,
            vignetteSmoothness: 0.3f,
            vignetteColour: new Color(0.04f, 0.05f, 0.05f),
            vignetteRounded: true,

            chromaticAberration: 0.04f,
            whiteBalanceTemperature: -4f,
            whiteBalanceTint: 0f,
            contrast: 22f,

            // Crushed nearly to grey, not all the way. A security picture is a colour picture of a
            // room lit by one bad lamp, which is a different thing from a black and white one.
            saturation: -82f,
            lift: 0.04f,

            splitShadows: new Color(0.47f, 0.50f, 0.54f),
            splitHighlights: new Color(0.53f, 0.52f, 0.48f),
            splitBalance: 0f,

            bloomIntensity: 0.35f,
            bloomThreshold: 0.95f,
            bloomTint: new Color(0.90f, 0.95f, 1.00f),

            lensDistortion: 0.3f,
            lensDistortionScale: 1f,
            tonemapping: NeutralTonemapping));

    // Tolerances. Every one of these values reaches the camera through a slider whose display is
    // rounded, so an exact compare would report Custom for a value the user never touched.
    private const float FovTolerance = 0.5f;
    private const float ApertureTolerance = 0.02f;
    private const float FocalLengthTolerance = 0.5f;
    private const float MotionBlurTolerance = 0.005f;
    private const float OffsetTolerance = 0.01f;

    // The look values come off percentage and -100..100 sliders, so a tenth is finer than anything
    // the panel can express and coarse enough to survive the round trip through one.
    private const float LookTolerance = 0.1f;

    /// <summary>One 8-bit channel. A colour that survived a trip through a hex field cannot be closer.</summary>
    private const float ColourTolerance = 1f / 255f;

    private static bool TryGetPreset(BasisCameraMode mode, out BasisCameraModePreset preset)
    {
        switch (mode)
        {
            case BasisCameraMode.Photo: preset = PhotoPreset; return true;
            case BasisCameraMode.FlyingPuck: preset = FlyingPuckPreset; return true;
            case BasisCameraMode.FollowMe: preset = FollowMePreset; return true;
            case BasisCameraMode.Cinematic: preset = CinematicPreset; return true;
            case BasisCameraMode.Disposable: preset = DisposablePreset; return true;
            case BasisCameraMode.Instant: preset = InstantPreset; return true;
            case BasisCameraMode.Camcorder: preset = CamcorderPreset; return true;
            case BasisCameraMode.Security: preset = SecurityPreset; return true;
            default: preset = default; return false;
        }
    }

    /// <summary>
    /// Puts the camera into a mode. Custom is a state, not a preset — selecting it changes nothing,
    /// which is the point: it means "keep what I have".
    /// </summary>
    public void ApplyCameraMode(BasisCameraMode mode)
    {
        if (!TryGetPreset(mode, out BasisCameraModePreset preset))
        {
            CameraMode = BasisCameraMode.Custom;
            return;
        }

        ApplyPresetPlacement(preset);

        useAutoLeveling = preset.AutoLevel;
        useVRHandheldSmoothing = preset.VrStabilisation;
        capture360Enabled = preset.Capture360;

        // Only a mode that actually runs follow owns follow's settings. The others mark the whole
        // Follow section as doing nothing, and a mode that greys a section out has no business
        // resetting the values in it — the user's framing is still theirs when they come back.
        if (preset.DrivesSubject)
        {
            autoFocusFollowSubject = preset.AutoFocusSubject;
            subjectSettings.anchorToBody = preset.AnchorToBody;
        }

        // Before the optics, because a body owns the frame the camera shoots and the optics are
        // framed against it — and with a fresh load, because choosing a kind from the picker is
        // being handed that camera rather than picking the one you had back up.
        SetBody(preset.Body, freshLoad: true);

        SetFieldOfView(preset.Fov);
        ApplyPresetOptics(preset);
        ApplyPresetLook(preset.Look);
        SyncPropUiAfterModeChange();

        CameraMode = mode;
        ComparedMode = mode;
    }

    /// <summary>
    /// Arms the mode's placement: whether follow and the shot rig are running, and where the camera
    /// is pinned. This is the half a settings file cannot carry, so it is also the whole of what a
    /// restore re-runs.
    ///
    /// <para>Follow and the shot rig both claim world space on the way in and both hand it back on
    /// the way out, so whichever is unwanted has to be switched off before the wanted one is armed
    /// — otherwise the loser's hand-back fires last and drags the camera out of the pin it was just
    /// given. The explicit pin write afterwards then settles the modes that arm neither.</para>
    /// </summary>
    private void ApplyPresetPlacement(BasisCameraModePreset preset) =>
        ApplyPlacement(preset.Pin, preset.BuildStack(Modifiers));

    /// <summary>
    /// The placement write itself — the ordering above is subtle enough that a second copy of it
    /// would be a second chance to get it wrong.
    /// </summary>
    internal void ApplyPlacement(CameraPinSpace pin, BasisCameraModifierStack stack)
    {
        if (stack != null)
        {
            ApplyModifierStack(stack);
        }

        // The pin is an int off disk, so it can name an anchor this build does not have.
        if (pin < CameraPinSpace.HandHeld || pin > CameraPinSpace.Attached)
        {
            pin = CameraPinSpace.HandHeld;
        }

        // It carries the anchor but never what that anchor was riding: the target is a live
        // reference to something in the world the mode was saved in, and there is nothing to
        // resolve it against here. Restoring Attached with nothing attached would offer an anchor
        // the camera has no way to be on, so it lands on the world instead.
        if (pin == CameraPinSpace.Attached && AnchorKind == CameraAnchorKind.None)
        {
            pin = CameraPinSpace.WorldSpace;
        }

        SetAnchorSpace(pin);
    }

    /// <summary>
    /// Writes the lens and post-processing half of a preset. Split out because it is the half that
    /// needs a live volume profile: on a camera whose overrides have not been created yet there is
    /// nothing to write, and skipping is correct — <see cref="MatchesCameraMode"/> skips the same
    /// values, so a camera missing its profile is not reported as Custom for want of one.
    /// </summary>
    private void ApplyPresetOptics(BasisCameraModePreset preset)
    {
        var depthOfField = MetaData?.depthOfField;
        if (depthOfField != null)
        {
            depthOfField.mode.overrideState = true;
            depthOfField.mode.value = (UnityEngine.Rendering.Universal.DepthOfFieldMode)Mathf.Clamp(preset.DoFStyle, 1, 2);
            depthOfField.active = preset.DoFEnabled;

            // Written even where the effect is off, so switching it on later gives the look the
            // mode intended rather than whatever the last mode happened to leave behind.
            depthOfField.aperture.overrideState = true;
            depthOfField.aperture.value = preset.Aperture;
            depthOfField.focalLength.overrideState = true;
            depthOfField.focalLength.value = preset.FocalLength;

            BasisDOFInteractionHandler?.SetDoFState(preset.DoFEnabled);
        }

        var motionBlur = MetaData?.motionBlur;
        if (motionBlur != null)
        {
            motionBlur.intensity.overrideState = true;
            motionBlur.intensity.value = preset.MotionBlur;
            // URP only runs the pass above zero, so the strength doubles as the on/off switch.
            motionBlur.active = preset.MotionBlur > 0f;
        }

    }

    /// <summary>
    /// Writes the picture half of a preset: grain, cast, contrast and the rest of the
    /// post-processing a camera kind is made of.
    ///
    /// <para>Routed through the UI's own setters rather than written at the overrides directly,
    /// because every one of those values needs more than a number — a <c>VolumeParameter</c> is
    /// ignored until its override state is set, film grain needs a lookup chosen or nothing renders
    /// at any intensity, and half of them own an <c>active</c> flag that decides whether the pass
    /// runs at all. Duplicating that here would be a second set of rules for the same effects, free
    /// to disagree with the sliders that share them.</para>
    ///
    /// <para>Skipped on a camera whose UI has no camera yet, which is every edit-mode test.
    /// <see cref="MatchesLook"/> skips the same values on the same camera — there are no overrides
    /// to read there — so a fixture without a volume profile is not reported as Custom for want of
    /// one.</para>
    /// </summary>
    private void ApplyPresetLook(BasisCameraLook look)
    {
        if (!look.Active)
        {
            ResetFilmGrading();
            return;
        }

        // The still's grade is a field on the camera rather than a volume override, so it is
        // written whether or not there is a UI to route the rest through — and it has to be, or a
        // camera with no volume profile would apply a kind and immediately not match it.
        SetCaptureTonemapping(look.Tonemapping);

        if (HandHeld == null || HandHeld.HHC == null) return;

        // Shape before strength throughout, the order ApplySettings uses: the strength owns whether
        // the effect runs, so the shot is never drawn for a frame at the new strength with the last
        // mode's shape.
        HandHeld.ChangeVignetteSmoothness(look.VignetteSmoothness);
        HandHeld.ChangeVignetteColour(look.VignetteColour);
        HandHeld.ChangeVignetteRounded(look.VignetteRounded);
        HandHeld.ChangeVignette(look.Vignette);
        HandHeld.ChangeFilmGrainType(look.GrainType);
        HandHeld.ChangeFilmGrainResponse(look.GrainResponse);
        HandHeld.ChangeFilmGrain(look.FilmGrain);
        HandHeld.ChangeChromaticAberration(look.ChromaticAberration);
        HandHeld.ChangeWhiteBalanceTemperature(look.WhiteBalanceTemperature);
        HandHeld.ChangeWhiteBalanceTint(look.WhiteBalanceTint);
        HandHeld.ChangeContrast(look.Contrast);
        HandHeld.ChangeSaturation(look.Saturation);
        HandHeld.ChangeSplitToning(look.SplitShadows, look.SplitHighlights);
        HandHeld.ChangeSplitToningBalance(look.SplitBalance);
        HandHeld.ChangeFilmLift(look.Lift);
        HandHeld.ChangeLensDistortionScale(look.LensDistortionScale);
        HandHeld.ChangeLensDistortion(look.LensDistortion);

        HandHeld.ChangeBloomIntensity(look.BloomIntensity);
        HandHeld.ChangeBloomThreshold(look.BloomThreshold);
        HandHeld.ChangeBloomTint(look.BloomTint);
    }

    /// <summary>
    /// Hands the picture back to the shipped defaults, for a mode that has no look of its own.
    ///
    /// <para>Without this a camera kind would be a one-way door: the four placement modes write no
    /// grading, so picking Photo after a disposable would leave the grain, the halation, the split
    /// toning and the lifted blacks exactly where the disposable put them, and the panel would
    /// cheerfully call the result Photo. "Photo is the camera as it ships" has to be true of the
    /// picture as well as of where the camera sits.</para>
    ///
    /// <para>The values come from the settings constructor rather than being typed out again. That
    /// constructor <em>is</em> the definition of what ships, so a second copy of its numbers here
    /// would be a second thing to keep in step with it — and the one that got forgotten.</para>
    ///
    /// <para>Written but never compared, which is the same bargain <see cref="ApplyPresetOptics"/>
    /// already makes with the depth of field values it writes while the effect is off: a mode with
    /// no opinion about grain must not be knocked out of itself by somebody adding grain.</para>
    /// </summary>
    private void ResetFilmGrading()
    {
        if (HandHeld == null || HandHeld.HHC == null) return;

        BasisHandHeldCameraUI.CameraSettings shipped = new BasisHandHeldCameraUI.CameraSettings();

        HandHeld.ChangeVignetteSmoothness(shipped.vignetteSmoothness);
        HandHeld.ChangeVignetteColour(shipped.vignetteColour);
        HandHeld.ChangeVignetteRounded(shipped.vignetteRounded);
        HandHeld.ChangeVignette(shipped.vignette);
        HandHeld.ChangeFilmGrainType(shipped.filmGrainType);
        HandHeld.ChangeFilmGrainResponse(shipped.filmGrainResponse);
        HandHeld.ChangeFilmGrain(shipped.filmGrain);
        HandHeld.ChangeChromaticAberration(shipped.chromaticAberration);
        HandHeld.ChangeWhiteBalanceTemperature(shipped.whiteBalanceTemperature);
        HandHeld.ChangeWhiteBalanceTint(shipped.whiteBalanceTint);
        HandHeld.ChangeContrast(shipped.contrast);
        HandHeld.ChangeSaturation(shipped.saturation);
        HandHeld.ChangeSplitToning(shipped.splitToningShadows, shipped.splitToningHighlights);
        HandHeld.ChangeSplitToningBalance(shipped.splitToningBalance);
        HandHeld.ChangeFilmLift(shipped.filmLift);
        HandHeld.ChangeLensDistortionScale(shipped.lensDistortionScale);
        HandHeld.ChangeLensDistortion(shipped.lensDistortion);
        HandHeld.ChangeBloomIntensity(shipped.bloomIntensity);
        HandHeld.ChangeBloomThreshold(shipped.bloomThreshold);
        HandHeld.ChangeBloomTint(shipped.bloomTint);
        SetCaptureTonemapping(shipped.captureTonemapping);
    }

    /// <summary>Adds every value the look wrote that the live camera is no longer holding.</summary>
    private void CompareLook(BasisCameraLook look, ref ulong fields)
    {
        if (!look.Active) return;

        // Read the way each effect is written: the ones that own an active flag are off at zero, so
        // a switched-off effect has to read as zero rather than as whatever value it kept.
        var grain = MetaData?.filmGrain;
        if (grain != null)
        {
            if (!NearLook(Strength(grain.active, grain.intensity.value), look.FilmGrain))
                fields |= Bit(BasisCameraPresetField.FilmGrain);

            // Shape only where the grain is actually running, the same rule the vignette follows:
            // with the effect off the panel hides its texture and falloff entirely.
            if (look.FilmGrain > 0f)
            {
                if ((int)grain.type.value != look.GrainType) fields |= Bit(BasisCameraPresetField.GrainType);
                if (!NearLook(grain.response.value, look.GrainResponse)) fields |= Bit(BasisCameraPresetField.GrainResponse);
            }
        }

        var vignette = MetaData?.vignette;
        if (vignette != null)
        {
            if (!NearLook(Strength(vignette.active, vignette.intensity.value), look.Vignette))
                fields |= Bit(BasisCameraPresetField.Vignette);

            // Shape is only compared where the effect it shapes is running. With the vignette off
            // the panel has nothing on screen that would explain a smoothness mismatch.
            if (look.Vignette > 0f)
            {
                if (!NearLook(vignette.smoothness.value, look.VignetteSmoothness))
                    fields |= Bit(BasisCameraPresetField.VignetteSmoothness);
                if (!NearColour(vignette.color.value, look.VignetteColour))
                    fields |= Bit(BasisCameraPresetField.VignetteColour);
                if (vignette.rounded.value != look.VignetteRounded)
                    fields |= Bit(BasisCameraPresetField.VignetteRounded);
            }
        }

        var chromatic = MetaData?.chromaticAberration;
        if (chromatic != null &&
            !NearLook(Strength(chromatic.active, chromatic.intensity.value), look.ChromaticAberration))
        {
            fields |= Bit(BasisCameraPresetField.ChromaticAberration);
        }

        var whiteBalance = MetaData?.whiteBalance;
        if (whiteBalance != null)
        {
            if (!NearLook(Strength(whiteBalance.active, whiteBalance.temperature.value), look.WhiteBalanceTemperature))
                fields |= Bit(BasisCameraPresetField.WhiteBalanceTemperature);
            if (!NearLook(Strength(whiteBalance.active, whiteBalance.tint.value), look.WhiteBalanceTint))
                fields |= Bit(BasisCameraPresetField.WhiteBalanceTint);
        }

        var colour = MetaData?.colorAdjustments;
        if (colour != null)
        {
            if (!NearLook(colour.contrast.value, look.Contrast)) fields |= Bit(BasisCameraPresetField.Contrast);
            if (!NearLook(colour.saturation.value, look.Saturation)) fields |= Bit(BasisCameraPresetField.Saturation);
        }

        var bloom = MetaData?.bloom;
        if (bloom != null)
        {
            if (!NearLook(bloom.intensity.value, look.BloomIntensity)) fields |= Bit(BasisCameraPresetField.BloomIntensity);
            if (!NearLook(bloom.threshold.value, look.BloomThreshold)) fields |= Bit(BasisCameraPresetField.BloomThreshold);
            if (look.BloomIntensity > 0f && !NearColour(bloom.tint.value, look.BloomTint))
                fields |= Bit(BasisCameraPresetField.BloomTint);
        }

        var splitToning = MetaData?.splitToning;
        if (splitToning != null)
        {
            if (!NearColour(splitToning.shadows.value, look.SplitShadows)) fields |= Bit(BasisCameraPresetField.SplitShadows);
            if (!NearColour(splitToning.highlights.value, look.SplitHighlights))
                fields |= Bit(BasisCameraPresetField.SplitHighlights);
            if (!NearLook(splitToning.balance.value, look.SplitBalance)) fields |= Bit(BasisCameraPresetField.SplitBalance);
        }

        var lift = MetaData?.liftGammaGain;
        if (lift != null && !NearLook(lift.lift.value.w, look.Lift)) fields |= Bit(BasisCameraPresetField.FilmLift);

        var distortion = MetaData?.lensDistortion;
        if (distortion != null)
        {
            if (!NearLook(Strength(distortion.active, distortion.intensity.value), look.LensDistortion))
                fields |= Bit(BasisCameraPresetField.LensDistortion);
            if (look.LensDistortion != 0f && !NearLook(distortion.scale.value, look.LensDistortionScale))
                fields |= Bit(BasisCameraPresetField.LensDistortionScale);
        }

        if ((int)CaptureTonemapping != look.Tonemapping) fields |= Bit(BasisCameraPresetField.Tonemapping);
    }

    /// <summary>An effect's strength as the panel means it: zero whenever the pass is not running.</summary>
    private static float Strength(bool active, float value) => active ? value : 0f;

    private static bool NearLook(float live, float wanted) => Mathf.Abs(live - wanted) <= LookTolerance;

    /// <summary>
    /// Colours get a tighter tolerance than the numbers around them: they arrive through three
    /// 0..1 channels rather than through a percentage slider, so a tenth would be a fifth of the
    /// whole range and two visibly different tints would compare as the same one.
    /// </summary>
    private static bool NearColour(Color live, Color wanted) =>
        Mathf.Abs(live.r - wanted.r) <= ColourTolerance
        && Mathf.Abs(live.g - wanted.g) <= ColourTolerance
        && Mathf.Abs(live.b - wanted.b) <= ColourTolerance;

    /// <summary>
    /// Pushes what a preset just wrote back into the prop's own HUD.
    ///
    /// <para>⚠️ Not cosmetic. Saving harvests the field of view and the depth aperture <em>from the
    /// HUD sliders</em>, not from the camera — so a preset that writes the camera and leaves the
    /// sliders behind is saved with the old numbers, and the mode quietly degrades to Custom the
    /// next time the file is loaded. <see cref="BasisHandHeldCameraUI.SyncPropControlsFromState"/>
    /// re-seeds every shared control from the live camera, which is exactly that repair.</para>
    ///
    /// <para><see cref="BasisHandHeldCameraUI.SetDepthMode"/> is re-run alongside it because the
    /// HUD's focus cursor and depth sliders show or hide on whether depth of field is running, and
    /// it derives that from the live effect. Both are skipped when the UI has no camera yet, which
    /// is every edit-mode test.</para>
    /// </summary>
    private void SyncPropUiAfterModeChange()
    {
        if (HandHeld == null || HandHeld.HHC == null) return;

        HandHeld.SyncPropControlsFromState();
        HandHeld.SetDepthMode(HandHeld.currentDepthMode);
    }

    /// <summary>True while every value the mode writes still holds on the live camera.</summary>
    public bool MatchesCameraMode(BasisCameraMode mode) => CompareToMode(mode).Matches;

    /// <summary>
    /// Every value of <paramref name="mode"/> the live camera is no longer holding.
    ///
    /// <para>The only comparison in the file. <see cref="MatchesCameraMode"/> is this one reading
    /// empty, and the settings readout colours the rows it names — so what drops the label to
    /// Custom and what the readout calls changed cannot come to mean two different things.</para>
    /// </summary>
    public BasisCameraPresetDiff CompareToMode(BasisCameraMode mode)
    {
        if (!TryGetPreset(mode, out BasisCameraModePreset preset)) return default;

        ulong fields = 0;

        // The body is compared without a tolerance, because it is the one thing here that is not a
        // number: you either are holding a disposable or you are not, and no amount of matching its
        // grain on a digital camera makes it one.
        if (Body != preset.Body) fields |= Bit(BasisCameraPresetField.Body);

        // PinSpace is deliberately not compared. It is where the camera happens to be, not how it
        // is configured — grabbing a flying puck back out of the air, or letting go of a photo
        // camera, must not read as "you have left the mode".
        if (Modifiers.positionModifier != preset.Position) fields |= Bit(BasisCameraPresetField.PositionModifier);
        if (Modifiers.rotationModifier != preset.Rotation) fields |= Bit(BasisCameraPresetField.RotationModifier);
        if (!preset.EffectsMatch(Modifiers)) fields |= Bit(BasisCameraPresetField.Effects);
        if (useAutoLeveling != preset.AutoLevel) fields |= Bit(BasisCameraPresetField.AutoLevel);
        if (useVRHandheldSmoothing != preset.VrStabilisation) fields |= Bit(BasisCameraPresetField.VrStabilisation);
        if (capture360Enabled != preset.Capture360) fields |= Bit(BasisCameraPresetField.Capture360);

        // Compared only where they are written. A mode that leaves the follow settings alone must
        // not be knocked out of itself by them, or editing a section it greys out would drop the
        // camera to Custom for a change that had no effect on the shot.
        if (preset.DrivesSubject)
        {
            if (autoFocusFollowSubject != preset.AutoFocusSubject) fields |= Bit(BasisCameraPresetField.AutoFocusSubject);
            if (subjectSettings.anchorToBody != preset.AnchorToBody) fields |= Bit(BasisCameraPresetField.AnchorToBody);
            if (Vector3.Distance(Modifiers.follow.positionOffset, preset.FollowOffset) > OffsetTolerance)
                fields |= Bit(BasisCameraPresetField.FollowOffset);
        }

        if (captureCamera != null &&
            Mathf.Abs(captureCamera.fieldOfView - preset.Fov) > FovTolerance)
        {
            fields |= Bit(BasisCameraPresetField.FieldOfView);
        }

        var depthOfField = MetaData?.depthOfField;
        if (depthOfField != null)
        {
            if (depthOfField.active != preset.DoFEnabled) fields |= Bit(BasisCameraPresetField.DepthOfField);

            // With the effect off the panel hides the style, aperture and focal length entirely, so
            // their values are whatever was left behind. Comparing them would strand the camera on
            // Custom with nothing on screen to explain why.
            if (preset.DoFEnabled)
            {
                if ((int)depthOfField.mode.value != preset.DoFStyle) fields |= Bit(BasisCameraPresetField.DepthStyle);
                if (Mathf.Abs(depthOfField.aperture.value - preset.Aperture) > ApertureTolerance)
                    fields |= Bit(BasisCameraPresetField.DepthAperture);
                if (Mathf.Abs(depthOfField.focalLength.value - preset.FocalLength) > FocalLengthTolerance)
                    fields |= Bit(BasisCameraPresetField.FocalLength);
            }
        }

        var motionBlur = MetaData?.motionBlur;
        if (motionBlur != null)
        {
            float liveMotionBlur = motionBlur.active ? motionBlur.intensity.value : 0f;
            if (Mathf.Abs(liveMotionBlur - preset.MotionBlur) > MotionBlurTolerance)
                fields |= Bit(BasisCameraPresetField.MotionBlur);
        }

        CompareLook(preset.Look, ref fields);
        return new BasisCameraPresetDiff(mode, fields);
    }

    private static ulong Bit(BasisCameraPresetField field) => BasisCameraPresetDiff.Bit(field);

    /// <summary>
    /// Re-derives <see cref="CameraMode"/> from the live camera and reports whether it moved.
    ///
    /// <para>The current mode is checked first so a camera that still matches keeps its label
    /// without being re-identified. Only once it has drifted does the rest of the table get a look,
    /// which is what lets a camera arrive back at a mode by hand — set a Photo camera's follow and
    /// its lens the way Follow Me has them and the panel will say Follow Me, because it is.</para>
    /// </summary>
    public bool RefreshCameraMode()
    {
        BasisCameraMode resolved = ResolveCameraMode();

        // A camera that has arrived back on a preset is measured against that one from here on,
        // however it got there. Only the drop to Custom leaves the comparison where it was.
        if (resolved != BasisCameraMode.Custom) ComparedMode = resolved;

        if (resolved == CameraMode) return false;

        CameraMode = resolved;
        return true;
    }

    private BasisCameraMode ResolveCameraMode()
    {
        if (CameraMode != BasisCameraMode.Custom && MatchesCameraMode(CameraMode))
        {
            return CameraMode;
        }

        for (int Index = 0; Index < BasisCameraModes.Ordered.Length; Index++)
        {
            BasisCameraMode candidate = BasisCameraModes.Ordered[Index];
            if (candidate != BasisCameraMode.Custom && MatchesCameraMode(candidate))
            {
                return candidate;
            }
        }

        return BasisCameraMode.Custom;
    }

    /// <summary>
    /// Restores a saved mode as part of loading a settings file.
    ///
    /// <para>Only the mode's <em>placement</em> is re-armed — follow, the shot rig, and the pin.
    /// Those three are deliberately absent from the file, so without this a camera would come back
    /// labelled Cinematic while sitting inert in your hand. Everything else a preset writes is
    /// persisted in its own right and has just been applied from the file, so re-applying it here
    /// would overwrite the user's saved values with the preset's.</para>
    ///
    /// <para>The label is then re-derived rather than asserted: a file whose values no longer match
    /// the mode it names settles on Custom instead of lying, and a hand-tuned file that happens to
    /// match a preset exactly is promoted to it.</para>
    /// </summary>
    internal void RestoreCameraMode(BasisCameraMode mode)
    {
        // Only the pin. Everything else a preset writes — the two slots, the effects, the framing
        // — is carried by the settings file that has just been applied, so re-running the preset
        // here would overwrite the values the load had only just finished restoring.
        if (TryGetPreset(mode, out BasisCameraModePreset preset))
        {
            PinSpace = preset.Pin;
            ComparedMode = mode;
        }

        CameraMode = mode;
        RefreshCameraMode();
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>Test-only access to the restore, which is otherwise only reached through a load.</summary>
    public void RestoreCameraModeForTest(BasisCameraMode mode) => RestoreCameraMode(mode);

    /// <summary>
    /// Test-only access to the placement write, taking the raw int a settings file actually
    /// stores rather than the enum it is read back as.
    /// </summary>
    public void ApplyPlacementForTest(int pinSpace) => ApplyPlacement((CameraPinSpace)pinSpace, null);
#endif
}
