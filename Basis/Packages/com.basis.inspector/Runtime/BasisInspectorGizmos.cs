using System;
using System.Collections.Generic;
using UnityEngine;

[Flags]
public enum BasisInspectorGizmoKind { None = 0, Axes = 1, Bounds = 2, Colliders = 4, Ranges = 8, Camera = 16, Children = 32, Label = 64 }

public sealed class BasisInspectorGizmos
{
    public const int CircleSegments = 32, MaxChildren = 256;
    const float AxisLength = 0.25f, MinWidth = 0.0015f, WidthPerMeter = 0.0015f, MaxFrustumDistance = 10f, LabelScale = 0.014f, NodeSize = 0.02f;
    static readonly Color AxisX = new Color(1f, 0.3f, 0.3f, 1f), AxisY = new Color(0.4f, 1f, 0.4f, 1f), AxisZ = new Color(0.35f, 0.55f, 1f, 1f);
    static readonly Color BoundsColor = new Color(1f, 0.85f, 0.3f, 1f), LocalBoundsColor = new Color(0.31f, 0.82f, 0.77f, 1f), ColliderColor = new Color(0.55f, 1f, 0.55f, 1f), TriggerColor = new Color(0.55f, 1f, 0.55f, 0.5f);
    static readonly Color LightColor = new Color(1f, 0.92f, 0.5f, 1f), AudioMinColor = new Color(1f, 0.85f, 0.4f, 1f), AudioMaxColor = new Color(1f, 0.6f, 0.3f, 1f), ProbeColor = new Color(0.55f, 0.85f, 1f, 1f), CameraColor = new Color(0.35f, 0.78f, 0.98f, 1f), ChildColor = new Color(0.78f, 0.78f, 0.86f, 0.8f), LabelColor = Color.white;
    readonly BasisGizmoSet set = new BasisGizmoSet("Inspector");
    readonly Vector3[] circle = new Vector3[CircleSegments], corners = new Vector3[8], near = new Vector3[4], far = new Vector3[4];
    bool hooked, drawn;
    float width;

    void Hook()
    {
        if (hooked) return;
        hooked = true;
        BasisGizmoManager.OnUseGizmosChanged += OnMasterToggle;
    }

    void OnMasterToggle(bool state)
    {
        if (!state) set.Forget();
    }

    public void Dispose()
    {
        if (hooked) BasisGizmoManager.OnUseGizmosChanged -= OnMasterToggle;
        hooked = false;
        set.Clear();
        drawn = false;
    }

    public void Draw(Transform target, List<Component> components, BasisInspectorGizmoKind kinds, Vector3 viewer)
    {
        if (target == null || kinds == BasisInspectorGizmoKind.None)
        {
            if (!drawn) return;
            set.Begin();
            set.End();
            drawn = false;
            return;
        }
        Hook();
        drawn = true;
        Vector3 origin = target.position;
        width = Mathf.Max(MinWidth, Vector3.Distance(viewer, origin) * WidthPerMeter);
        set.Begin();
        if ((kinds & BasisInspectorGizmoKind.Axes) != 0)
        {
            set.Line(origin, origin + target.right * AxisLength, AxisX, width);
            set.Line(origin, origin + target.up * AxisLength, AxisY, width);
            set.Line(origin, origin + target.forward * AxisLength, AxisZ, width);
        }
        for (int i = 0; i < components.Count; i++)
        {
            Component component = components[i];
            if (component == null) continue;
            switch (component)
            {
                case Renderer renderer when (kinds & BasisInspectorGizmoKind.Bounds) != 0:
                    DrawRendererBounds(renderer);
                    break;
                case Collider collider when (kinds & BasisInspectorGizmoKind.Colliders) != 0:
                    DrawCollider(collider);
                    break;
                case Light light when (kinds & BasisInspectorGizmoKind.Ranges) != 0:
                    DrawLight(light);
                    break;
                case AudioSource audio when (kinds & BasisInspectorGizmoKind.Ranges) != 0:
                    if (audio.spatialBlend > 0f)
                    {
                        WireSphere(audio.transform.position, Quaternion.identity, audio.minDistance, AudioMinColor);
                        WireSphere(audio.transform.position, Quaternion.identity, audio.maxDistance, AudioMaxColor);
                    }
                    break;
                case ReflectionProbe probe when (kinds & BasisInspectorGizmoKind.Ranges) != 0:
                    Box(Matrix4x4.identity, probe.bounds.center, probe.bounds.size, ProbeColor);
                    break;
                case Camera camera when (kinds & BasisInspectorGizmoKind.Camera) != 0:
                    DrawFrustum(camera);
                    break;
            }
        }
        if ((kinds & BasisInspectorGizmoKind.Children) != 0)
        {
            int count = Mathf.Min(target.childCount, MaxChildren);
            for (int i = 0; i < count; i++)
            {
                Vector3 child = target.GetChild(i).position;
                set.Line(origin, child, ChildColor, width * 0.6f);
                set.Sphere(child, NodeSize, ChildColor);
            }
        }
        if ((kinds & BasisInspectorGizmoKind.Label) != 0) set.Label(origin + Vector3.up * (AxisLength * 0.5f), BasisInspectorReflection.Clip(target.name), LabelColor, viewer, LabelScale * Mathf.Max(1f, Vector3.Distance(viewer, origin)));
        set.End();
    }

    void DrawRendererBounds(Renderer renderer)
    {
        if (!renderer.enabled) return;
        Bounds world = renderer.bounds;
        Box(Matrix4x4.identity, world.center, world.size, BoundsColor);
        Bounds local = renderer.localBounds;
        Box(renderer.transform.localToWorldMatrix, local.center, local.size, LocalBoundsColor);
    }

