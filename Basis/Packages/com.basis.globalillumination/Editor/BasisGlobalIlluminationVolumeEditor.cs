using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

[CanEditMultipleObjects]
[CustomEditor(typeof(BasisGlobalIlluminationVolume))]
sealed class BasisGlobalIlluminationVolumeEditor : VolumeComponentEditor
{
    private SerializedDataParameter enable, mode;
    private SerializedDataParameter intensity, saturation, tint, obscuranceIntensity, obscuranceRadius, maxRayLength, fadeDistance;
    private SerializedDataParameter quality, overrideQualityCounts, rayCount, rayMaxSteps, thickness, jitter, smoothing, wideBlur, normalSource;
    private SerializedDataParameter fallback, fallbackIntensity, rayReuse, emitters, emitterIntensity, emitterOcclusion;
    private SerializedDataParameter resolution, temporalFilter, temporalResponse, depthRejection, neighbourhoodClamp, fireflyClamp, bilateralUpsample;
    private SerializedDataParameter bounces, rayTracedLights, rayTracedLightIntensity, rayTracedShadows, rayTracedEmissiveSurfaces, rayTracedTextureAlbedo;
    private SerializedDataParameter rayTracedSkinnedMeshes, rayTracedSkinnedBudget, rayTracedSkinnedInterval, rayTracedSkinnedDistance;
    private SerializedDataParameter rayTracedLayerMask, rayTracedShadowCastersOnly, rayTracedRescanInterval, rayTracedNormalBias;

    private static bool generalExpanded = true, qualityExpanded = true, fallbackExpanded, performanceExpanded, rayTracingExpanded = true;

    public override void OnEnable()
    {
        base.OnEnable();
        PropertyFetcher<BasisGlobalIlluminationVolume> fetcher = new PropertyFetcher<BasisGlobalIlluminationVolume>(serializedObject);

        enable = Unpack(fetcher.Find(x => x.enable));
        mode = Unpack(fetcher.Find(x => x.mode));

        intensity = Unpack(fetcher.Find(x => x.intensity));
        saturation = Unpack(fetcher.Find(x => x.saturation));
        tint = Unpack(fetcher.Find(x => x.tint));
        obscuranceIntensity = Unpack(fetcher.Find(x => x.obscuranceIntensity));
        obscuranceRadius = Unpack(fetcher.Find(x => x.obscuranceRadius));
        maxRayLength = Unpack(fetcher.Find(x => x.maxRayLength));
        fadeDistance = Unpack(fetcher.Find(x => x.fadeDistance));

        quality = Unpack(fetcher.Find(x => x.quality));
        overrideQualityCounts = Unpack(fetcher.Find(x => x.overrideQualityCounts));
        rayCount = Unpack(fetcher.Find(x => x.rayCount));
        rayMaxSteps = Unpack(fetcher.Find(x => x.rayMaxSteps));
        thickness = Unpack(fetcher.Find(x => x.thickness));
        jitter = Unpack(fetcher.Find(x => x.jitter));
        smoothing = Unpack(fetcher.Find(x => x.smoothing));
        wideBlur = Unpack(fetcher.Find(x => x.wideBlur));
        normalSource = Unpack(fetcher.Find(x => x.normalSource));

        fallback = Unpack(fetcher.Find(x => x.fallback));
        fallbackIntensity = Unpack(fetcher.Find(x => x.fallbackIntensity));
        rayReuse = Unpack(fetcher.Find(x => x.rayReuse));
        emitters = Unpack(fetcher.Find(x => x.emitters));
        emitterIntensity = Unpack(fetcher.Find(x => x.emitterIntensity));
        emitterOcclusion = Unpack(fetcher.Find(x => x.emitterOcclusion));

        resolution = Unpack(fetcher.Find(x => x.resolution));
        temporalFilter = Unpack(fetcher.Find(x => x.temporalFilter));
        temporalResponse = Unpack(fetcher.Find(x => x.temporalResponse));
        depthRejection = Unpack(fetcher.Find(x => x.depthRejection));
        neighbourhoodClamp = Unpack(fetcher.Find(x => x.neighbourhoodClamp));
        fireflyClamp = Unpack(fetcher.Find(x => x.fireflyClamp));
        bilateralUpsample = Unpack(fetcher.Find(x => x.bilateralUpsample));

        bounces = Unpack(fetcher.Find(x => x.bounces));
        rayTracedLights = Unpack(fetcher.Find(x => x.rayTracedLights));
        rayTracedLightIntensity = Unpack(fetcher.Find(x => x.rayTracedLightIntensity));
        rayTracedShadows = Unpack(fetcher.Find(x => x.rayTracedShadows));
        rayTracedEmissiveSurfaces = Unpack(fetcher.Find(x => x.rayTracedEmissiveSurfaces));
        rayTracedTextureAlbedo = Unpack(fetcher.Find(x => x.rayTracedTextureAlbedo));
        rayTracedSkinnedMeshes = Unpack(fetcher.Find(x => x.rayTracedSkinnedMeshes));
        rayTracedSkinnedBudget = Unpack(fetcher.Find(x => x.rayTracedSkinnedBudget));
        rayTracedSkinnedInterval = Unpack(fetcher.Find(x => x.rayTracedSkinnedInterval));
        rayTracedSkinnedDistance = Unpack(fetcher.Find(x => x.rayTracedSkinnedDistance));
        rayTracedLayerMask = Unpack(fetcher.Find(x => x.rayTracedLayerMask));
        rayTracedShadowCastersOnly = Unpack(fetcher.Find(x => x.rayTracedShadowCastersOnly));
        rayTracedRescanInterval = Unpack(fetcher.Find(x => x.rayTracedRescanInterval));
        rayTracedNormalBias = Unpack(fetcher.Find(x => x.rayTracedNormalBias));
    }

