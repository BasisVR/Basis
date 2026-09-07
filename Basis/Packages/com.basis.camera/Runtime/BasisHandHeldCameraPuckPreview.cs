using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// A viewfinder that appears in the world when the detached camera is turned back toward you, and
/// goes again when it is turned away.
///
/// <para>
/// Turning the puck around to point at yourself is the one moment the camera's own viewfinder is
/// useless — it is on the far side of the body, facing away. This puts the feed where you are
/// already looking, on <see cref="BasisHandHeldCamera.MarkerLayer"/> alongside the puck itself, so
/// the capture camera culls it and nothing it shows can reach a photo, a 360 or the video feed.
/// </para>
///
/// <para>
/// Deliberately independent of <see cref="BasisHandHeldCamera.detachedMarker"/>: a camera turned on
/// you is the same situation whether the marker showing where it went is the puck, the wireframe or
/// nothing at all, and a toggle that silently did nothing because an unrelated dropdown was on Off
/// would read as broken.
/// </para>
/// </summary>
public partial class BasisHandHeldCamera
{
    [Header("Puck Look-At Preview")]

    /// <summary>Whether a detached camera pointed at you puts its feed up in front of it. Off by default.</summary>
    public bool puckLookAtPreview;

    /// <summary>How far off the lens axis you may stand, in degrees, for the preview to come up.</summary>
    public float puckPreviewShowAngle = 40f;

    /// <summary>
    /// How far off the lens axis you may stand before a preview already up goes again. Wider than
    /// <see cref="puckPreviewShowAngle"/> on purpose: a hand holding the puck near the boundary
    /// would otherwise flicker the screen in and out on the shake alone.
    /// </summary>
    public float puckPreviewHideAngle = 55f;

    /// <summary>
    /// How far past the puck the preview is parked along the lens axis, in metres at default avatar
    /// scale. Measured from the puck rather than from the camera so a resized marker carries the
    /// preview out with it — with the camera facing you, further along that axis is nearer to you,
    /// and the preview is the thing you are meant to be reading, not the thing it is in front of.
    /// </summary>
    public float puckPreviewLensClearance = 0.17f;

    /// <summary>Closest the preview may sit to your head, in metres at default avatar scale.</summary>
    public float puckPreviewMinViewerDistance = 0.35f;

    /// <summary>Width of the preview, in metres at default avatar scale, at the reference distance.</summary>
    public float puckPreviewWidth = 0.2f;

    /// <summary>Viewing distance <see cref="puckPreviewWidth"/> is authored for.</summary>
    public float puckPreviewReferenceDistance = 1f;

    /// <summary>
    /// Most the preview may be scaled up to hold its apparent size at range. Angular size is size
    /// over distance, so growing with distance cancels — but only up to a point: a camera flown
    /// across the map would otherwise be given a screen tens of metres wide, buried in whatever
    /// world geometry stood between the two of you.
    /// </summary>
    public float puckPreviewMaxGrowth = 6f;

    private GameObject puckPreviewInstance;
    private Material puckPreviewMaterial;

    /// <summary>True while the look-at preview is up. A feed consumer for as long as it is.</summary>
    public bool IsPuckPreviewVisible => puckPreviewInstance != null;

    /// <summary>Turns the look-at preview on or off, taking down one already up.</summary>
    public void SetPuckLookAtPreview(bool enabled)
    {
        if (puckLookAtPreview == enabled) return;
        puckLookAtPreview = enabled;
        if (!enabled) DespawnPuckPreview();
    }

    /// <summary>
    /// Whether the preview belongs on screen this frame. Pure so the hysteresis — the whole
    /// subtlety here — can be exercised without a scene, a head or a render texture.
    /// </summary>
    /// <param name="showing">Whether the preview is already up, which is what widens the band.</param>
    public static bool PuckPreviewShouldShow(Vector3 cameraPosition, Quaternion cameraRotation, Vector3 viewerHead,
        bool showing, float showAngle, float hideAngle)
    {
        Vector3 toViewer = viewerHead - cameraPosition;
        // Standing inside the camera is as looked-at as it gets, and there is no direction to
        // measure against.
        if (toViewer.sqrMagnitude < 1e-8f) return true;

        // A hide angle authored under the show angle would invert the band into a dead zone the
        // preview could never leave, so the wider of the two is what an open preview is held to.
        float threshold = showing ? Mathf.Max(showAngle, hideAngle) : showAngle;
        return Vector3.Angle(cameraRotation * Vector3.forward, toViewer) <= threshold;
    }

    /// <summary>
    /// How far out along the lens axis the preview sits, in metres at default avatar scale: past
    /// wherever the puck is parked at this marker size, by a clearance that grows with the marker
    /// so an enlarged puck cannot poke through the screen in front of it.
    /// </summary>
    public static float PuckPreviewParkDistance(float markerScale, float clearance) =>
        FollowPuckParkDistance(markerScale) + clearance * Mathf.Max(1f, markerScale);

    /// <summary>
    /// How much bigger than authored the preview is drawn at a given range. Never below 1: the
    /// authored width is what it looks like in the hand, and shrinking from there would make the
    /// one case you can already read the hardest.
    /// </summary>
    public static float PuckPreviewGrowth(float distance, float referenceDistance, float maxGrowth)
    {
        if (referenceDistance <= 1e-4f) return 1f;
        return Mathf.Clamp(distance / referenceDistance, 1f, Mathf.Max(1f, maxGrowth));
    }

