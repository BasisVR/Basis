using UnityEngine;
public sealed class BasisCameraWireframeMarker
{
    public const float KnobSize = 0.06f;
    private const float Depth = 0.18f, HalfSize = 0.10f;
    private const string GizmoName = "CameraDetachedGizmo";
    private static readonly Color Colour = new Color(0.2f, 0.9f, 1f, 1f);
    private readonly Vector3[] quad = new Vector3[4];
    private readonly int[] coneIds = new int[4];
    private int quadId, stickId, knobId;
    private bool created;
    public void Draw(Vector3 apex, Quaternion rotation, float scale, Vector3 knob, float knobSize)
    {
        float depth = Depth * scale, half = HalfSize * scale;
        quad[0] = apex + rotation * new Vector3(-half, -half, -depth);
        quad[1] = apex + rotation * new Vector3(half, -half, -depth);
        quad[2] = apex + rotation * new Vector3(half, half, -depth);
        quad[3] = apex + rotation * new Vector3(-half, half, -depth);

        if (!created)
        {
            int layer = BasisCameraCaptureLayers.Marker;
            BasisGizmoManager.CreateLineGizmo(GizmoName, out quadId, quad, 0.004f, Colour, loop: true);
            BasisGizmoManager.SetGizmoLayer(quadId, layer);
            for (int Index = 0; Index < 4; Index++)
            {
                BasisGizmoManager.CreateLineGizmo(GizmoName, out coneIds[Index], apex, quad[Index], 0.003f, Colour);
                BasisGizmoManager.SetGizmoLayer(coneIds[Index], layer);
            }
            BasisGizmoManager.CreateLineGizmo(GizmoName, out stickId, apex, knob, 0.003f, Colour);
            BasisGizmoManager.SetGizmoLayer(stickId, layer);
            BasisGizmoManager.CreateSphereGizmo(GizmoName, out knobId, knob, knobSize, Colour);
            BasisGizmoManager.SetGizmoLayer(knobId, layer);
            created = true;
            return;
        }

        BasisGizmoManager.SetGizmoActive(quadId, true);
        BasisGizmoManager.UpdateLineGizmo(quadId, quad);
        for (int Index = 0; Index < 4; Index++)
        {
            BasisGizmoManager.SetGizmoActive(coneIds[Index], true);
            BasisGizmoManager.UpdateLineGizmo(coneIds[Index], apex, quad[Index]);
        }
        BasisGizmoManager.SetGizmoActive(stickId, true);
        BasisGizmoManager.UpdateLineGizmo(stickId, apex, knob);
        BasisGizmoManager.SetGizmoActive(knobId, true);
        BasisGizmoManager.UpdateSphereGizmo(knobId, knob, Vector3.one * knobSize);
    }
    public void Hide()
    {
        if (!created) return;
        BasisGizmoManager.SetGizmoActive(quadId, false);
        for (int Index = 0; Index < coneIds.Length; Index++) BasisGizmoManager.SetGizmoActive(coneIds[Index], false);
        BasisGizmoManager.SetGizmoActive(stickId, false);
        BasisGizmoManager.SetGizmoActive(knobId, false);
    }
    public void Destroy()
    {
        if (!created) return;
        BasisGizmoManager.DestroyGizmo(quadId);
        for (int Index = 0; Index < coneIds.Length; Index++) BasisGizmoManager.DestroyGizmo(coneIds[Index]);
        BasisGizmoManager.DestroyGizmo(stickId);
        BasisGizmoManager.DestroyGizmo(knobId);
        created = false;
    }
}
