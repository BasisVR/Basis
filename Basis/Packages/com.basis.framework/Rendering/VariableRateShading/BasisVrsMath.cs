using UnityEngine;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Pure math for gaze-foveated VRS. The foveal falloff is angular (eccentricity from
    /// the gaze direction), not a UV-space circle — equal UV distances near the edge of a
    /// wide-FOV projection span fewer degrees, so a UV circle over-coarsens right next to
    /// the gaze point when looking away from center. Kept free of engine state so the
    /// projection round-trip is unit-testable.
    /// </summary>
    public static class BasisVrsMath
    {
        /// <summary>
        /// Unprojection constants for a GPU projection matrix: (m02, m12, 1/m00, 1/m11).
        /// With clip.w = -z_view, nx = (m00*x + m02*z)/(-z), so at z = -1: x = (nx + m02)/m00.
        /// </summary>
        public static Vector4 UnprojectParams(Matrix4x4 gpuProj)
        {
            return new Vector4(gpuProj.m02, gpuProj.m12, 1f / gpuProj.m00, 1f / gpuProj.m11);
        }

        /// <summary>View-space ray direction through an NDC point. Mirrors the compute shader.</summary>
        public static Vector3 ViewDirForNdc(Vector4 unproj, Vector2 ndc)
        {
            return new Vector3((ndc.x + unproj.x) * unproj.z, (ndc.y + unproj.y) * unproj.w, -1f).normalized;
        }

        /// <summary>
        /// Forward-project a view-space direction to UV. Algebraically identical to the
        /// legacy proj*view point projection for points in front of the eye.
        /// </summary>
        public static Vector2 ViewDirToUV(Matrix4x4 gpuProj, Vector3 viewDir, Vector2 fallback)
        {
            if (viewDir.z >= -1e-5f)
                return fallback;
            float invNegZ = 1f / -viewDir.z;
            float nx = (gpuProj.m00 * viewDir.x + gpuProj.m02 * viewDir.z) * invNegZ;
            float ny = (gpuProj.m11 * viewDir.y + gpuProj.m12 * viewDir.z) * invNegZ;
            return new Vector2(Mathf.Clamp01(0.5f * nx + 0.5f), Mathf.Clamp01(0.5f * ny + 0.5f));
        }

        /// <summary>
        /// Cosine of the eccentricity angle that a foveal radius (fraction of view height
        /// at the screen center) subtends: theta = atan(2r/m11) => cos = 1/sqrt(1 + x^2).
        /// Keeps the sliders' at-center meaning identical to the old UV-circle model.
        /// </summary>
        public static float CosForUvRadius(float uvRadius, float projM11)
        {
            float x = 2f * Mathf.Max(0f, uvRadius) / Mathf.Max(1e-4f, Mathf.Abs(projM11));
            return 1f / Mathf.Sqrt(1f + x * x);
        }

        /// <summary>
        /// Per-eye gaze direction in that eye's view space: aims at the focal point while
        /// gaze is live (weight 1) and eases back to the eye's optical axis as it fades
        /// (weight 0). A focal point at/behind the eye plane degrades to the axis.
        /// </summary>
        public static Vector3 EyeGazeViewDir(Matrix4x4 view, Vector3 focalWorld, float weight)
        {
            Vector3 forward = new Vector3(0f, 0f, -1f);
            if (weight <= 0f)
                return forward;
            Vector3 viewPos = view.MultiplyPoint3x4(focalWorld);
            if (viewPos.z >= -1e-4f)
                return forward;
            if (weight >= 1f)
                return viewPos.normalized;
            return Vector3.Slerp(forward, viewPos.normalized, weight).normalized;
        }

        /// <summary>
        /// Degrade each band to the finest coarser rate the GPU actually reports so the
        /// SRI never contains an unsupported encoding (undefined behavior on D3D12).
        /// rates = (near 1x1, mid 2x2, far 4x4); ratesAniso = (wide 4x2, tall 2x4).
        /// </summary>
        public static void ResolveRates(bool has2x2, bool hasWide, bool hasTall, bool has4x4,
            uint native2x2, uint nativeWide, uint nativeTall, uint native4x4,
            out Vector4 rates, out Vector4 ratesAniso)
        {
            uint mid = has2x2 ? native2x2 : 0u;
            uint wide = hasWide ? nativeWide : mid;
            uint tall = hasTall ? nativeTall : mid;
            uint far = has4x4 ? native4x4 : mid;
            rates = new Vector4(0f, mid, far, 0f);
            ratesAniso = new Vector4(wide, tall, 0f, 0f);
        }
    }
}
