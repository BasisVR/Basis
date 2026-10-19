using Unity.Scripting.LifecycleManagement;
using System;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public partial class BasisSpawnAnchorHandle : BasisPickupInteractable
    {
        public const float BodyDiameter = 0.12f;
        public const float ArrowLength = 0.18f;
        public const float UpArrowLength = 0.09f;
        public const float HeadLength = 0.035f;
        public const float HeadAngle = 28f;
        public const float RingRadius = 0.2f;
        public const float ArcRadius = 0.16f;
        public const float TickLength = 0.02f;
        public const float LineWidth = 0.008f;
        public const float ThinLineWidth = 0.004f;
        public const int RingPoints = 48;
        public const int ArcPoints = 25;
        public const int MaxSnapTicks = 72;
        public const float MinArcDegrees = 0.5f;
        public const float LabelGap = 0.06f;
        public const float LabelScale = 0.02f;
        public const float AngleLabelScale = 0.014f;
        public const float AngleLabelGap = 0.04f;
        public const float GestureStep = 1.02f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");
        private static readonly Color SelectedColor = new Color(1f, 0.65f, 0.1f, 1f);
        private static readonly Color IdleColor = new Color(0.85f, 0.85f, 0.9f, 1f);
        private static readonly Color ForwardColor = new Color(0.25f, 0.55f, 1f, 1f);
        private static readonly Color UpColor = new Color(0.35f, 0.9f, 0.4f, 1f);
        private static readonly Color RingColor = new Color(0.7f, 0.7f, 0.75f, 0.45f);
        private static readonly Color SelectedRingColor = new Color(1f, 0.65f, 0.1f, 0.6f);
        private static readonly Color TickColor = new Color(0.7f, 0.7f, 0.75f, 0.7f);
        private static readonly Color NorthColor = new Color(0.95f, 0.95f, 1f, 0.9f);
        private static readonly Color PlumbColor = new Color(0.35f, 0.9f, 0.4f, 0.35f);
        [NoAutoStaticsCleanup] private static readonly Vector3[] ring = new Vector3[RingPoints];
        [NoAutoStaticsCleanup] private static readonly Vector3[] yawArc = new Vector3[ArcPoints];
        [NoAutoStaticsCleanup] private static readonly Vector3[] tiltArc = new Vector3[ArcPoints];
        private static MaterialPropertyBlock block;

        public bool IsGrabbed { get; private set; }
        public Action<BasisSpawnAnchorHandle> OnGrabbed;
        public Action<BasisSpawnAnchorHandle> OnReleased;
        public Action<BasisSpawnAnchorHandle, float> OnScaleGesture;

        private readonly BasisGizmoSet gizmos = new BasisGizmoSet("SpawnAnchor");
        private Transform body;
        private Renderer bodyRenderer;
        private Material material;
        private int grabCount;
        private float anchorScale = 1f;
        private string labelText = string.Empty;
        private Color labelColor = IdleColor;
        private Color ringColor = RingColor;
        private int yawKey = int.MinValue;
        private int tiltKey = int.MinValue;
        private string yawText = string.Empty;
        private string tiltText = string.Empty;

        public static BasisSpawnAnchorHandle Spawn(Transform parent)
        {
            GameObject root = new GameObject("SpawnAnchorHandle");
            root.transform.SetParent(parent, false);
            int layer = LayerMask.NameToLayer("OverlayUI");
            if (layer < 0)
            {
                layer = 0;
            }
            root.layer = layer;

            Material material = CreateMaterial();
            Renderer bodyRenderer = BuildBody(root.transform, layer, material);

            BasisSpawnAnchorHandle handle = root.AddComponent<BasisSpawnAnchorHandle>();
            handle.body = bodyRenderer.transform;
            handle.bodyRenderer = bodyRenderer;
            handle.material = material;
            handle.GenerateColliderMesh = false;
            handle.KinematicWhileInteracting = true;
            handle.CanSelfSteal = false;
            handle.enableScaleWithGesture = true;
            handle.minScalePercent = BasisSpawnAnchors.MinScale * 100f;
            handle.maxScalePercent = BasisSpawnAnchors.MaxScale * 100f;
            handle.OnInteractStartEvent.AddListener(handle.Grabbed);
            handle.OnInteractEndEvent.AddListener(handle.Released);
            return handle;
        }

        private static Material CreateMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            return shader == null ? null : new Material(shader);
        }

        private static Renderer BuildBody(Transform parent, int layer, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            part.name = "Body";
            part.layer = layer;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = Vector3.zero;
            part.transform.localScale = Vector3.one * BodyDiameter;
            Renderer renderer = part.GetComponent<Renderer>();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Tint(renderer, IdleColor);
            return renderer;
        }

        private static void Tint(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }
            block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, color);
            block.SetColor(LegacyColorId, color);
            renderer.SetPropertyBlock(block);
        }

        public void Apply(BasisSpawnAnchors.SpawnAnchor anchor, bool selected)
        {
            enableGridSnap = BasisSettingsDefaults.SpawnAnchorPositionSnap.RawValue;
            gridSnapSize = BasisSettingsDefaults.SpawnAnchorPositionSnapSize.RawValue;
            enableRotationSnap = BasisSettingsDefaults.SpawnAnchorRotationSnap.RawValue;
            rotationSnapDegrees = BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees.RawValue;
            anchorScale = anchor.OverrideScale ? anchor.Scale : 1f;
            if (!IsGrabbed)
            {
                transform.SetPositionAndRotation(anchor.Position, anchor.Rotation);
            }
            if (body != null)
            {
                body.localScale = Vector3.one * (BodyDiameter * Mathf.Clamp(anchorScale, 0.5f, 2.5f));
            }
            labelColor = selected ? SelectedColor : IdleColor;
            ringColor = selected ? SelectedRingColor : RingColor;
            Tint(bodyRenderer, labelColor);
            labelText = anchor.OverrideScale ? $"{anchor.Name}\n×{anchor.Scale:0.00}" : anchor.Name;
        }

        public void Tick(Vector3 cameraPosition, float scale)
        {
            Vector3 origin = transform.position;
            Quaternion rotation = transform.rotation;
            Vector3 forward = rotation * Vector3.forward;
            Vector3 up = rotation * Vector3.up;
            Vector3 right = rotation * Vector3.right;
            float bodyRadius = body != null ? body.localScale.y * 0.5f : BodyDiameter * 0.5f;

            float yaw = rotation.eulerAngles.y;
            bool showYaw = yaw >= MinArcDegrees && yaw <= 360f - MinArcDegrees;
            float tilt = Vector3.Angle(Vector3.up, up);
            bool showTilt = tilt >= MinArcDegrees;
            Vector3 tiltAxis = Vector3.Cross(Vector3.up, up);
            tiltAxis = tiltAxis.sqrMagnitude <= 1e-8f ? right : tiltAxis.normalized;
            float tiltRadius = bodyRadius + UpArrowLength * 0.6f;

            gizmos.Begin();
            FillRing(ring, origin, Vector3.up, Vector3.forward, RingRadius);
            gizmos.Poly(ring, ringColor, true, ThinLineWidth);
            if (showYaw)
            {
                FillArc(yawArc, origin, Vector3.up, Vector3.forward, yaw, ArcRadius);
                gizmos.Poly(yawArc, ForwardColor, false, ThinLineWidth);
            }
            if (showTilt)
            {
                FillArc(tiltArc, origin, tiltAxis, Vector3.up, tilt, tiltRadius);
                gizmos.Poly(tiltArc, UpColor, false, ThinLineWidth);
            }

            Arrow(origin + forward * bodyRadius, forward, ArrowLength, up, right, ForwardColor, LineWidth);
            Arrow(origin + up * bodyRadius, up, UpArrowLength, forward, right, UpColor, LineWidth);
            if (showYaw)
            {
                ArcHead(yawArc, Vector3.up, ForwardColor);
            }
            if (showTilt)
            {
                ArcHead(tiltArc, tiltAxis, UpColor);
                gizmos.Line(origin + Vector3.up * bodyRadius, origin + Vector3.up * (bodyRadius + UpArrowLength), PlumbColor, ThinLineWidth);
            }
            for (int i = 0; i < 4; i++)
            {
                Vector3 direction = Quaternion.AngleAxis(90f * i, Vector3.up) * Vector3.forward;
                gizmos.Line(origin + direction * RingRadius, origin + direction * (RingRadius + (i == 0 ? TickLength * 1.5f : TickLength)), i == 0 ? NorthColor : TickColor, ThinLineWidth);
            }
            if (enableRotationSnap && rotationSnapDegrees >= 1f)
            {
                int steps = Mathf.Min(Mathf.RoundToInt(360f / rotationSnapDegrees), MaxSnapTicks);
                for (int i = 1; i < steps; i++)
                {
                    float angle = i * rotationSnapDegrees;
                    float cardinal = Mathf.Repeat(angle, 90f);
                    if (cardinal < 0.01f || cardinal > 89.99f)
                    {
                        continue;
                    }
                    Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                    gizmos.Line(origin + direction * (RingRadius - TickLength * 0.5f), origin + direction * RingRadius, TickColor, ThinLineWidth);
                }
            }

            Vector3 labelPosition = origin + Vector3.up * (bodyRadius + LabelGap * scale);
            gizmos.Label(labelPosition, labelText, labelColor, cameraPosition, LabelScale * scale);
            if (showYaw)
            {
                int key = Mathf.RoundToInt(yaw) % 360;
                if (key != yawKey)
                {
                    yawKey = key;
                    yawText = key + "°";
                }
                Vector3 mid = Quaternion.AngleAxis(yaw * 0.5f, Vector3.up) * Vector3.forward;
                gizmos.Label(origin + mid * (RingRadius + AngleLabelGap), yawText, ForwardColor, cameraPosition, AngleLabelScale * scale);
            }
            if (showTilt)
            {
                int key = Mathf.RoundToInt(tilt);
                if (key != tiltKey)
                {
                    tiltKey = key;
                    tiltText = key + "°";
                }
                Vector3 mid = Quaternion.AngleAxis(tilt * 0.5f, tiltAxis) * Vector3.up;
                gizmos.Label(origin + mid * (tiltRadius + AngleLabelGap), tiltText, UpColor, cameraPosition, AngleLabelScale * scale);
            }
            gizmos.End();
        }

        public static void FillRing(Vector3[] points, Vector3 origin, Vector3 axis, Vector3 start, float radius)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = origin + Quaternion.AngleAxis(360f * i / points.Length, axis) * start * radius;
            }
        }

        public static void FillArc(Vector3[] points, Vector3 origin, Vector3 axis, Vector3 start, float degrees, float radius)
        {
            int last = points.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                points[i] = origin + Quaternion.AngleAxis(degrees * i / last, axis) * start * radius;
            }
        }

        private void Arrow(Vector3 start, Vector3 direction, float length, Vector3 finA, Vector3 finB, Color color, float width)
        {
            Vector3 tip = start + direction * length;
            gizmos.Line(start, tip, color, width);
            Vector3 root = tip - direction * (HeadLength * Mathf.Cos(HeadAngle * Mathf.Deg2Rad));
            float side = HeadLength * Mathf.Sin(HeadAngle * Mathf.Deg2Rad);
            gizmos.Line(tip, root + finA * side, color, width);
            gizmos.Line(tip, root - finA * side, color, width);
            gizmos.Line(tip, root + finB * side, color, width);
            gizmos.Line(tip, root - finB * side, color, width);
        }

        private void ArcHead(Vector3[] arc, Vector3 planeAxis, Color color)
        {
            Vector3 tip = arc[arc.Length - 1];
            Vector3 tangent = tip - arc[arc.Length - 2];
            if (tangent.sqrMagnitude <= 1e-10f)
            {
                return;
            }
            tangent.Normalize();
            gizmos.Line(tip, tip - Quaternion.AngleAxis(HeadAngle, planeAxis) * tangent * HeadLength, color, ThinLineWidth);
            gizmos.Line(tip, tip - Quaternion.AngleAxis(-HeadAngle, planeAxis) * tangent * HeadLength, color, ThinLineWidth);
        }

        public void ForgetGizmos()
        {
            gizmos.Forget();
        }

        public void Despawn()
        {
            gizmos.Clear();
            Destroy(gameObject);
        }

        private void Grabbed(BasisInput input)
        {
            grabCount++;
            if (grabCount != 1)
            {
                return;
            }
            IsGrabbed = true;
            OnGrabbed?.Invoke(this);
        }

        private void Released(BasisInput input)
        {
            grabCount = Mathf.Max(0, grabCount - 1);
            if (grabCount != 0)
            {
                return;
            }
            IsGrabbed = false;
            OnReleased?.Invoke(this);
        }

        protected override float GestureScaleReference => 1f;

        protected override void ApplyGestureScaleStep(BasisTransform.Direction scaleDirection, float stepSize, float minScale, float maxScale)
        {
            float next = Mathf.Clamp(anchorScale * (scaleDirection == BasisTransform.Direction.Embiggen ? GestureStep : 1f / GestureStep), minScale, maxScale);
            if (Mathf.Approximately(next, anchorScale))
            {
                return;
            }
            anchorScale = next;
            OnScaleGesture?.Invoke(this, next);
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            gizmos.Clear();
            if (material != null)
            {
                Destroy(material);
                material = null;
            }
        }
    }
}
