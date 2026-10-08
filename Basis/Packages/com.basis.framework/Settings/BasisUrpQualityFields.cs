using Unity.Scripting.LifecycleManagement;
using System;
using System.Reflection;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Reflection access to the handful of <see cref="UniversalRenderPipelineAsset"/> quality knobs
/// URP exposes with a getter but an <c>internal</c> setter.
///
/// <para>The alternative was widening those accessors in the embedded URP copy. Reflection keeps
/// the fork surface at zero, so a URP package upgrade can't silently drop the change or land in
/// conflict. The cost is that a rename becomes a runtime failure rather than a compile error,
/// which is why every field is resolved once up front and a miss is logged loudly and then
/// skipped — a missing knob must degrade to "this tier doesn't lower that setting", never to
/// broken rendering.</para>
///
/// <para>The backing fields are written directly rather than through the property. Every one of
/// these setters is a plain <c>m_X = value</c> passthrough with no side effects, so the two are
/// equivalent — and going to the field also covers <c>enableLODCrossFade</c>, which has no setter
/// at all. All six are <c>[SerializeField]</c>, so Unity's serializer keeps them and IL2CPP
/// managed stripping won't remove them.</para>
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisUrpQualityFields
{
    /// <summary>Cached handle to one private field, with a typed get/set that no-ops if absent.</summary>
    public readonly struct Field<T>
    {
        private readonly FieldInfo _info;

        internal Field(FieldInfo info)
        {
            _info = info;
        }

        /// <summary>False when the field could not be resolved on this URP version.</summary>
        public bool Resolved => _info != null;

        public T Get(UniversalRenderPipelineAsset asset, T fallback)
        {
            if (_info == null || asset == null)
            {
                return fallback;
            }
            return (T)_info.GetValue(asset);
        }

        public void Set(UniversalRenderPipelineAsset asset, T value)
        {
            if (_info == null || asset == null)
            {
                return;
            }
            _info.SetValue(asset, value);
        }
    }

    public static Field<bool> SupportsSoftShadows { get; private set; }
    public static Field<SoftShadowQuality> SoftShadowQualityLevel { get; private set; }
    public static Field<LightRenderingMode> AdditionalLightsRenderingMode { get; private set; }
    public static Field<bool> ReflectionProbeBlending { get; private set; }
    public static Field<bool> ReflectionProbeBoxProjection { get; private set; }
    public static Field<bool> EnableLODCrossFade { get; private set; }

    private static bool _resolved;

    /// <summary>
    /// Resolves every field handle once. Safe to call repeatedly; only the first call does work.
    /// </summary>
    public static void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }
        _resolved = true;

        Type type = typeof(UniversalRenderPipelineAsset);

        SupportsSoftShadows = new Field<bool>(Resolve(type, "m_SoftShadowsSupported"));
        SoftShadowQualityLevel = new Field<SoftShadowQuality>(Resolve(type, "m_SoftShadowQuality"));
        AdditionalLightsRenderingMode = new Field<LightRenderingMode>(Resolve(type, "m_AdditionalLightsRenderingMode"));
        ReflectionProbeBlending = new Field<bool>(Resolve(type, "m_ReflectionProbeBlending"));
        ReflectionProbeBoxProjection = new Field<bool>(Resolve(type, "m_ReflectionProbeBoxProjection"));
        EnableLODCrossFade = new Field<bool>(Resolve(type, "m_EnableLODCrossFade"));
    }

    private static FieldInfo Resolve(Type type, string fieldName)
    {
        FieldInfo info = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (info == null)
        {
            // Loud on purpose: this is the failure mode reflection buys us over editing the
            // URP fork, so it must not be quiet. The tier simply stops lowering this knob.
            BasisDebug.LogError(
                $"BasisUrpQualityFields: UniversalRenderPipelineAsset.{fieldName} not found — " +
                "URP likely renamed it. The graphics quality level will no longer tier that setting.",
                BasisDebug.LogTag.System);
        }
        return info;
    }
}