    void DrawCollider(Collider collider)
    {
        Color color = collider.isTrigger ? TriggerColor : ColliderColor;
        Transform t = collider.transform;
        switch (collider)
        {
            case BoxCollider box:
                Box(t.localToWorldMatrix, box.center, box.size, color);
                return;
            case SphereCollider sphere:
            {
                Vector3 scale = Abs(t.lossyScale);
                WireSphere(t.TransformPoint(sphere.center), t.rotation, sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)), color);
                return;
            }
            case CapsuleCollider capsule:
                Capsule(t, capsule.center, capsule.radius, capsule.height, capsule.direction, color);
                return;
            case CharacterController controller:
                Capsule(t, controller.center, controller.radius, controller.height, 1, color);
                return;
        }
        Bounds bounds = collider.bounds;
        Box(Matrix4x4.identity, bounds.center, bounds.size, color);
    }

    void DrawLight(Light light)
    {
        Transform t = light.transform;
        Vector3 position = t.position;
        switch (light.type)
        {
            case LightType.Point:
                WireSphere(position, Quaternion.identity, light.range, LightColor);
                return;
            case LightType.Spot:
            {
                float range = light.range, radius = Mathf.Tan(light.spotAngle * 0.5f * Mathf.Deg2Rad) * range;
                Vector3 end = position + t.forward * range;
                Circle(end, t.rotation, radius, 2, LightColor);
                set.Line(position, end + t.up * radius, LightColor, width);
                set.Line(position, end - t.up * radius, LightColor, width);
                set.Line(position, end + t.right * radius, LightColor, width);
                set.Line(position, end - t.right * radius, LightColor, width);
                return;
            }
            case LightType.Directional:
            {
                Vector3 tip = position + t.forward;
                set.Line(position, tip, LightColor, width);
                set.Line(tip, tip - t.forward * 0.15f + t.up * 0.08f, LightColor, width);
                set.Line(tip, tip - t.forward * 0.15f - t.up * 0.08f, LightColor, width);
                return;
            }
        }
    }

    void DrawFrustum(Camera camera)
    {
        Transform t = camera.transform;
        Rect viewport = new Rect(0f, 0f, 1f, 1f);
        camera.CalculateFrustumCorners(viewport, camera.nearClipPlane, Camera.MonoOrStereoscopicEye.Mono, near);
        camera.CalculateFrustumCorners(viewport, Mathf.Min(camera.farClipPlane, MaxFrustumDistance), Camera.MonoOrStereoscopicEye.Mono, far);
        for (int i = 0; i < 4; i++)
        {
            near[i] = t.TransformPoint(near[i]);
            far[i] = t.TransformPoint(far[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) & 3;
            set.Line(near[i], near[next], CameraColor, width);
            set.Line(far[i], far[next], CameraColor, width);
            set.Line(near[i], far[i], CameraColor, width);
        }
    }

    void Box(Matrix4x4 matrix, Vector3 center, Vector3 size, Color color)
    {
        Vector3 e = size * 0.5f;
        for (int i = 0; i < 8; i++) corners[i] = matrix.MultiplyPoint3x4(center + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z));
        for (int i = 0; i < 8; i++)
        {
            if ((i & 1) == 0) set.Line(corners[i], corners[i | 1], color, width);
            if ((i & 2) == 0) set.Line(corners[i], corners[i | 2], color, width);
            if ((i & 4) == 0) set.Line(corners[i], corners[i | 4], color, width);
        }
    }

    void Circle(Vector3 center, Quaternion rotation, float radius, int axis, Color color)
    {
        if (radius <= 0f || float.IsNaN(radius) || float.IsInfinity(radius)) return;
        Vector3 u = rotation * (axis == 0 ? Vector3.up : Vector3.right), v = rotation * (axis == 2 ? Vector3.up : Vector3.forward);
        if (axis == 1)
        {
            u = rotation * Vector3.right;
            v = rotation * Vector3.forward;
        }
        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = i * (Mathf.PI * 2f / CircleSegments);
            circle[i] = center + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * radius;
        }
        set.Poly(circle, color, true, width);
    }

    void WireSphere(Vector3 center, Quaternion rotation, float radius, Color color)
    {
        Circle(center, rotation, radius, 0, color);
        Circle(center, rotation, radius, 1, color);
        Circle(center, rotation, radius, 2, color);
    }

    void Capsule(Transform t, Vector3 center, float radius, float height, int direction, Color color)
    {
        Vector3 scale = Abs(t.lossyScale);
        Vector3 axis = direction == 0 ? Vector3.right : direction == 2 ? Vector3.forward : Vector3.up;
        float along = direction == 0 ? scale.x : direction == 2 ? scale.z : scale.y;
        float across = direction == 0 ? Mathf.Max(scale.y, scale.z) : direction == 2 ? Mathf.Max(scale.x, scale.y) : Mathf.Max(scale.x, scale.z);
        float worldRadius = radius * across, half = Mathf.Max(0f, height * along * 0.5f - worldRadius);
        Vector3 middle = t.TransformPoint(center), dir = (t.rotation * axis).normalized;
        Quaternion frame = Quaternion.FromToRotation(Vector3.up, dir) * Quaternion.identity;
        Vector3 top = middle + dir * half, bottom = middle - dir * half;
        WireSphere(top, frame, worldRadius, color);
        WireSphere(bottom, frame, worldRadius, color);
        Vector3 side = frame * Vector3.right * worldRadius, front = frame * Vector3.forward * worldRadius;
        set.Line(top + side, bottom + side, color, width);
        set.Line(top - side, bottom - side, color, width);
        set.Line(top + front, bottom + front, color, width);
        set.Line(top - front, bottom - front, color, width);
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}
