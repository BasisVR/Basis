using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Spawns a grabbable, resizable preview screen next to the handheld camera that mirrors the
/// camera's live feed. By default it exists only while the Camera HUD setting is on, the user is
/// in VR, and the camera is in direct-to-screen mode. It always shows the camera's own render
/// texture — there is a single render path — and <see cref="SetPreviewScreenVisible"/> overrides
/// the automatic gate in either direction.
/// </summary>
public partial class BasisHandHeldCamera
{
    [Header("Preview Screen")]
    /// <summary>Sideways offset from the camera, in meters at default avatar scale, where the screen spawns.</summary>
    public float previewScreenSideOffset = 0.35f;

    /// <summary>Spawn width of the screen, in meters at default avatar scale, before the user resizes it.</summary>
    public float previewScreenWidth = 0.45f;

    /// <summary>Smallest the screen can be resized to, as a percent of its spawn size (two-hand gesture).</summary>
    public float previewScreenMinScalePercent = 40f;

    /// <summary>Largest the screen can be resized to, as a percent of its spawn size (two-hand gesture).</summary>
    public float previewScreenMaxScalePercent = 1200f;

    /// <summary>Viewing distance the width and side offset above are authored for.</summary>
    public float previewScreenReferenceDistance = 0.5f;

    /// <summary>Closest the screen is allowed to sit to the viewer, in metres at default avatar scale.</summary>
    public float previewScreenMinDistance = 0.4f;

    /// <summary>Furthest the screen is allowed to sit from the viewer, in metres at default avatar scale.</summary>
    public float previewScreenMaxDistance = 1.5f;

    private GameObject previewScreenGO;
    private Material previewScreenMaterial;
    private BasisPickupInteractable previewScreenPickup;
    private bool previewScreenSubscribed;
    private bool? previewScreenOverride;

    /// <summary>Set once the user grabs the screen, after which it stays where they put it.</summary>
    private bool previewScreenUserPlaced;

    /// <summary>True while the preview screen is spawned.</summary>
    public bool IsPreviewScreenVisible => previewScreenGO != null;

    /// <summary>Forces the preview screen on or off, ignoring the automatic gate.</summary>
    public void SetPreviewScreenVisible(bool visible)
    {
        previewScreenOverride = visible;
        UpdatePreviewScreen();
    }

    /// <summary>Toggles the preview screen and pins it to that state.</summary>
    public void TogglePreviewScreen() => SetPreviewScreenVisible(!IsPreviewScreenVisible);

    /// <summary>Returns the preview screen to the automatic gate.</summary>
    public void ClearPreviewScreenOverride()
    {
        previewScreenOverride = null;
        UpdatePreviewScreen();
    }

    private RenderTexture PreviewScreenFeed =>
        // Single render path now: the camera always renders into its own RT (see
        // BasisHandHeldCameraDirectToScreen), so the preview screen never needs the copy RT.
        renderTexture;

    /// <summary>Subscribes to the Camera HUD setting so toggling it spawns/despawns the screen live.</summary>
    private void SubscribePreviewScreen()
    {
        if (previewScreenSubscribed) return;
        BasisSettingsDefaults.CameraHud.OnChanged += OnCameraHudSettingChanged;
        previewScreenSubscribed = true;
    }

    private void UnsubscribePreviewScreen()
    {
        if (!previewScreenSubscribed) return;
        BasisSettingsDefaults.CameraHud.OnChanged -= OnCameraHudSettingChanged;
        previewScreenSubscribed = false;
    }

    private void OnCameraHudSettingChanged(bool _) => UpdatePreviewScreen();

    /// <summary>
    /// Spawns or despawns the preview-screen pickup. An explicit override wins outright; otherwise
    /// the gate is the Camera HUD setting on, the user in VR, and the camera in direct-to-screen mode.
    /// </summary>
    private void UpdatePreviewScreen()
    {
        // Auto-follow deliberately does NOT auto-show the screen — the follow puck already marks
        // where the camera went, and toggling follow flipping the screen on/off was unwanted. The
        // manual panel toggle (previewScreenOverride) is the way to show it while following.
        // Flying still auto-shows because the camera is off in the world with no other marker.
        bool shouldShow = previewScreenOverride ?? (BasisSettingsDefaults.CameraHud.RawValue
            && (IsFlying
                || (BasisDeviceManagement.IsCurrentModeVR() && IsOverridingDesktopView)));

        if (shouldShow)
        {
            if (previewScreenGO == null)
            {
                SpawnPreviewScreen();
            }
        }
        else
        {
            DespawnPreviewScreen();
        }
    }

