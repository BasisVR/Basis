using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    /// <summary>
    /// Screen-center aim reticle owned by <see cref="BasisDesktopEye"/>. Plain
    /// <c>[Serializable]</c> driver class (not a MonoBehaviour) so it serializes
    /// inline on the desktop eye, mirroring the
    /// <c>BasisLocalMicrophoneIconDriver</c> pattern.
    ///
    /// Renders a small SDF quad parented to the local camera transform so it
    /// tracks aim for free, drawn with the always-on-top
    /// <c>Custom/DesktopReticleOverlay</c> shader. Three concentric bands
    /// (dot, dark gap, outer ring) give it visibility against any background
    /// without relying on color-inversion blending — that doesn't behave
    /// correctly under URP HDR + Forward+ + post-processing.
    /// </summary>
    [System.Serializable]
    public class BasisDesktopReticle
    {
        public const string ShaderName = "Custom/DesktopReticleOverlay";

        public float DistanceFromCamera = 1f;
        public float SizeMeters = 0.05f;

        [Header("Shape (UV space, 0..0.5 from quad center)")]
        public float DotRadius = 0.04f;
        public float RingInnerRadius = 0.1f;
        public float RingOuterRadius = 0.15f;

        [Header("Colors")]
        public Color DotColor = new Color(1f, 1f, 1f, 0.55f);
        public Color GapColor = new Color(0f, 0f, 0f, 0.55f);
        public Color RingColor = new Color(1f, 1f, 1f, 0.20f);

        private GameObject _quadGO;
        private Material _material;

        // Visibility is gated by two independent inputs: the user-facing
        // setting toggle and cursor focus (hide while the cursor is unlocked
        // for a menu). The quad is shown only when both are true.
        private bool _userEnabled;
        private bool _focused = true;

        private static readonly int IdDotRadius = Shader.PropertyToID("_DotRadius");
        private static readonly int IdRingInnerRadius = Shader.PropertyToID("_RingInnerRadius");
        private static readonly int IdRingOuterRadius = Shader.PropertyToID("_RingOuterRadius");
        private static readonly int IdDotColor = Shader.PropertyToID("_DotColor");
        private static readonly int IdGapColor = Shader.PropertyToID("_GapColor");
        private static readonly int IdRingColor = Shader.PropertyToID("_RingColor");

        public void Initialize()
        {
            if (_quadGO != null) return; // idempotent

            Transform camTransform = BasisLocalCameraDriver.Instance.transform;

            _quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadGO.name = "DesktopReticleQuad";
            _quadGO.layer = LayerMask.NameToLayer("UI"); // Render feture puts UI layer on top of everything else.
            Object.Destroy(_quadGO.GetComponent<MeshCollider>());

            _quadGO.transform.SetParent(camTransform, false);
            _quadGO.transform.SetLocalPositionAndRotation(Vector3.forward * DistanceFromCamera, Quaternion.identity);
            _quadGO.transform.localScale = Vector3.one * SizeMeters;

            _material = new Material(Shader.Find(ShaderName));
            _material.SetFloat(IdDotRadius, DotRadius);
            _material.SetFloat(IdRingInnerRadius, RingInnerRadius);
            _material.SetFloat(IdRingOuterRadius, RingOuterRadius);
            _material.SetColor(IdDotColor, DotColor);
            _material.SetColor(IdGapColor, GapColor);
            _material.SetColor(IdRingColor, RingColor);

            var renderer = _quadGO.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        public void Destroy()
        {
            if (_material != null)
            {
                Object.Destroy(_material);
                _material = null;
            }
            if (_quadGO != null)
            {
                Object.Destroy(_quadGO);
                _quadGO = null;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _userEnabled = enabled;
            ApplyVisibility();
        }

        public void SetFocused(bool focused)
        {
            _focused = focused;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool visible = _userEnabled && _focused;
            // Lazy init
            if (visible && _quadGO == null)
            {
                Initialize();
            }
            if (_quadGO != null)
            {
                _quadGO.SetActive(visible);
            }
        }
    }
}
