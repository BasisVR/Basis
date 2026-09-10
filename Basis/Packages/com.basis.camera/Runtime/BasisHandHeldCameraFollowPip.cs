using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
public partial class BasisHandHeldCamera
{
    public BasisCameraDetachedMarker detachedMarker = BasisCameraDetachedMarker.Puck;
    private readonly BasisCameraWireframeMarker wireframeMarker = new BasisCameraWireframeMarker();
    private GameObject followPipInstance, gizmoGripInstance;
    private AsyncOperationHandle<GameObject> followPipHandle;
    private BasisCameraFollowPuckPickup followPipPickup, gizmoGripPickup;
    private BoxCollider followPipGrabBox;
    private Vector3 followPipPrefabScale = Vector3.one, followPipMeasuredSize;
    private float detachedMarkerScale = 1f, appliedFollowPuckScale = -1f;
    private bool followPipLoading, followPipGrabbed, gizmoGripGrabbed, gizmoGripLayered;
    public float DetachedMarkerScale => detachedMarkerScale;
    public float BaseDetachedMarkerScale => BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
    private Transform FollowGripTransform
    {
        get
        {
            if (followPipGrabbed && followPipInstance != null) return followPipInstance.transform;
            if (gizmoGripGrabbed && gizmoGripInstance != null) return gizmoGripInstance.transform;
            return null;
        }
    }
    public void SetDetachedMarkerScale(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) scale = 1f;

        scale = Mathf.Clamp(scale, BasisCameraDetachedMarkers.MinScale, BasisCameraDetachedMarkers.MaxScale);
        if (Mathf.Approximately(scale, detachedMarkerScale)) return;

