using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The physical camera, as opposed to its settings: the film in it, the wheel you have to wind, the
/// flash on the front, and the date it burns into the corner.
///
/// <para>Everything here is state a slider cannot reach, which is exactly why it is worth having.
/// A mode that only changed the grain and the vignette would be a filter; what makes a disposable a
/// disposable is that it has twenty-seven frames in it, that the shutter will not fire again for a
/// second, and that the shot you got is the shot you got.</para>
///
/// <para>The body is stored in its own right rather than derived from <see cref="CameraMode"/>: the
/// moment you touch a slider the mode drops to Custom, and you are still holding a disposable. It
/// is also the only thing that tells two film bodies apart on a camera with no volume profile — the
/// look compares are all skipped there — which is what keeps the mode table unambiguous.</para>
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>
    /// A saved frame count meaning "a full load". Written by a settings file that predates bodies
    /// and by one saved on a body that had no film, so neither loads as a camera that is empty.
    /// </summary>
    public const int FullRoll = -1;

    /// <summary>The camera you are holding. Only ever changed by picking a mode, or by loading one.</summary>
    public BasisCameraBodyKind Body { get; private set; } = BasisCameraBodyKind.Digital;

    /// <summary>What that body can and cannot do. Never null.</summary>
    public BasisCameraBodyTraits BodyTraits => BasisCameraBodies.Get(Body);

    /// <summary>Frames left on this load, or zero on a body that never runs out.</summary>
    public int ExposuresRemaining { get; private set; }

    /// <summary>Whether the flash will fire on the next frame. Meaningless on a body without one.</summary>
    public bool FlashEnabled { get; private set; }

    /// <summary>Seconds until the shutter comes back after the last frame.</summary>
    public float WindOnRemaining { get; private set; }

    /// <summary>Seconds until the last frame has finished coming up.</summary>
    public float DevelopRemaining { get; private set; }

    /// <summary>Seconds until the flash has charged again.</summary>
    public float FlashRecycleRemaining { get; private set; }

    /// <summary>True while the flash would actually fire: fitted, switched on, and charged.</summary>
    public bool FlashReady =>
        BodyTraits.HasFlash && FlashEnabled && FlashRecycleRemaining <= 0f;

    /// <summary>
    /// Whether this body can send its picture anywhere but its own viewfinder. False on the film
    /// bodies, which gates direct-to-screen and the video output — there is no
    /// socket on the back of a disposable, so those are things it does not have rather than things
    /// somebody switched off.
    /// </summary>
    public bool BodyAllowsLiveFeed => BodyTraits.LivePreview;

    private Light flashLight;
    private float flashHoldRemaining;

    /// <summary>
    /// True while the prop's own display is showing the frame counter rather than the self-timer.
    ///
    /// <para>The little window with a number in it is most of what a film camera tells you, and the
    /// prop already has exactly one place to put a number — the countdown display. Sharing it is
    /// what makes the counter visible in VR without a prefab change, and this flag is what keeps
    /// the two from writing over each other: the counter never takes the display while the timer is
    /// running, and never clears text it did not put there.</para>
    /// </summary>
    private bool frameCountShowing;

    /// <summary>
    /// Reused across captures. A stamp is a few dozen rectangles and this is reached from a GPU
    /// readback callback, so it is the one allocation on the path worth not making every photo.
    /// </summary>
    private readonly List<RectInt> stampGlyphs = new List<RectInt>();

    /// <summary>
    /// Hands the camera a different body.
    ///
    /// <para><paramref name="freshLoad"/> is what separates picking a mode from loading a file:
    /// choosing Disposable is being handed a new one, so it comes with a full roll and its flash
    /// switched on; loading a file is picking up the one you already had, so the counter is
    /// whatever it was left at.</para>
    /// </summary>
    internal void SetBody(BasisCameraBodyKind kind, bool freshLoad)
    {
        BasisCameraBodyTraits previous = BodyTraits;
        bool moved = Body != kind;
        Body = kind;

        BasisCameraBodyTraits traits = BasisCameraBodies.Get(kind);

        if (moved || freshLoad)
        {
            ExposuresRemaining = traits.Exposures;
            FlashEnabled = traits.HasFlash;
            WindOnRemaining = 0f;
            DevelopRemaining = 0f;
            FlashRecycleRemaining = 0f;
        }
        else
        {
            ExposuresRemaining = traits.HasFilm
                ? Mathf.Clamp(ExposuresRemaining, 0, traits.Exposures)
                : 0;
        }

        // A flash held over from the body before this one would be a lamp bolted to a camcorder,
        // and a frame count held over would be a number on a camera that does not count.
        if (!traits.HasFlash) ReleaseFlash();
        ClearFrameCount();

        ApplyBodyCaptureSize(previous, traits);
        ApplyBodyLiveFeed(previous, traits);
    }

    /// <summary>
    /// Restores a body from a settings file: the kind, what was left on the load, and whether the
    /// flash was on. Separate from <see cref="SetBody"/> because a load must not hand out a fresh
    /// roll — a disposable that refilled itself every session would never run out at all.
    /// </summary>
    internal void RestoreBody(int kind, int exposuresRemaining, bool flashEnabled)
    {
        SetBody(BasisCameraBodies.Sanitize(kind), freshLoad: true);

        BasisCameraBodyTraits traits = BodyTraits;
        if (traits.HasFilm && exposuresRemaining != FullRoll)
        {
            ExposuresRemaining = Mathf.Clamp(exposuresRemaining, 0, traits.Exposures);
        }

        FlashEnabled = traits.HasFlash && flashEnabled;
    }

    /// <summary>Winds a fresh roll in. Happens by itself when one runs out; nothing has to ask.</summary>
    public void ReloadFilm()
    {
        BasisCameraBodyTraits traits = BodyTraits;
        if (!traits.HasFilm) return;

        ExposuresRemaining = traits.Exposures;
        WindOnRemaining = 0f;
        DevelopRemaining = 0f;

        // A counter left reading zero over a camera that has just been reloaded is the one thing
        // the display can say that is worse than saying nothing.
        ClearFrameCount();
    }

    /// <summary>Switches the flash on or off. Ignored on a body with nothing on the front.</summary>
    public void SetFlashEnabled(bool enabled)
    {
        if (!BodyTraits.HasFlash || FlashEnabled == enabled) return;

        FlashEnabled = enabled;
    }

    /// <summary>Why the shutter will not fire, or <see cref="BasisCameraShutterState.Ready"/>.</summary>
    public BasisCameraShutterState EvaluateShutter()
    {
        if (DevelopRemaining > 0f) return BasisCameraShutterState.Developing;
        if (WindOnRemaining > 0f) return BasisCameraShutterState.WindingOn;
        return BasisCameraShutterState.Ready;
    }

    /// <summary>
    /// Takes a frame off the body, if it has one to give.
    ///
    /// <para>Called before the shutter sound and before anything is rendered, for the reason the
    /// moderation check is: a camera that will not take the picture must not sound like it did.
    /// Spending the frame here rather than on the way out is also what makes the count honest — a
    /// readback that fails has still used the film.</para>
    /// </summary>
    private bool TryTakeFrame()
    {
        // A spent roll winds itself on. There is no film to go and buy, so an empty counter was
        // only ever a button the operator had to find before the camera would work again.
        BasisCameraBodyTraits loaded = BodyTraits;
        if (loaded.HasFilm && ExposuresRemaining <= 0) ExposuresRemaining = loaded.Exposures;

        BasisCameraShutterState state = EvaluateShutter();
        if (state != BasisCameraShutterState.Ready)
        {
            BasisDebug.Log($"Shutter refused: {state}.", BasisDebug.LogTag.Camera);
            return false;
        }

        BasisCameraBodyTraits traits = BodyTraits;
        if (traits.HasFilm) ExposuresRemaining--;

        WindOnRemaining = traits.WindOnSeconds;
        DevelopRemaining = traits.DevelopSeconds;

        FireFlash(traits);
        ShowFrameCount(traits);
        return true;
    }

    /// <summary>
    /// Counts the body's clocks down and puts the flash out. Driven from the render phase alongside
    /// the rest of the camera's upkeep, so the light is extinguished on a frame boundary rather
    /// than mid-capture.
    /// </summary>
    private void TickBody()
    {
        float delta = Time.deltaTime;

        if (WindOnRemaining > 0f) WindOnRemaining = Mathf.Max(0f, WindOnRemaining - delta);
        if (DevelopRemaining > 0f) DevelopRemaining = Mathf.Max(0f, DevelopRemaining - delta);
        if (FlashRecycleRemaining > 0f) FlashRecycleRemaining = Mathf.Max(0f, FlashRecycleRemaining - delta);

        if (flashHoldRemaining > 0f)
        {
            flashHoldRemaining -= delta;
            if (flashHoldRemaining <= 0f && flashLight != null) flashLight.enabled = false;
        }

        // The count stays up for as long as the camera is still doing something about the last
        // frame, and then goes — except on an empty camera, where a zero that will not go away is
        // exactly the point.
        if (frameCountShowing && WindOnRemaining <= 0f && DevelopRemaining <= 0f)
        {
            ClearFrameCount();
        }
    }

    /// <summary>Puts the frames left on the prop's display, if the self-timer is not using it.</summary>
    private void ShowFrameCount(BasisCameraBodyTraits traits)
    {
        if (!traits.HasFilm || countdownText == null || countdownRoutine != null) return;

        countdownText.text = ExposuresRemaining.ToString();
        frameCountShowing = true;
    }

    private void ClearFrameCount()
    {
        if (!frameCountShowing) return;
        frameCountShowing = false;

        // Not while the timer has it: the countdown has taken the display back, and blanking it
        // here would erase a number somebody is watching count down.
        if (countdownText != null && countdownRoutine == null) countdownText.text = string.Empty;
    }

    // ---------- The flash ----------

    /// <summary>
    /// Lights the frame, locally.
    ///
    /// <para>A real light rather than a brightness lift, because the shot is what has to be lit:
    /// the capture renders through <c>captureCamera.Render()</c> with the lamp on, so faces near
    /// the camera come out hot and the far side of the room falls away, which is the entire look of
    /// a flash photograph and cannot be had by raising exposure.</para>
    ///
    /// <para>It is a local light: remote players see the picture you took, but not the pop. Making
    /// the pop visible to the room would need a network message of its own, which the shutter sound
    /// has and this does not.</para>
    /// </summary>
    private void FireFlash(BasisCameraBodyTraits traits)
    {
        if (!traits.HasFlash || !FlashEnabled || FlashRecycleRemaining > 0f) return;

        // The charge is spent before the lamp is asked for, and stays spent if there is no lamp to
        // build. Firing is what the body did; the light is only how it was seen — a camera that
        // recharged instantly because its capture camera had not been wired yet would be reporting
        // on the scene rather than on itself.
        FlashRecycleRemaining = traits.FlashRecycleSeconds;

        if (!EnsureFlash(traits)) return;

        flashLight.enabled = true;
        flashHoldRemaining = traits.FlashSeconds;
    }

    /// <summary>
    /// Builds the lamp on first use and aims it down the lens. Created under the capture camera so
    /// it follows the shot rather than the prop — a flash that pointed where the body was held
    /// would light the room behind a selfie.
    /// </summary>
    private bool EnsureFlash(BasisCameraBodyTraits traits)
    {
        if (captureCamera == null) return false;

        if (flashLight == null)
        {
            GameObject go = new GameObject("BasisCameraFlash");
            go.transform.SetParent(captureCamera.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            flashLight = go.AddComponent<Light>();
            flashLight.type = LightType.Spot;
            flashLight.shadows = LightShadows.None;
            flashLight.enabled = false;
        }

        flashLight.intensity = traits.FlashIntensity;
        flashLight.range = traits.FlashRange;
        flashLight.spotAngle = traits.FlashAngle;
        flashLight.color = traits.FlashColour;
        return true;
    }

    private void ReleaseFlash()
    {
        flashHoldRemaining = 0f;
        if (flashLight == null) return;

        Destroy(flashLight.gameObject);
        flashLight = null;
    }

    // ---------- The frame the body shoots ----------

    /// <summary>
    /// Puts the capture on the frame this body shoots, or hands it back to the resolution the panel
    /// is showing.
    ///
    /// <para>A body's frame is a shape, not an entry on the resolution list: a square instant print
    /// and a 4:3 tape frame are not things you would pick from a menu of 720p through 8K, they are
    /// what the camera is. So the list is bypassed while a film body is fitted, and restored the
    /// moment one is not — which is also why the Output section is coloured as doing nothing.</para>
    /// </summary>
    private void ApplyBodyCaptureSize(BasisCameraBodyTraits previous, BasisCameraBodyTraits traits)
    {
        // The sensitive area, before the frame: everything the lens does — the field of view a
        // focal length gives, the depth an f-number leaves — is measured against it, so a body that
        // does not set its own would wear the last body's idea of how big a millimetre is.
        if (captureCamera != null && traits.SensorSize.x > 0f && traits.SensorSize.y > 0f)
        {
            // Field of view is re-asserted after, not before: on a physical camera the sensor, the
            // focal length and the field of view are one value seen three ways, and whichever is
            // written last is the one that wins.
            float fieldOfView = captureCamera.fieldOfView;
            captureCamera.sensorSize = traits.SensorSize;
            captureCamera.gateFit = UnityEngine.Camera.GateFitMode.Vertical;
            captureCamera.fieldOfView = fieldOfView;
        }

        if (traits.CaptureSize.x > 0 && traits.CaptureSize.y > 0)
        {
            captureWidth = traits.CaptureSize.x;
            captureHeight = traits.CaptureSize.y;
            ApplyViewfinderCrop();
            return;
        }

        // Only where something was actually taken away. Every mode change runs through here, and a
        // camera that never had a body's frame in the first place has nothing to be handed back —
        // reasserting the panel's index there would be this code quietly owning the resolution.
        if (previous.CaptureSize.x <= 0) return;

        // Through the UI because the index lives with the control that owns it, and skipped where
        // there is no UI — an edit-mode camera keeps what it has.
        HandHeld?.RestoreCaptureResolution();
    }

    /// <summary>
    /// Re-runs the gates a body's lack of an output socket closes: direct-to-screen and the video
    /// stream.
    ///
    /// <para>Run in both directions, because coming back off a film body has to give them back —
    /// the toggles were never cleared, only overruled, so the settings return with the camera that
    /// can use them. Skipped on a camera with no capture camera, which is an edit-mode fixture:
    /// nothing there is presenting anything for a body to take away.</para>
    /// </summary>
    private void ApplyBodyLiveFeed(BasisCameraBodyTraits previous, BasisCameraBodyTraits traits)
    {
        // Only on the change. The re-ask rebinds the viewfinder feed and respawns the preview
        // screen, which is real work to do on every mode change for a permission that moves twice
        // in a session.
        if (previous.LivePreview == traits.LivePreview) return;

        // Stopped rather than left to fail on its next frame: a stream already running was started
        // by a body that had a socket, and the one being fitted does not. Safe to call at any time
        // — it is a no-op on a camera that was not streaming.
        if (!traits.LivePreview) StopVideoOutput();

        // The window gate runs in both directions: a film body takes the monitor back, and the
        // digital body fitted after it returns the feed without the setting having moved.
        RefreshDirectToScreen();
    }

    // ---------- The stamp ----------

    /// <summary>
    /// Everything the body does to a picture after the shutter: the light that got in around it,
    /// the date printed on it, and the frame it is mounted in.
    ///
    /// <para>Returns the texture to save, which is not always the one that was handed in — a print
    /// is a bigger sheet with the photograph placed on it. The order is the order it happens in: a
    /// leak fogged the film before the picture was taken, the databack exposed the date through the
    /// same emulsion, and only then was any of it mounted.</para>
    ///
    /// <para>EXR is left alone throughout. It is a linear negative kept for grading later, and
    /// burning display-referred orange into one — or padding it out with a white border — would be
    /// writing decoration into the only format that promised not to have any.</para>
    /// </summary>
    private Texture2D FinishPicture(Texture2D picture)
    {
        if (picture == null || picture.format != TextureFormat.RGBA32) return picture;

        BurnLightLeak(picture);
        BurnStamp(picture);
        return MountPrint(picture);
    }

    /// <summary>
    /// Fogs one edge of a frame that was at the wrong end of the roll.
    ///
    /// <para>Added rather than blended, because that is what it is: light reached the film twice, so
    /// the fogged part is the picture <em>plus</em> an exposure, and a midtone under a leak goes to
    /// orange-white rather than being replaced by orange.</para>
    /// </summary>
    private void BurnLightLeak(Texture2D picture)
    {
        BasisCameraBodyTraits traits = BodyTraits;
        if (!traits.LeaksLight || !traits.HasFilm) return;
        if (!BasisCameraPrintFinish.ShouldLeak(ExposuresRemaining, traits.Exposures)) return;

        if (!BasisCameraPrintFinish.TryGetLeak(
                ExposuresRemaining, picture.width, picture.height,
                out int edge, out int depth, out float strength))
        {
            return;
        }

        Unity.Collections.NativeArray<byte> pixels = picture.GetRawTextureData<byte>();
        Color32 fog = BasisCameraPrintFinish.LeakColour;
        int width = picture.width;
        int height = picture.height;

        // Only the band is walked. The rest of the frame is most of the picture and none of the
        // leak, and a readback at 1296x864 is not somewhere to spend a full pass for nothing.
        int xMin = edge == 1 ? Mathf.Max(0, width - depth) : 0;
        int xMax = edge == 0 ? Mathf.Min(width, depth) : width;
        int yMin = edge == 3 ? Mathf.Max(0, height - depth) : 0;
        int yMax = edge == 2 ? Mathf.Min(height, depth) : height;

        for (int y = yMin; y < yMax; y++)
        {
            int row = y * width;
            for (int x = xMin; x < xMax; x++)
            {
                int distance;
                switch (edge)
                {
                    case 0: distance = x; break;
                    case 1: distance = width - 1 - x; break;
                    case 2: distance = y; break;
                    default: distance = height - 1 - y; break;
                }

                float amount = BasisCameraPrintFinish.LeakFalloff(distance, depth) * strength;
                if (amount <= 0f) continue;

                int offset = (row + x) * 4;
                pixels[offset] = AddExposure(pixels[offset], fog.r, amount);
                pixels[offset + 1] = AddExposure(pixels[offset + 1], fog.g, amount);
                pixels[offset + 2] = AddExposure(pixels[offset + 2], fog.b, amount);
            }
        }

        picture.Apply(false);
    }

    /// <summary>One channel, exposed a second time. Clamped, because film clips and bytes do too.</summary>
    private static byte AddExposure(byte channel, byte light, float amount) =>
        (byte)Mathf.Min(255, channel + Mathf.RoundToInt(light * amount));

    /// <summary>
    /// Mounts the picture on a sheet of instant film stock.
    ///
    /// <para>The photograph is copied row by row into its window rather than scaled into it, so the
    /// print is the picture at full size with a border around it — which is what a print is. The
    /// sheet is reused between shots for the same reason the screenshot buffer is: at print size
    /// this is a megabyte and a half that nothing else needs a second copy of.</para>
    /// </summary>
    private Texture2D MountPrint(Texture2D picture)
    {
        if (!BasisCameraPrintFinish.TryGetMount(
                BodyTraits.PrintBorder, picture.width, picture.height,
                out RectInt window, out int printWidth, out int printHeight))
        {
            return picture;
        }

        if (pooledPrint == null || pooledPrint.width != printWidth || pooledPrint.height != printHeight)
        {
            if (pooledPrint != null) Destroy(pooledPrint);
            pooledPrint = new Texture2D(printWidth, printHeight, TextureFormat.RGBA32, false);
        }

        Unity.Collections.NativeArray<byte> sheet = pooledPrint.GetRawTextureData<byte>();
        Unity.Collections.NativeArray<byte> source = picture.GetRawTextureData<byte>();
        Color32 stock = BasisCameraPrintFinish.InstantBorderColour;

        // Row-contiguous on both sides, so the sheet is filled with three copies per row —
        // border, photograph, border — instead of one bounds-checked write per byte.
        int rowBytes = printWidth * 4;
        if (printBorderRow == null || printBorderRow.Length != rowBytes)
        {
            printBorderRow = new byte[rowBytes];
            for (int x = 0; x < printWidth; x++)
            {
                int offset = x * 4;
                printBorderRow[offset] = stock.r;
                printBorderRow[offset + 1] = stock.g;
                printBorderRow[offset + 2] = stock.b;
                printBorderRow[offset + 3] = 255;
            }
        }

        int leftBytes = window.xMin * 4;
        int windowBytes = window.width * 4;
        int rightStart = window.xMax * 4;
        int rightBytes = rowBytes - rightStart;

        for (int y = 0; y < printHeight; y++)
        {
            int sheetRow = y * rowBytes;

            if (y < window.yMin || y >= window.yMax)
            {
                Unity.Collections.NativeArray<byte>.Copy(printBorderRow, 0, sheet, sheetRow, rowBytes);
                continue;
            }

            if (leftBytes > 0) Unity.Collections.NativeArray<byte>.Copy(printBorderRow, 0, sheet, sheetRow, leftBytes);
            Unity.Collections.NativeArray<byte>.Copy(source, (y - window.yMin) * windowBytes, sheet, sheetRow + leftBytes, windowBytes);
            if (rightBytes > 0) Unity.Collections.NativeArray<byte>.Copy(printBorderRow, rightStart, sheet, sheetRow + rightStart, rightBytes);
        }

        pooledPrint.Apply(false);
        return pooledPrint;
    }

    /// <summary>The sheet a print is mounted on, kept between shots. Null until a body asks for one.</summary>
    private Texture2D pooledPrint;
    private byte[] printBorderRow;

    /// <summary>Frees the sheet, alongside the rest of what the camera pooled.</summary>
    private void ReleasePrintSheet()
    {
        if (pooledPrint == null) return;

        Destroy(pooledPrint);
        pooledPrint = null;
    }

    /// <summary>
    /// Burns the body's stamp into the finished picture.
    ///
    /// <para>Written into the readback buffer rather than rendered, so it lands at the picture's own
    /// resolution with hard edges and no filtering — which is what a databack's exposed characters
    /// and a recorder's character generator both looked like. EXR is left alone: it is a linear
    /// negative for grading later, and painting display-referred orange into one would be writing
    /// nonsense into the only format that promised not to have any.</para>
    /// </summary>
    private void BurnStamp(Texture2D picture)
    {
        BasisCameraStamp stamp = BodyTraits.Stamp;
        if (stamp == BasisCameraStamp.None || picture == null) return;
        if (picture.format != TextureFormat.RGBA32) return;

        stampGlyphs.Clear();
        if (!BasisCameraStampPainter.BuildGlyphs(
                BasisCameraStampPainter.Compose(stamp, DateTime.Now),
                picture.width, picture.height, stampGlyphs))
        {
            return;
        }

        Unity.Collections.NativeArray<byte> pixels = picture.GetRawTextureData<byte>();
        Color32 ink = BasisCameraStampPainter.ColourOf(stamp);
        int width = picture.width;
        int height = picture.height;

        for (int Index = 0; Index < stampGlyphs.Count; Index++)
        {
            RectInt rect = stampGlyphs[Index];

            int xMin = Mathf.Max(0, rect.xMin);
            int xMax = Mathf.Min(width, rect.xMax);
            int yMin = Mathf.Max(0, rect.yMin);
            int yMax = Mathf.Min(height, rect.yMax);

            for (int y = yMin; y < yMax; y++)
            {
                int row = y * width;
                for (int x = xMin; x < xMax; x++)
                {
                    int offset = (row + x) * 4;
                    pixels[offset] = ink.r;
                    pixels[offset + 1] = ink.g;
                    pixels[offset + 2] = ink.b;
                    pixels[offset + 3] = ink.a;
                }
            }
        }

        picture.Apply(false);
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>Test-only access to the frame spend, which is otherwise only reached by the shutter.</summary>
    public bool TryTakeFrameForTest() => TryTakeFrame();

    /// <summary>Test-only clock, standing in for the render-phase tick that does not run in edit mode.</summary>
    public void AdvanceBodyForTest(float seconds)
    {
        WindOnRemaining = Mathf.Max(0f, WindOnRemaining - seconds);
        DevelopRemaining = Mathf.Max(0f, DevelopRemaining - seconds);
        FlashRecycleRemaining = Mathf.Max(0f, FlashRecycleRemaining - seconds);
    }

    /// <summary>Test-only access to the load restore a settings file performs.</summary>
    public void RestoreBodyForTest(int kind, int exposuresRemaining, bool flashEnabled) =>
        RestoreBody(kind, exposuresRemaining, flashEnabled);
#endif
}
