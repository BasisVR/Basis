using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>How the detached camera is marked while it is off in the world.</summary>
public enum BasisCameraDetachedMarker
{
    /// <summary>No marker.</summary>
    Off = 0,
    /// <summary>A solid puck model — the same one remote players see, and it's grabbable as a selfie stick.</summary>
    Puck = 1,
    /// <summary>A lightweight wireframe camera gizmo, grabbable by the knob on the end of its stick.</summary>
    Gizmo = 2,
}

/// <summary>
/// A local marker shown whenever the camera has left your hand — following, flying, or
/// world/playspace pinned — so you can see where it has gone, even when the camera body itself
/// is hidden. Either a solid puck (the model remote players see) or a lightweight wireframe
/// gizmo; both are grabbable as a selfie stick, through the same grip.
/// </summary>
public partial class BasisHandHeldCamera
{
    // Same addressable the network PIP driver instantiates. Its address is the full asset path
    // (see BasisNetworkPIPCameraDriver), and the prefab must stay in com.basis.sdk for it.
    private const string FollowPipPrefabAddress = "Packages/com.basis.sdk/Prefabs/UI/Camera Prefab/BasisCameraRemotePip.prefab";

    /// <summary>Which marker to show while the camera is detached. Puck by default.</summary>
    public BasisCameraDetachedMarker detachedMarker = BasisCameraDetachedMarker.Puck;

    /// <summary>How far the marker can be resized either way, as a ratio of its natural size. The
    /// same range the camera body's own two-hand resize is given, so neither reads as the odd one.</summary>
    public const float MinDetachedMarkerScale = 0.25f;
    public const float MaxDetachedMarkerScale = 4f;

    /// <summary>
    /// User resize of the detached marker, held as a ratio of <see cref="BaseDetachedMarkerScale"/>
    /// rather than as an absolute size. Both markers are avatar-relative, and the puck is rebuilt
    /// from its prefab every time the camera detaches — so an absolute size would be overwritten by
    /// the next height change and thrown away by the next respawn.
    /// </summary>
    private float detachedMarkerScale = 1f;

    /// <summary>Marker size as a ratio of its natural size; 1 is the size the puck prefab ships at.</summary>
    public float DetachedMarkerScale => detachedMarkerScale;

    /// <summary>
    /// The size both markers take with no user resize applied. Avatar-relative, the term the puck's
    /// parking offset and the wireframe's geometry already carry, so a resize is a ratio of what
    /// this avatar would otherwise be shown rather than of a fixed metre.
    /// </summary>
    public float BaseDetachedMarkerScale => BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

    /// <summary>
    /// Resizes the detached marker — both of them, since the size is the marker's, not the puck's.
    /// Takes a ratio of the natural size, clamped to <see cref="MinDetachedMarkerScale"/>..
    /// <see cref="MaxDetachedMarkerScale"/>, which is the range the pickup gesture is handed as
    /// well, so the panel slider and the two-hand pinch cannot disagree on how far the marker goes.
    /// </summary>
    public void SetDetachedMarkerScale(float scale)
    {
        // A file written before the setting existed arrives holding the constructor default, but a
        // hand-edited one can hold anything: read a nonsense size as the natural size rather than
        // clamping it to the smallest marker the panel is able to ask for.
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) scale = 1f;

        scale = Mathf.Clamp(scale, MinDetachedMarkerScale, MaxDetachedMarkerScale);
        if (Mathf.Approximately(scale, detachedMarkerScale)) return;

