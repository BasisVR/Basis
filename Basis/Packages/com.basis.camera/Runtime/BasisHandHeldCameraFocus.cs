using UnityEngine;
public partial class BasisHandHeldCamera
{
    [SerializeField] public float focusRackSeconds = 0.5f;
    private float focusRackFrom, focusRackTo, focusRackElapsed;
    private bool focusRacking;
    public bool IsRackingFocus => focusRacking;
    public float FocusRackTarget => focusRacking ? focusRackTo : (MetaData != null && MetaData.depthOfField != null ? MetaData.depthOfField.focusDistance.value : 0f);
    public bool CanAutoFocusOnFollowSubject => IsFollowingRemotePlayer || (IsModifierDriven && Modifiers.ResolvesSubject);
    public bool AutoFocusHasNoSubject => autoFocusFollowSubject && !CanAutoFocusOnFollowSubject;
    public float MinimumFocusDistance => BasisCameraFocusMath.MinimumDistance(MetaData?.depthOfField);
    public bool TryGetFocusDepth(Vector3 worldPoint, out float depth) => BasisCameraFocusMath.TryGetDepth(captureCamera, worldPoint, MinimumFocusDistance, out depth);
    public void ApplyFocusDistance(float metres)
    {
        focusRacking = false;
        SetFocusDistance(metres);
    }
    public void RefreshFocusDistance()
    {
        if (MetaData == null || MetaData.depthOfField == null) return;
        SetFocusDistance(MetaData.depthOfField.focusDistance.value);
    }
    public void RackFocusTo(float metres)
    {
        if (MetaData == null || MetaData.depthOfField == null) return;

        float target = Mathf.Max(MinimumFocusDistance, metres);
        float current = Mathf.Max(MinimumFocusDistance, MetaData.depthOfField.focusDistance.value);

        if (focusRackSeconds <= 0f || BasisCameraFocusMath.IsRackNegligible(current, target))
        {
            focusRacking = false;
            SetFocusDistance(target);
            HandHeld?.SyncFocusReadout();
            return;
        }

        focusRackFrom = current;
        focusRackTo = target;
        focusRackElapsed = 0f;
        focusRacking = true;
    }
    private void SetFocusDistance(float metres)
    {
        if (MetaData == null || MetaData.depthOfField == null) return;
        BasisCameraFocusMath.Apply(MetaData.depthOfField, Mathf.Max(MinimumFocusDistance, metres));
    }
    private void UpdateAutoFocus()
    {
        if (!autoFocusFollowSubject || MetaData.depthOfField == null || captureCamera == null) return;
        if (!MetaData.depthOfField.active || !CanAutoFocusOnFollowSubject) return;
        if (!TryGetFollowFocusPoint(out Vector3 point) || !TryGetFocusDepth(point, out float depth)) return;

        ApplyFocusDistance(BasisCameraFocusMath.AutoFocusStep(MetaData.depthOfField.focusDistance.value, depth, Time.deltaTime));
    }
    private void TickFocusRack()
    {
        if (!focusRacking) return;
        if (MetaData == null || MetaData.depthOfField == null)
        {
            focusRacking = false;
            return;
        }

        focusRackElapsed += Time.deltaTime;
        float t = focusRackSeconds > 0f ? Mathf.Clamp01(focusRackElapsed / focusRackSeconds) : 1f;

        SetFocusDistance(BasisCameraFocusMath.SampleRack(focusRackFrom, focusRackTo, t));
        HandHeld?.SyncFocusReadout();

        if (t >= 1f) focusRacking = false;
    }
}