    public override void OnInspectorGUI()
    {
        PropertyField(enable);
        PropertyField(mode);
        if (!BasisGlobalIlluminationFeature.SupportsPlatform())
        {
            EditorGUILayout.HelpBox("This platform does not run Basis Global Illumination. The effect is skipped at runtime.", MessageType.Info);
        }

        bool rayTraced = mode.value.enumValueIndex == (int)BasisGlobalIlluminationMode.RayTraced;
        if (rayTraced && !BasisGlobalIlluminationRayContext.Supported)
        {
            EditorGUILayout.HelpBox("This GPU has no ray tracing backend. The ray traced mode falls back to the screen space gather at runtime.", MessageType.Warning);
        }

        generalExpanded = EditorGUILayout.Foldout(generalExpanded, "General", true);
        if (generalExpanded)
        {
            PropertyField(intensity);
            PropertyField(saturation);
            PropertyField(tint);
            PropertyField(obscuranceIntensity);
            PropertyField(obscuranceRadius);
            PropertyField(maxRayLength);
            PropertyField(fadeDistance);
        }

        qualityExpanded = EditorGUILayout.Foldout(qualityExpanded, "Quality", true);
        if (qualityExpanded)
        {
            PropertyField(quality);
            PropertyField(overrideQualityCounts);
            if (overrideQualityCounts.value.boolValue)
            {
                PropertyField(rayCount);
                if (rayTraced) { PropertyField(bounces); }
                else { PropertyField(rayMaxSteps); }
            }
            if (!rayTraced)
            {
                PropertyField(thickness);
                PropertyField(jitter);
            }
            PropertyField(smoothing);
            PropertyField(wideBlur);
            if (!rayTraced) { PropertyField(normalSource); }
        }

        if (rayTraced)
        {
            rayTracingExpanded = EditorGUILayout.Foldout(rayTracingExpanded, "Ray Tracing", true);
            if (rayTracingExpanded)
            {
                PropertyField(rayTracedLights);
                if (rayTracedLights.value.boolValue)
                {
                    PropertyField(rayTracedLightIntensity);
                    PropertyField(rayTracedShadows);
                }
                PropertyField(rayTracedEmissiveSurfaces);
                PropertyField(rayTracedTextureAlbedo);
                PropertyField(rayTracedSkinnedMeshes);
                if (rayTracedSkinnedMeshes.value.enumValueIndex == (int)BasisGlobalIlluminationRaySkinnedMode.Dynamic)
                {
                    PropertyField(rayTracedSkinnedBudget);
                    PropertyField(rayTracedSkinnedInterval);
                    PropertyField(rayTracedSkinnedDistance);
                }
                PropertyField(rayTracedLayerMask);
                PropertyField(rayTracedShadowCastersOnly);
                PropertyField(rayTracedRescanInterval);
                PropertyField(rayTracedNormalBias);
            }
        }

        fallbackExpanded = EditorGUILayout.Foldout(fallbackExpanded, "Fallbacks", true);
        if (fallbackExpanded)
        {
            PropertyField(fallback);
            PropertyField(fallbackIntensity);
            if (!rayTraced) { PropertyField(rayReuse); }
            PropertyField(emitters);
            if (emitters.value.boolValue || (rayTraced && rayTracedEmissiveSurfaces.value.boolValue))
            {
                PropertyField(emitterIntensity);
                if (!rayTraced) { PropertyField(emitterOcclusion); }
            }
        }

        performanceExpanded = EditorGUILayout.Foldout(performanceExpanded, "Performance", true);
        if (performanceExpanded)
        {
            PropertyField(resolution);
            PropertyField(temporalFilter);
            if (temporalFilter.value.boolValue)
            {
                PropertyField(temporalResponse);
                PropertyField(depthRejection);
                PropertyField(neighbourhoodClamp);
            }
            PropertyField(fireflyClamp);
            PropertyField(bilateralUpsample);
        }
    }
}