    private void SpawnPreviewScreen()
    {
        RenderTexture feed = PreviewScreenFeed;

        previewScreenGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        previewScreenGO.name = "CameraPreviewScreen";
        previewScreenGO.layer = LayerMask.NameToLayer("OverlayUI");

        if (previewScreenGO.TryGetComponent(out MeshCollider meshCollider))
        {
            DestroyImmediate(meshCollider);
        }
        BoxCollider box = previewScreenGO.AddComponent<BoxCollider>();
        box.size = new Vector3(1f, 1f, 0.1f);

        previewScreenMaterial = Material != null ? Instantiate(Material) : new Material(Shader.Find("Unlit/Texture"));
        if (previewScreenMaterial.HasProperty("_Cull"))
        {
            previewScreenMaterial.SetFloat("_Cull", 0f);
        }
        BindPreviewScreenFeed(feed);
        if (previewScreenGO.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.sharedMaterial = previewScreenMaterial;
        }

        previewScreenUserPlaced = false;
        PositionPreviewScreen();

        previewScreenPickup = previewScreenGO.AddComponent<BasisPickupInteractable>();
        previewScreenPickup.enableScaleWithGesture = true;
        previewScreenPickup.minScalePercent = previewScreenMinScalePercent;
        previewScreenPickup.maxScalePercent = previewScreenMaxScalePercent;
    }

    /// <summary>
    /// Places the screen on the line from the viewer to the camera, but at a distance of its
    /// own choosing.
    /// <para>
    /// The authored width and side offset describe how the screen should look from
    /// <see cref="previewScreenReferenceDistance"/>. Both are then scaled by the distance
    /// actually used, which is what holds the apparent size and the apparent gap constant:
    /// angular size is size over distance, so scaling size with distance cancels. Anchoring
    /// to the camera instead — as this did — meant the whole arrangement shrank with range
    /// until flying across the map left the PiP a speck, and the fixed metre offset went from
    /// a comfortable gap up close to invisible far away.
    /// </para>
    /// <para>
    /// The distance is clamped rather than tracked all the way out: a billboard hundreds of
    /// metres off would be scaled to match, land inside world geometry, and be occluded by
    /// everything between. Past the clamp only the direction is borrowed.
    /// </para>
    /// </summary>
    private void PositionPreviewScreen()
    {
        if (previewScreenGO == null) return;

        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        Vector3 headPos = BasisLocalCameraDriver.HeadPosition;
        Quaternion headRot = BasisLocalCameraDriver.HeadRotation;

        Vector3 forward = headRot * Vector3.forward;
        Vector3 toCamera = transform.position - headPos;
        Vector3 direction = toCamera.sqrMagnitude > 1e-6f ? toCamera.normalized : forward;

        // Fly the camera behind yourself and pointing at it would put the PiP behind your
        // head. Past 90 degrees off, fall back to straight ahead — being visible beats
        // pointing the right way.
        if (Vector3.Dot(direction, forward) <= 0f) direction = forward;

        float distance = Mathf.Clamp(toCamera.magnitude,
            previewScreenMinDistance * scale, previewScreenMaxDistance * scale);

        float reference = Mathf.Max(previewScreenReferenceDistance * scale, 1e-4f);
        float distanceRatio = distance / reference;

        // Horizontal right of the view direction, so the screen never rolls with head tilt.
        Vector3 right = Vector3.Cross(Vector3.up, direction);
        right = right.sqrMagnitude > 1e-6f ? right.normalized : headRot * Vector3.right;

        Vector3 position = headPos + direction * distance
            + right * (previewScreenSideOffset * scale * distanceRatio);
        Vector3 faceDir = position - headPos;
        Quaternion rotation = faceDir.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(faceDir, Vector3.up)
            : headRot;
        previewScreenGO.transform.SetPositionAndRotation(position, rotation);

        RenderTexture feed = PreviewScreenFeed;
        float aspect = (feed != null && feed.height > 0) ? (float)feed.width / feed.height : 16f / 9f;
        float width = previewScreenWidth * scale * distanceRatio;
        previewScreenGO.transform.localScale = new Vector3(width, width / aspect, 1f);
    }

    private void DespawnPreviewScreen()
    {
        previewScreenPickup = null;
        previewScreenUserPlaced = false;
        if (previewScreenGO != null)
        {
            Destroy(previewScreenGO);
            previewScreenGO = null;
        }
        if (previewScreenMaterial != null)
        {
            Destroy(previewScreenMaterial);
            previewScreenMaterial = null;
        }
    }

    /// <summary>
    /// Per-frame upkeep: re-evaluates the gate (fly and follow can start or stop at any
    /// moment), keeps the screen bound to the current direct-to-screen feed since the static
    /// RT can change, and re-anchors it to the viewer.
    /// </summary>
    private void UpdatePreviewScreenTexture()
    {
        UpdatePreviewScreen();
        if (previewScreenGO == null || previewScreenMaterial == null) return;

        BindPreviewScreenFeed(PreviewScreenFeed);

        // Follow the viewer until the user grabs it; after that it is theirs to place, and
        // re-anchoring would tear it out of their hand or snap it back on release.
        if (previewScreenPickup != null && previewScreenPickup.Inputs.AnyInteracting())
        {
            previewScreenUserPlaced = true;
        }
        if (!previewScreenUserPlaced) PositionPreviewScreen();
    }

    private void BindPreviewScreenFeed(RenderTexture feed)
    {
        if (feed == null || previewScreenMaterial == null) return;
        if (previewScreenMaterial.mainTexture == feed) return;
        previewScreenMaterial.mainTexture = feed;
        previewScreenMaterial.SetTexture("_MainTex", feed);
    }
}
