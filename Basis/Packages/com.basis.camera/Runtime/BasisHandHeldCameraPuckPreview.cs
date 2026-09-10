using Basis.Scripts.Drivers;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    [Header("Puck Look-At Preview")]
    public bool puckLookAtPreview;
    public float puckPreviewShowAngle = 40f, puckPreviewHideAngle = 55f, puckPreviewLensClearance = 0.17f;
    public float puckPreviewMinViewerDistance = 0.35f, puckPreviewWidth = 0.2f, puckPreviewReferenceDistance = 1f;
    public float puckPreviewMaxGrowth = 6f;
    private GameObject puckPreviewInstance;
    private Material puckPreviewMaterial;
    public bool IsPuckPreviewVisible => puckPreviewInstance != null;
    public void SetPuckLookAtPreview(bool enabled)
    {
        if (puckLookAtPreview == enabled) return;
        puckLookAtPreview = enabled;
        if (!enabled) DespawnPuckPreview();
    }
    private void UpdatePuckPreview()
    {
        bool wanted = puckLookAtPreview && IsDetachedFromHand && BodyAllowsLiveFeed && captureCamera != null && BasisLocalCameraDriver.HasInstance;

        if (wanted)
        {
            captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
            wanted = BasisCameraPuckPreview.ShouldShow(pos, rot, BasisLocalCameraDriver.HeadPosition, IsPuckPreviewVisible, puckPreviewShowAngle, puckPreviewHideAngle);
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
        puckPreviewInstance = BasisCameraPuckPreview.CreateQuad(Material, out puckPreviewMaterial);
        if (puckPreviewInstance == null) return;

        PositionPuckPreview();
        UpdateRenderGate();
    }
    private void PositionPuckPreview()
    {
        if (puckPreviewInstance == null || captureCamera == null) return;

        float scale = BaseDetachedMarkerScale;
        captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
        Vector3 forward = camRot * Vector3.forward;
        Vector3 headPos = BasisLocalCameraDriver.HasInstance ? BasisLocalCameraDriver.HeadPosition : camPos - forward;
        Vector3 position = camPos + forward * (BasisCameraPuckPreview.ParkDistance(DetachedMarkerScale, puckPreviewLensClearance) * scale);
        Vector3 fromHead = position - headPos;
        float minDistance = puckPreviewMinViewerDistance * scale;
        if (fromHead.magnitude < minDistance)
        {
            Vector3 away = fromHead.sqrMagnitude > 1e-6f ? fromHead.normalized : -forward;
            position = headPos + away * minDistance;
            fromHead = position - headPos;
        }

        Quaternion rotation = fromHead.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fromHead.normalized, Vector3.up) : camRot;
        puckPreviewInstance.transform.SetPositionAndRotation(position, rotation);

        RenderTexture feed = ViewfinderTexture;
        float aspect = feed != null && feed.height > 0 ? (float)feed.width / feed.height : 16f / 9f;
        float width = puckPreviewWidth * scale * BasisCameraPuckPreview.Growth(fromHead.magnitude, puckPreviewReferenceDistance * scale, puckPreviewMaxGrowth);
        puckPreviewInstance.transform.localScale = new Vector3(width, width / aspect, 1f);
    }
    private void BindPuckPreviewFeed()
    {
        RenderTexture feed = ViewfinderTexture;
        if (feed != null && puckPreviewMaterial != null && puckPreviewMaterial.mainTexture != feed) BasisCameraRenderTargets.Bind(puckPreviewMaterial, feed);
    }
    private void DespawnPuckPreview()
    {
        bool wasUp = puckPreviewInstance != null;
        BasisCameraRenderTargets.DestroyAndClear(ref puckPreviewInstance);
        BasisCameraRenderTargets.DestroyAndClear(ref puckPreviewMaterial);
        if (wasUp) UpdateRenderGate();
    }
}
