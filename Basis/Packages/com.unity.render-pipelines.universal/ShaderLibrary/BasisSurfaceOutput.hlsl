#ifndef BASIS_SURFACE_OUTPUT_INCLUDED
#define BASIS_SURFACE_OUTPUT_INCLUDED

#pragma multi_compile_fragment _ _BASIS_SURFACE_MRT

#if defined(_WRITE_RENDERING_LAYERS)
#define BASIS_SURFACE_TARGET SV_Target2
#else
#define BASIS_SURFACE_TARGET SV_Target1
#endif

half4 BasisSurfaceOutput(half3 albedo, half metallic)
{
    return half4(saturate(albedo) * (half(1.0) - saturate(metallic)), half(0.0));
}

half4 BasisSurfaceOutput(half3 albedo, half metallic, half3 specular, bool specularSetup)
{
    return BasisSurfaceOutput(albedo, specularSetup ? max(specular.r, max(specular.g, specular.b)) : metallic);
}

#endif