        detachedMarkerScale = scale;
        // The wireframe is rebuilt from this every frame; the puck is a spawned object, so it has
        // to be told.
        ApplyFollowPuckScale();
    }

    /// <summary>
    /// The layer both detached markers live on, or -1 where the project does not define it.
    /// OverlayUI is what the capture camera culls and what <see cref="ManagedCaptureLayers"/>
    /// keeps out of the Render Layers list, so nothing on it can reach a photo, a 360 or the
    /// video feed — while the player, whose camera does render it, still sees the marker.
    /// </summary>
    public static int MarkerLayer => LayerMask.NameToLayer("OverlayUI");

    /// <summary>
    /// How far out along the lens axis the puck is parked from the capture camera, in metres at
    /// default avatar scale.
    ///
    /// <para>It used to sit exactly on the camera, which is where the prop's own HUD is too, so on
    /// the frame the camera detaches the two are coincident: the puck landed in the middle of the
    /// panel and its grab box took the pointer the buttons under it wanted. The operator is always
    /// behind the lens — the same fact <see cref="TryGetFocusDepth"/> leans on — so parking the
    /// puck out along that axis puts it behind the panel from where they stand, where the panel
    /// hides it and is what a pointer reaches first.</para>
    /// </summary>
    private const float FollowPuckLensOffset = 0.25f;

    /// <summary>
    /// How far out the puck is parked at <paramref name="markerScale"/>, in metres at default
    /// avatar scale. A resized puck reaches further back toward the operator, so the parking
    /// distance grows with it or an enlarged marker lands back on the panel it was moved off. A
    /// shrunk one keeps the full distance instead of scaling down with it: what it is parked clear
    /// of is the panel and its buttons, and they do not get smaller with the marker.
    /// </summary>
    public static float FollowPuckParkDistance(float markerScale) =>
        FollowPuckLensOffset * Mathf.Max(1f, markerScale);

    /// <summary>Where the puck sits relative to a capture camera at <paramref name="rotation"/>, in world space.</summary>
    private Vector3 FollowPuckOffset(Quaternion rotation) =>
        rotation * new Vector3(0f, 0f, FollowPuckParkDistance(detachedMarkerScale) * BaseDetachedMarkerScale);

    private GameObject followPipInstance;
    private AsyncOperationHandle<GameObject> followPipHandle;
    private bool followPipLoading;
    private BasisCameraFollowPuckPickup followPipPickup;
    private bool followPipGrabbed;

    /// <summary>The puck prefab's own scale, so the resize stays a multiplier of the authored size.</summary>
    private Vector3 followPipPrefabScale = Vector3.one;

    /// <summary>The grab box measured off the model, in its own space. Zero where nothing was measurable.</summary>
    private Vector3 followPipMeasuredSize;
    private BoxCollider followPipGrabBox;

    /// <summary>Last size written to the live puck, so the per-frame re-apply is a comparison, not a write.</summary>
    private float appliedFollowPuckScale = -1f;

    /// <summary>
    /// The transform the selfie-stick grip is being held by: the puck itself, or the collider-only
    /// stand-in the wireframe spawns — a batched gizmo is a draw rather than a GameObject, so it
    /// has nothing on it for a hand to take hold of. Only one marker is ever out, so everything
    /// downstream of a grab is the same code path either way. Null while nothing is held.
    /// </summary>
    private Transform FollowGripTransform
    {
        get
        {
            if (followPipGrabbed && followPipInstance != null) return followPipInstance.transform;
            if (gizmoGripGrabbed && gizmoGripInstance != null) return gizmoGripInstance.transform;
            return null;
        }
    }

    /// <summary>While grabbed, the grip's transform is where the camera should be, less the parking offset.</summary>
    public bool TryGetFollowPipPose(out Vector3 pos, out Quaternion rot)
    {
        Transform grip = FollowGripTransform;
        if (grip != null)
        {
            grip.GetPositionAndRotation(out pos, out rot);
            // Undo the parking offset, or taking hold of the grip would jump the camera by it.
            pos -= FollowPuckOffset(rot);
            return true;
        }
        pos = default;
        rot = Quaternion.identity;
        return false;
    }

    /// <summary>Sets the detached-marker mode, tearing down whatever the previous mode had spawned.</summary>
    public void SetDetachedMarker(BasisCameraDetachedMarker mode)
    {
        if (detachedMarker == mode) return;
        detachedMarker = mode;
        // Drop the visuals the old mode owned; the next tick rebuilds for the new one.
        if (mode != BasisCameraDetachedMarker.Puck) DespawnFollowPip();
        if (mode != BasisCameraDetachedMarker.Gizmo) HideDetachedGizmo();
    }

    /// <summary>Per-frame: show the chosen marker while the camera is off in the world, else clear both.</summary>
    private void UpdateFollowPip()
    {
        bool detached = IsDetachedFromHand;

        if (!detached || detachedMarker != BasisCameraDetachedMarker.Puck)
        {
            DespawnFollowPip();
        }
        if (!detached || detachedMarker != BasisCameraDetachedMarker.Gizmo)
        {
            HideDetachedGizmo();
        }

        if (!detached) return;

        switch (detachedMarker)
        {
            case BasisCameraDetachedMarker.Puck:
                UpdateFollowPuck();
                break;
            case BasisCameraDetachedMarker.Gizmo:
                UpdateDetachedGizmo();
                break;
        }
    }

    private void UpdateFollowPuck()
    {
        if (followPipInstance == null)
        {
            SpawnFollowPip();
            return; // Positioned once it finishes loading, and every frame after.
        }

        // Re-applied every frame rather than only on a resize: the natural size follows the
        // avatar's height, which moves without anything telling the marker. Held as well as free —
        // resizing is done with both hands on it.
        ApplyFollowPuckScale();

        // While the player holds the puck it is the master — the camera tracks it (see
        // MoveCameraFlying), so leave the transform to the pickup and don't drive it from the camera.
        if (followPipGrabbed) return;

        captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
        followPipInstance.transform.SetPositionAndRotation(pos + FollowPuckOffset(rot), rot);
    }

    /// <summary>
    /// The pose every remote's copy of this camera's puck should be placed at.
    ///
    /// <para>The marker and the networked camera are the same prefab, so the two have to agree on
    /// where it is. The puck is parked out along the lens axis (see <see cref="FollowPuckLensOffset"/>),
    /// and the raw camera pose the send used to carry drew everyone else's copy back at the lens —
    /// a parking distance from where its owner is looking at it. The resize is what made that
    /// visible rather than what introduced it: the distance grows with the marker, so one at
    /// <see cref="MaxDetachedMarkerScale"/> misses by a metre.</para>
    ///
    /// <para>Only the puck moves. The wireframe is drawn at the camera and parks nothing but its
    /// grab knob out there, and a camera in the hand or with the marker off has no marker at all —
    /// in every one of those the lens is what a remote copy marks, and the pose passes through.</para>
    /// </summary>
    internal void GetNetworkedMarkerPose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (captureCamera == null) return;

        captureCamera.transform.GetPositionAndRotation(out position, out rotation);

        if (detachedMarker != BasisCameraDetachedMarker.Puck || !IsDetachedFromHand) return;

        // Its own transform rather than the offset re-applied: while the puck is held it is the
        // master and the camera is what follows it, through a smoothing a recomputed pose would
        // trail. Free, it is wherever UpdateFollowPuck just put it, which is the same answer.
        if (followPipInstance != null)
        {
            followPipInstance.transform.GetPositionAndRotation(out position, out rotation);

            // What this pose stands in for is the camera, and a held grip whose roll is not being
            // let through leaves the two disagreeing: the puck is banked in the hand and the shot
            // is not. Levelled to match, so a remote copy is banked the way the picture is. Only
            // while it is held — free of a hand the puck already carries the camera's own
            // rotation, whatever that is, and levelling would be the disagreement instead.
            if (followPipGrabbed) rotation = ApplyGripRoll(rotation, cameraRollEnabled);
            return;
        }

        // Still loading: nothing to read, so aim at where the puck lands on the frame it arrives
        // rather than letting the remote copy sit at the lens until then and jump.
        position += FollowPuckOffset(rotation);
    }

    private void SpawnFollowPip()
    {
        // Async load in flight, or the camera is gone: nothing to do this frame.
        if (followPipLoading || captureCamera == null) return;

        followPipLoading = true;
        followPipHandle = Addressables.LoadAssetAsync<GameObject>(FollowPipPrefabAddress);
        followPipHandle.Completed += handle =>
        {
            followPipLoading = false;

            // Follow may have ended, the mode changed, or the camera been destroyed while loading.
            if (this == null || detachedMarker != BasisCameraDetachedMarker.Puck || !IsDetachedFromHand || captureCamera == null)
            {
                if (handle.IsValid()) Addressables.Release(handle);
                return;
            }

            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                BasisDebug.LogError("Follow PIP prefab failed to load.", BasisDebug.LogTag.Camera);
                if (handle.IsValid()) Addressables.Release(handle);
                return;
            }

            captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
            followPipInstance = Instantiate(handle.Result, pos + FollowPuckOffset(rot), rot);
            followPipInstance.name = "FollowCameraPip";
            // Instantiate copies the prefab's scale, and the resize is a multiplier of it.
            followPipPrefabScale = followPipInstance.transform.localScale;
            appliedFollowPuckScale = -1f;
            RegisterSpawnedObject(followPipInstance);

            // Keep the marker out of the shot. The puck is parked out along the lens axis, square
            // in front of it, so the layer is the only thing keeping the capture from filming it.
            int overlayUi = MarkerLayer;
            if (overlayUi >= 0) SetLayerRecursively(followPipInstance, overlayUi);

            // Local-only marker: strip the networked-camera identity so nothing treats it as a
            // real remote PIP. Its own colliders stay off; grabbing goes through the box below.
            if (followPipInstance.TryGetComponent(out BasisCameraRemotePip remotePip)) Destroy(remotePip);
            foreach (Collider existing in followPipInstance.GetComponentsInChildren<Collider>(true))
            {
                existing.enabled = false;
            }

            MakeFollowPipGrabbable(followPipInstance);
            ApplyFollowPuckScale();
        };
    }

    /// <summary>Grab box a puck with nothing measurable gets, in metres at natural size.</summary>
    private const float FollowPuckFallbackGrabSize = 0.2f;

    /// <summary>Smallest grab box the puck keeps however far it is shrunk, in metres at natural size.</summary>
    private const float FollowPuckMinGrabSize = 0.08f;

    /// <summary>
    /// Adds a grab box + pickup so the puck acts as a selfie stick: while held the camera tracks
    /// it, and releasing hands control back to whatever the camera was doing (auto-follow resumes).
    /// The same grip carries the two-hand resize — see <see cref="BasisCameraFollowPuckPickup"/>.
    /// </summary>
    private void MakeFollowPipGrabbable(GameObject pip)
    {
        followPipGrabBox = pip.AddComponent<BoxCollider>();
        followPipMeasuredSize = Vector3.zero;
        if (TryGetLocalRendererBounds(pip, out Vector3 center, out Vector3 size))
        {
            followPipGrabBox.center = center;
            followPipMeasuredSize = size;
        }
        RefreshFollowPuckGrabBox();

        followPipPickup = pip.AddComponent<BasisCameraFollowPuckPickup>();
        followPipPickup.Owner = this;
        // Resizing the marker rides the preference that arms it on the camera body: it is the same
        // gesture, on a thing the same hands are holding.
        followPipPickup.enableScaleWithGesture = ResizeWithGesture;
        followPipPickup.minScalePercent = MinDetachedMarkerScale * 100f;
        followPipPickup.maxScalePercent = MaxDetachedMarkerScale * 100f;
        followPipPickup.OnInteractStartEvent.AddListener(_ => followPipGrabbed = true);
        followPipPickup.OnInteractEndEvent.AddListener(_ => followPipGrabbed = false);
    }

    /// <summary>Sizes a live puck to its natural size times the user resize. Cheap to call every frame.</summary>
    private void ApplyFollowPuckScale()
    {
        if (followPipInstance == null) return;

        float scale = BaseDetachedMarkerScale * detachedMarkerScale;
        if (Mathf.Approximately(scale, appliedFollowPuckScale)) return;

        appliedFollowPuckScale = scale;
        followPipInstance.transform.localScale = followPipPrefabScale * scale;
        RefreshFollowPuckGrabBox();
    }

    /// <summary>
    /// Re-fits the grab box to the resized puck. The box is authored in the model's own space, so
    /// the measured part tracks a resize on its own; what does not is the floor under it, which is
    /// there so a thin puck stays catchable — held against the avatar rather than the model, or
    /// shrinking the marker would take the last of the grab box down with it.
    /// </summary>
    private void RefreshFollowPuckGrabBox()
    {
        if (followPipGrabBox == null) return;

        // Against the model, not the world: the avatar term is in both sizes and cancels, which is
        // what keeps the grab box the same reach for a small avatar as for a large one.
        float modelScale = Mathf.Abs(followPipPrefabScale.x) * detachedMarkerScale;
        float floor = FollowPuckMinGrabSize / Mathf.Max(1e-4f, modelScale);

        followPipGrabBox.size = followPipMeasuredSize == Vector3.zero
            ? Vector3.one * Mathf.Max(FollowPuckFallbackGrabSize, floor)
            // A small grab margin over the model, and never below the floor.
            : Vector3.Max(followPipMeasuredSize * 1.2f, Vector3.one * floor);
    }

    /// <summary>
    /// Arms or disarms the marker's two-hand resize, following the camera body's own preference.
    /// A size already set is kept either way: the panel slider sets the same number, and putting
    /// the gesture away is not a request to undo what it did.
    /// </summary>
    internal void SetDetachedMarkerResizeWithGesture(bool enabled)
    {
        if (followPipPickup != null) followPipPickup.enableScaleWithGesture = enabled;
        if (gizmoGripPickup != null) gizmoGripPickup.enableScaleWithGesture = enabled;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        Transform t = root.transform;
        for (int Index = 0; Index < t.childCount; Index++)
        {
            SetLayerRecursively(t.GetChild(Index).gameObject, layer);
        }
    }

    private static bool TryGetLocalRendererBounds(GameObject root, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;

        Bounds world = renderers[0].bounds;
        for (int Index = 1; Index < renderers.Length; Index++) world.Encapsulate(renderers[Index].bounds);

        center = root.transform.InverseTransformPoint(world.center);
        Vector3 scale = root.transform.lossyScale;
        size = new Vector3(
            world.size.x / Mathf.Max(1e-4f, Mathf.Abs(scale.x)),
            world.size.y / Mathf.Max(1e-4f, Mathf.Abs(scale.y)),
            world.size.z / Mathf.Max(1e-4f, Mathf.Abs(scale.z)));
        return true;
    }

    private void DespawnFollowPip()
    {
        followPipGrabbed = false;
        followPipPickup = null;
        followPipGrabBox = null;
        followPipMeasuredSize = Vector3.zero;
        // The next puck is a fresh prefab instance, so nothing about the one being dropped —
        // least of all the size that was applied to it — carries over to sizing that one.
        followPipPrefabScale = Vector3.one;
        appliedFollowPuckScale = -1f;
        if (followPipInstance != null)
        {
            ForgetSpawnedObject(followPipInstance);
            Destroy(followPipInstance);
            followPipInstance = null;
        }
        if (followPipHandle.IsValid())
        {
            Addressables.Release(followPipHandle);
        }
    }

    // ---- Gizmo marker ---------------------------------------------------------------
    // A wireframe camera drawn through BasisGizmoManager: a small lens quad set behind the camera
    // plus four cone lines from its corners up to the lens, and out the front a short stick ending
    // in a knob — the selfie-stick grip, which the puck has as part of its model and this one has
    // to draw. Rendered whenever active regardless of the debug gizmo master toggle
    // (BasisGizmoManager.Render is not gated on it), so it works as a marker.
    //
    // Like the puck, it is kept out of the shot by living on MarkerLayer — the capture camera
    // culls it. Sitting behind the lens is not enough on its own: the batch is built at the tail
    // of LateUpdate from wherever the gizmo was last left, while the pose below is written in the
    // before-render pass, so the shot is taken one frame ahead of the geometry and any movement
    // between the two swings the marker into view. A 360 capture sees behind the camera anyway.

    private const float DetachedGizmoDepth = 0.18f;
    private const float DetachedGizmoHalfSize = 0.10f;
    private static readonly Color DetachedGizmoColor = new Color(0.2f, 0.9f, 1f, 1f);

    /// <summary>
    /// Diameter of the knob drawn on the end of the wireframe's stick, in metres at natural size.
    /// The grip itself is an invisible box, so the knob is the whole of what says the wireframe can
    /// be taken hold of — without it there is nothing to reach for.
    /// </summary>
    private const float DetachedGizmoKnobSize = 0.06f;

    private int _gizmoQuadId;
    private int[] _gizmoConeIds;
    private int _gizmoStickId;
    private int _gizmoKnobId;
    private bool _gizmoCreated;
    private readonly Vector3[] _gizmoQuad = new Vector3[4];

    /// <summary>
    /// The wireframe's grab handle, carrying the same pickup as the puck so the selfie-stick grip
    /// and the two-hand resize behave the same under either marker. Kept as its own object rather
    /// than folded into the puck's fields because <see cref="DespawnFollowPip"/> runs every frame
    /// the puck is not the chosen marker, and would clear a grip held on the wireframe.
    /// </summary>
    private GameObject gizmoGripInstance;
    private BasisCameraFollowPuckPickup gizmoGripPickup;
    private bool gizmoGripGrabbed;
    private bool gizmoGripLayered;

    private void UpdateDetachedGizmo()
    {
        if (captureCamera == null) { HideDetachedGizmo(); return; }

        captureCamera.transform.GetPositionAndRotation(out Vector3 apex, out Quaternion rot);
        // The user resize is the marker's, not the puck's: the wireframe is the other way of
        // showing the same thing, and one size control that only moved one of them would be a
        // setting that appears to do nothing half the time.
        float scale = BaseDetachedMarkerScale * detachedMarkerScale;
        float depth = DetachedGizmoDepth * scale;
        float half = DetachedGizmoHalfSize * scale;

        // Drawn behind the lens (negative Z in camera space) so it reads as a small camera icon
        // opening back toward the viewer rather than a cone across the subject. What keeps it out
        // of the shot is the layer, not the placement — see the note above.
        _gizmoQuad[0] = apex + rot * new Vector3(-half, -half, -depth);
        _gizmoQuad[1] = apex + rot * new Vector3(half, -half, -depth);
        _gizmoQuad[2] = apex + rot * new Vector3(half, half, -depth);
        _gizmoQuad[3] = apex + rot * new Vector3(-half, half, -depth);

        // The grip rides where the puck parks — see FollowPuckLensOffset — so a grab is the same
        // code path either way and the pointer still reaches the prop's panel before the grab box.
        // The camera has already been moved this frame (SimulateLate runs after it), so while the
        // grip is held apex is the pose it just dictated and the stick lands on the knob.
        Vector3 knob = apex + FollowPuckOffset(rot);
        float knobSize = DetachedGizmoKnobSize * scale;
        UpdateGizmoGrip(knob, rot, knobSize);

        if (!_gizmoCreated)
        {
            // A project without the marker layer hands back -1, which SetGizmoLayer reads as
            // "stay on the shared gizmo layer" — still a usable marker, just visible to the shot.
            int markerLayer = MarkerLayer;
            BasisGizmoManager.CreateLineGizmo("CameraDetachedGizmo", out _gizmoQuadId, _gizmoQuad, 0.004f, DetachedGizmoColor, loop: true);
            BasisGizmoManager.SetGizmoLayer(_gizmoQuadId, markerLayer);
            _gizmoConeIds = new int[4];
            for (int Index = 0; Index < 4; Index++)
            {
                BasisGizmoManager.CreateLineGizmo("CameraDetachedGizmo", out _gizmoConeIds[Index], apex, _gizmoQuad[Index], 0.003f, DetachedGizmoColor);
                BasisGizmoManager.SetGizmoLayer(_gizmoConeIds[Index], markerLayer);
            }
            BasisGizmoManager.CreateLineGizmo("CameraDetachedGizmo", out _gizmoStickId, apex, knob, 0.003f, DetachedGizmoColor);
            BasisGizmoManager.SetGizmoLayer(_gizmoStickId, markerLayer);
            BasisGizmoManager.CreateSphereGizmo("CameraDetachedGizmo", out _gizmoKnobId, knob, knobSize, DetachedGizmoColor);
            BasisGizmoManager.SetGizmoLayer(_gizmoKnobId, markerLayer);
            _gizmoCreated = true;
            return;
        }

        BasisGizmoManager.SetGizmoActive(_gizmoQuadId, true);
        BasisGizmoManager.UpdateLineGizmo(_gizmoQuadId, _gizmoQuad);
        for (int Index = 0; Index < 4; Index++)
        {
            BasisGizmoManager.SetGizmoActive(_gizmoConeIds[Index], true);
            BasisGizmoManager.UpdateLineGizmo(_gizmoConeIds[Index], apex, _gizmoQuad[Index]);
        }
        BasisGizmoManager.SetGizmoActive(_gizmoStickId, true);
        BasisGizmoManager.UpdateLineGizmo(_gizmoStickId, apex, knob);
        BasisGizmoManager.SetGizmoActive(_gizmoKnobId, true);
        BasisGizmoManager.UpdateSphereGizmo(_gizmoKnobId, knob, Vector3.one * knobSize);
    }

    /// <summary>
    /// Keeps the wireframe's grab handle alive and sitting under the drawn knob. Sized off the knob
    /// rather than measured, since a collider-only object has no renderer to measure, and re-sized
    /// every frame for the same reason the puck is: the natural size follows the avatar's height,
    /// which moves without anything telling the marker.
    /// </summary>
    private void UpdateGizmoGrip(Vector3 position, Quaternion rotation, float knobSize)
    {
        if (gizmoGripInstance == null)
        {
            SpawnGizmoGrip(position, rotation);
        }

        // A grab margin over the knob, never below the floor that keeps a shrunk marker catchable.
        // Carried on the transform over a unit box rather than in the collider, so the pickup's
        // hover highlight — cloned off that collider once, at Start — resizes with it.
        float grip = Mathf.Max(FollowPuckMinGrabSize, knobSize * 1.2f);
        gizmoGripInstance.transform.localScale = Vector3.one * grip;

        // The pickup builds its hover highlight from the collider at Start — a frame after the
        // spawn, and on whatever layer it is created on — so it has to be walked onto the marker
        // layer once it exists, or hovering the grip would put the highlight in the shot.
        if (!gizmoGripLayered && gizmoGripInstance.transform.childCount > 0)
        {
            int overlayUi = MarkerLayer;
            if (overlayUi >= 0) SetLayerRecursively(gizmoGripInstance, overlayUi);
            gizmoGripLayered = true;
        }

        // Held: the pickup owns the transform, and the camera is following it (MoveCameraFlying).
        if (gizmoGripGrabbed) return;

        gizmoGripInstance.transform.SetPositionAndRotation(position, rotation);
    }

    private void SpawnGizmoGrip(Vector3 position, Quaternion rotation)
    {
        gizmoGripInstance = new GameObject("FollowCameraGizmoGrip");
        gizmoGripInstance.transform.SetPositionAndRotation(position, rotation);
        gizmoGripLayered = false;

        int overlayUi = MarkerLayer;
        if (overlayUi >= 0) gizmoGripInstance.layer = overlayUi;

        // Registered like the puck so the camera's own click-to-focus knows this is its furniture
        // and not the scene: the grip sits a hand's width off the lens, which is exactly where a
        // focus pick would otherwise rack the plane to its minimum.
        RegisterSpawnedObject(gizmoGripInstance);

        // Collider before the pickup: the pickup resolves what it can be grabbed by in its Awake,
        // which AddComponent runs there and then. Left at its unit size — the grab volume is the
        // transform's, so that one number is also what the highlight mesh is cloned from.
        gizmoGripInstance.AddComponent<BoxCollider>();

        gizmoGripPickup = gizmoGripInstance.AddComponent<BasisCameraFollowPuckPickup>();
        gizmoGripPickup.Owner = this;
        gizmoGripPickup.enableScaleWithGesture = ResizeWithGesture;
        gizmoGripPickup.minScalePercent = MinDetachedMarkerScale * 100f;
        gizmoGripPickup.maxScalePercent = MaxDetachedMarkerScale * 100f;
        gizmoGripPickup.OnInteractStartEvent.AddListener(_ => gizmoGripGrabbed = true);
        gizmoGripPickup.OnInteractEndEvent.AddListener(_ => gizmoGripGrabbed = false);
    }

    /// <summary>Drops the wireframe's grab handle. Idempotent.</summary>
    private void DespawnGizmoGrip()
    {
        gizmoGripGrabbed = false;
        gizmoGripPickup = null;
        gizmoGripLayered = false;
        if (gizmoGripInstance == null) return;

        ForgetSpawnedObject(gizmoGripInstance);
        Destroy(gizmoGripInstance);
        gizmoGripInstance = null;
    }

    /// <summary>Parks the gizmo lines (kept for reuse) and drops its grab handle. Idempotent.</summary>
    private void HideDetachedGizmo()
    {
        // Before the _gizmoCreated gate: the grip is a real object in the world, and leaving one
        // behind a marker that is no longer drawn is a grab on thin air.
        DespawnGizmoGrip();

        if (!_gizmoCreated) return;
        BasisGizmoManager.SetGizmoActive(_gizmoQuadId, false);
        for (int Index = 0; Index < _gizmoConeIds.Length; Index++)
        {
            BasisGizmoManager.SetGizmoActive(_gizmoConeIds[Index], false);
        }
        BasisGizmoManager.SetGizmoActive(_gizmoStickId, false);
        BasisGizmoManager.SetGizmoActive(_gizmoKnobId, false);
    }

    /// <summary>Destroys the gizmo lines outright. Called from teardown.</summary>
    private void DestroyDetachedGizmo()
    {
        DespawnGizmoGrip();

        if (!_gizmoCreated) return;
        BasisGizmoManager.DestroyGizmo(_gizmoQuadId);
        for (int Index = 0; Index < _gizmoConeIds.Length; Index++)
        {
            BasisGizmoManager.DestroyGizmo(_gizmoConeIds[Index]);
        }
        BasisGizmoManager.DestroyGizmo(_gizmoStickId);
        BasisGizmoManager.DestroyGizmo(_gizmoKnobId);
        _gizmoCreated = false;
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>
    /// The pose handed to the network, so the marker the owner sees and the copy every remote draws
    /// can be asserted to agree without a peer, a puck prefab or a frame.
    /// </summary>
    public void GetNetworkedMarkerPoseForTest(out Vector3 position, out Quaternion rotation)
        => GetNetworkedMarkerPose(out position, out rotation);
#endif
}
