using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    /// <summary>
    /// Screen-center aim reticle owned by <see cref="BasisDesktopEye"/>. SDF
    /// quad parented to the local camera, drawn with the always-on-top
    /// <c>Custom/DesktopReticleOverlay</c> shader. Three concentric bands
    /// (dot, dark gap, outer ring) keep it visible against most backgrounds.
    ///
    /// Visibility is gated by two independent inputs. User setting
    /// (<see cref="SetEnabled"/>) and cursor focus (<see cref="SetFocused"/>,
    /// off while the cursor is unlocked). Quad is shown only when
    /// both are true.
    /// </summary>
    [System.Serializable]
    public class BasisDesktopReticle
    {
        public float DistanceFromCamera = 1f;
        public float SizeMeters = 0.05f;

        /// <summary>
        /// Material cloned at <see cref="Initialize"/> time from
        /// <see cref="BasisDesktopManagement"/> before the reticle is initialized.
        /// </summary>
        public Material SourceMaterial;

        private GameObject _quadGO;
        private Material _material;
        private bool _userEnabled;
        private bool _focused = true;

        public void Initialize()
        {
            if (_quadGO != null) return; // idempotent

            Transform camTransform = BasisLocalCameraDriver.Instance.transform;

            _quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadGO.name = "DesktopReticleQuad";
            // Render feture puts UI layer on top of everything else.
            _quadGO.layer = LayerMask.NameToLayer("UI");
            Object.Destroy(_quadGO.GetComponent<MeshCollider>());

            _quadGO.transform.SetParent(camTransform, false);
            _quadGO.transform.SetLocalPositionAndRotation(Vector3.forward * DistanceFromCamera, Quaternion.identity);
            _quadGO.transform.localScale = Vector3.one * SizeMeters;

            _material = new Material(SourceMaterial);

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