    /// <summary>
    /// Per-frame: spawn, place and bind the preview while the camera is off in the world and turned
    /// on you, and take it down otherwise. Run from <see cref="SimulateLate"/> after the camera has
    /// been moved, so the pose it is placed from is this frame's.
    /// </summary>
    private void UpdatePuckPreview()
    {
        // A film body has no screen to show a live feed on — the same gate direct-to-screen and the
        // video output stand behind.
        bool wanted = puckLookAtPreview && IsDetachedFromHand && BodyAllowsLiveFeed
            && captureCamera != null && BasisLocalCameraDriver.HasInstance;

        if (wanted)
        {
            captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
            wanted = PuckPreviewShouldShow(pos, rot, BasisLocalCameraDriver.HeadPosition,
                IsPuckPreviewVisible, puckPreviewShowAngle, puckPreviewHideAngle);
        }

        if (!wanted)
        {
            DespawnPuckPreview();
            return;
        }

        if (puckPreviewInstance == null) SpawnPuckPreview();
        if (puckPreviewInstance == null) return;

        BindPuckPreviewFeed();
        PositionPuckPreview();
    }

    private void SpawnPuckPreview()
    {
        // The prop's own viewfinder material, so the preview is drawn the way the camera draws
        // itself. Unlit/Texture only stands in for a rig that has none.
        if (Material != null)
        {
            puckPreviewMaterial = Instantiate(Material);
        }
        else
        {
            Shader unlit = Shader.Find("Unlit/Texture");
            if (unlit == null) return;
            puckPreviewMaterial = new Material(unlit);
        }

        // Double-sided, so catching the preview edge-on as the camera swings past still shows a
        // picture rather than nothing.
        if (puckPreviewMaterial.HasProperty("_Cull")) puckPreviewMaterial.SetFloat("_Cull", 0f);

        puckPreviewInstance = GameObject.CreatePrimitive(PrimitiveType.Quad);
        puckPreviewInstance.name = "CameraPuckLookAtPreview";

        // The collider a primitive quad ships with would be the nearest thing in front of the
        // lens for click-to-focus to rack onto, exactly the way the puck's used to. This is
        // something to read, not something to hold, so it carries none at all.
        if (puckPreviewInstance.TryGetComponent(out MeshCollider meshCollider)) DestroyImmediate(meshCollider);

        int overlayUi = MarkerLayer;
        if (overlayUi >= 0) puckPreviewInstance.layer = overlayUi;

        if (puckPreviewInstance.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.sharedMaterial = puckPreviewMaterial;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        PositionPuckPreview();

        // A second surface drawing the feed, and one the prop's own visibility says nothing about:
        // without this the camera stops rendering the moment you look away from the body, and the
        // preview holds the last frame it was given — a still image that reads as a live one.
        UpdateRenderGate();
    }

    /// <summary>
    /// Parks the preview along the lens axis and turns it to face you, sized so it stays readable
    /// at range. Held off your head by <see cref="puckPreviewMinViewerDistance"/>: a puck brought
    /// right up to your face would otherwise put the screen inside it.
    /// </summary>
    private void PositionPuckPreview()
    {
        if (puckPreviewInstance == null || captureCamera == null) return;

        float scale = BaseDetachedMarkerScale;
        captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
        Vector3 forward = camRot * Vector3.forward;
        Vector3 headPos = BasisLocalCameraDriver.HasInstance ? BasisLocalCameraDriver.HeadPosition : camPos - forward;

        Vector3 position = camPos
            + forward * (PuckPreviewParkDistance(DetachedMarkerScale, puckPreviewLensClearance) * scale);

        Vector3 fromHead = position - headPos;
        float minDistance = puckPreviewMinViewerDistance * scale;
        if (fromHead.magnitude < minDistance)
        {
            // Back toward the camera when there is no direction left to push along — with the lens
            // on you, that is the one way out of your own head.
            Vector3 away = fromHead.sqrMagnitude > 1e-6f ? fromHead.normalized : -forward;
            position = headPos + away * minDistance;
            fromHead = position - headPos;
        }

        // The quad's face is its local -Z, so +Z is pointed away from you for it to be read.
        Quaternion rotation = fromHead.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(fromHead.normalized, Vector3.up)
            : camRot;
        puckPreviewInstance.transform.SetPositionAndRotation(position, rotation);

        RenderTexture feed = ViewfinderTexture;
        float aspect = feed != null && feed.height > 0 ? (float)feed.width / feed.height : 16f / 9f;
        float width = puckPreviewWidth * scale
            * PuckPreviewGrowth(fromHead.magnitude, puckPreviewReferenceDistance * scale, puckPreviewMaxGrowth);
        puckPreviewInstance.transform.localScale = new Vector3(width, width / aspect, 1f);
    }

    /// <summary>
    /// Rebinds every frame rather than once at spawn: the viewfinder texture is swapped out from
    /// under everything showing it when focus peaking or the grid comes on, and again whenever the
    /// feed is resized.
    /// </summary>
    private void BindPuckPreviewFeed()
    {
        RenderTexture feed = ViewfinderTexture;
        if (feed == null || puckPreviewMaterial == null || puckPreviewMaterial.mainTexture == feed) return;

        // Both, the way the prop's own viewfinder is bound: which of the two the shader reads
        // depends on the shader, and the material is whatever the prefab was authored with.
        puckPreviewMaterial.SetTexture("_MainTex", feed);
        puckPreviewMaterial.mainTexture = feed;
    }

    private void DespawnPuckPreview()
    {
        bool wasUp = puckPreviewInstance != null;
        if (wasUp)
        {
            Destroy(puckPreviewInstance);
            puckPreviewInstance = null;
        }
        if (puckPreviewMaterial != null)
        {
            Destroy(puckPreviewMaterial);
            puckPreviewMaterial = null;
        }
        // Only once it has actually gone: the gate reads IsPuckPreviewVisible, and asking it while
        // the reference still stood would keep the camera rendering for a viewer that has left.
        if (wasUp) UpdateRenderGate();
    }
}
