using Basis.Scripts.BasisSdk.Interactions;
using UnityEngine;

/// <summary>
/// The grab handle on the camera's follow puck. Holding it flies the camera — a selfie-stick grip —
/// and the two-hand pinch resizes the detached marker.
///
/// <para>The resize is routed onto the camera rather than written straight onto this transform,
/// because the puck is not what owns its size. It is rebuilt from its prefab every time the camera
/// detaches, so a size written here is thrown away by the next respawn — and the inherited gesture
/// measures its limits against the scale the object was spawned at, so each respawn would take the
/// last resize as the new natural size and the range would ratchet outwards a factor at a time.
/// Both are the reasons the camera body overrides these same two hooks.</para>
/// </summary>
public class BasisCameraFollowPuckPickup : BasisPickupInteractable
{
    /// <summary>The camera this puck marks, assigned by that camera as it spawns the puck.</summary>
    public BasisHandHeldCamera Owner;

    /// <inheritdoc/>
    protected override float GestureScaleReference =>
        Owner != null ? Owner.BaseDetachedMarkerScale : base.GestureScaleReference;

    /// <inheritdoc/>
    protected override void ApplyGestureScaleStep(BasisTransform.Direction scaleDirection, float stepSize, float minScale, float maxScale)
    {
        if (Owner == null)
        {
            base.ApplyGestureScaleStep(scaleDirection, stepSize, minScale, maxScale);
            return;
        }

        float baseScale = Owner.BaseDetachedMarkerScale;
        if (baseScale <= 0f)
        {
            return;
        }

        float step = scaleDirection == BasisTransform.Direction.Embiggen ? stepSize : -stepSize;
        float stepped = Mathf.Clamp(baseScale * Owner.DetachedMarkerScale + step, minScale, maxScale);

        // Back to a ratio of the natural size, which is what the camera keeps, what the panel
        // slider reads, and what the settings file stores.
        Owner.SetDetachedMarkerScale(stepped / baseScale);
    }
}