        detachedMarkerScale = scale;
        ApplyFollowPuckScale();
    }
    public bool TryGetFollowPipPose(out Vector3 pos, out Quaternion rot)
    {
        Transform grip = FollowGripTransform;
        if (grip != null)
        {
            grip.GetPositionAndRotation(out pos, out rot);
            pos -= FollowPuckOffset(rot);
            return true;
        }
        pos = default;
        rot = Quaternion.identity;
        return false;
    }
    public void SetDetachedMarker(BasisCameraDetachedMarker mode)
    {
        if (detachedMarker == mode) return;
        detachedMarker = mode;
        if (mode != BasisCameraDetachedMarker.Puck) DespawnFollowPip();
        if (mode != BasisCameraDetachedMarker.Gizmo) HideDetachedGizmo();
    }
    internal void GetNetworkedMarkerPose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (captureCamera == null) return;

        captureCamera.transform.GetPositionAndRotation(out position, out rotation);

        if (detachedMarker != BasisCameraDetachedMarker.Puck || !IsDetachedFromHand) return;

        if (followPipInstance != null)
        {
            followPipInstance.transform.GetPositionAndRotation(out position, out rotation);
            if (followPipGrabbed) rotation = ApplyGripRoll(rotation, cameraRollEnabled);
            return;
        }

        position += FollowPuckOffset(rotation);
    }
    internal void SetDetachedMarkerResizeWithGesture(bool enabled)
    {
        if (followPipPickup != null) followPipPickup.enableScaleWithGesture = enabled;
        if (gizmoGripPickup != null) gizmoGripPickup.enableScaleWithGesture = enabled;
    }
    private Vector3 FollowPuckOffset(Quaternion rotation) => rotation * new Vector3(0f, 0f, BasisCameraDetachedMarkers.ParkDistance(detachedMarkerScale) * BaseDetachedMarkerScale);
    private void UpdateFollowPip()
    {
        bool detached = IsDetachedFromHand;

        if (!detached || detachedMarker != BasisCameraDetachedMarker.Puck) DespawnFollowPip();
        if (!detached || detachedMarker != BasisCameraDetachedMarker.Gizmo) HideDetachedGizmo();

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
            return;
        }

        ApplyFollowPuckScale();

        if (followPipGrabbed) return;

        captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
        followPipInstance.transform.SetPositionAndRotation(pos + FollowPuckOffset(rot), rot);
    }
    private void SpawnFollowPip()
    {
        if (followPipLoading || captureCamera == null) return;

        followPipLoading = true;
        followPipHandle = Addressables.LoadAssetAsync<GameObject>(BasisCameraDetachedMarkers.PuckPrefabAddress);
        followPipHandle.Completed += handle =>
        {
            followPipLoading = false;

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
            followPipPrefabScale = followPipInstance.transform.localScale;
            appliedFollowPuckScale = -1f;
            RegisterSpawnedObject(followPipInstance);

            int overlayUi = BasisCameraCaptureLayers.Marker;
            if (overlayUi >= 0) BasisCameraCaptureLayers.SetLayerRecursively(followPipInstance, overlayUi);

            if (followPipInstance.TryGetComponent(out BasisCameraRemotePip remotePip)) Destroy(remotePip);
            foreach (Collider existing in followPipInstance.GetComponentsInChildren<Collider>(true)) existing.enabled = false;

            MakeFollowPipGrabbable(followPipInstance);
            ApplyFollowPuckScale();
        };
    }
    private void MakeFollowPipGrabbable(GameObject pip)
    {
        followPipGrabBox = pip.AddComponent<BoxCollider>();
        followPipMeasuredSize = Vector3.zero;
        if (BasisCameraDetachedMarkers.TryGetLocalRendererBounds(pip, out Vector3 center, out Vector3 size))
        {
            followPipGrabBox.center = center;
            followPipMeasuredSize = size;
        }
        RefreshFollowPuckGrabBox();

        followPipPickup = CreateGripPickup(pip);
        followPipPickup.OnInteractStartEvent.AddListener(_ => followPipGrabbed = true);
        followPipPickup.OnInteractEndEvent.AddListener(_ => followPipGrabbed = false);
    }
    private BasisCameraFollowPuckPickup CreateGripPickup(GameObject host)
    {
        BasisCameraFollowPuckPickup pickup = host.AddComponent<BasisCameraFollowPuckPickup>();
        pickup.Owner = this;
        pickup.enableScaleWithGesture = ResizeWithGesture;
        pickup.minScalePercent = BasisCameraDetachedMarkers.MinScale * 100f;
        pickup.maxScalePercent = BasisCameraDetachedMarkers.MaxScale * 100f;
        return pickup;
    }
    private void ApplyFollowPuckScale()
    {
        if (followPipInstance == null) return;

        float scale = BaseDetachedMarkerScale * detachedMarkerScale;
        if (Mathf.Approximately(scale, appliedFollowPuckScale)) return;

        appliedFollowPuckScale = scale;
        followPipInstance.transform.localScale = followPipPrefabScale * scale;
        RefreshFollowPuckGrabBox();
    }
    private void RefreshFollowPuckGrabBox()
    {
        if (followPipGrabBox != null) followPipGrabBox.size = BasisCameraDetachedMarkers.GrabBoxSize(followPipMeasuredSize, followPipPrefabScale.x, detachedMarkerScale);
    }
    private void DespawnFollowPip()
    {
        followPipGrabbed = false;
        followPipPickup = null;
        followPipGrabBox = null;
        followPipMeasuredSize = Vector3.zero;
        followPipPrefabScale = Vector3.one;
        appliedFollowPuckScale = -1f;
        if (followPipInstance != null)
        {
            ForgetSpawnedObject(followPipInstance);
            Destroy(followPipInstance);
            followPipInstance = null;
        }
        if (followPipHandle.IsValid()) Addressables.Release(followPipHandle);
    }
    private void UpdateDetachedGizmo()
    {
        if (captureCamera == null)
        {
            HideDetachedGizmo();
            return;
        }

        captureCamera.transform.GetPositionAndRotation(out Vector3 apex, out Quaternion rot);
        float scale = BaseDetachedMarkerScale * detachedMarkerScale, knobSize = BasisCameraWireframeMarker.KnobSize * scale;
        Vector3 knob = apex + FollowPuckOffset(rot);

        UpdateGizmoGrip(knob, rot, knobSize);
        wireframeMarker.Draw(apex, rot, scale, knob, knobSize);
    }
    private void UpdateGizmoGrip(Vector3 position, Quaternion rotation, float knobSize)
    {
        if (gizmoGripInstance == null) SpawnGizmoGrip(position, rotation);

        gizmoGripInstance.transform.localScale = Vector3.one * BasisCameraDetachedMarkers.GripSize(knobSize);

        if (!gizmoGripLayered && gizmoGripInstance.transform.childCount > 0)
        {
            int overlayUi = BasisCameraCaptureLayers.Marker;
            if (overlayUi >= 0) BasisCameraCaptureLayers.SetLayerRecursively(gizmoGripInstance, overlayUi);
            gizmoGripLayered = true;
        }

        if (!gizmoGripGrabbed) gizmoGripInstance.transform.SetPositionAndRotation(position, rotation);
    }
    private void SpawnGizmoGrip(Vector3 position, Quaternion rotation)
    {
        gizmoGripInstance = new GameObject("FollowCameraGizmoGrip");
        gizmoGripInstance.transform.SetPositionAndRotation(position, rotation);
        gizmoGripLayered = false;

        int overlayUi = BasisCameraCaptureLayers.Marker;
        if (overlayUi >= 0) gizmoGripInstance.layer = overlayUi;

        RegisterSpawnedObject(gizmoGripInstance);
        gizmoGripInstance.AddComponent<BoxCollider>();

        gizmoGripPickup = CreateGripPickup(gizmoGripInstance);
        gizmoGripPickup.OnInteractStartEvent.AddListener(_ => gizmoGripGrabbed = true);
        gizmoGripPickup.OnInteractEndEvent.AddListener(_ => gizmoGripGrabbed = false);
    }
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
    private void HideDetachedGizmo()
    {
        DespawnGizmoGrip();
        wireframeMarker.Hide();
    }
    private void DestroyDetachedGizmo()
    {
        DespawnGizmoGrip();
        wireframeMarker.Destroy();
    }
#if UNITY_INCLUDE_TESTS
    public void GetNetworkedMarkerPoseForTest(out Vector3 position, out Quaternion rotation) => GetNetworkedMarkerPose(out position, out rotation);
#endif
}
