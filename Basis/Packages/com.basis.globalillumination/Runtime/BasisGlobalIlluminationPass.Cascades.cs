using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed partial class BasisGlobalIlluminationPass
{
    public const int PassCascadeReduce = 9, PassCascadeProbe = 10, PassCascade = 11, PassCascadeResolve = 12, PassCascadeGather = 13;
    public const int CascadeDirections = 4;
    public const int CascadeLevels = 4;
    public const int CascadeMax = BasisGlobalIlluminationSettings.CascadeCountMax;
    public const int CascadeJitterPeriod = 16;
    public const float CascadePenumbra = 3.4f;
    public const float WorldNearFieldVoxels = 16f;

    private static readonly int idRcColor1 = Shader.PropertyToID("_BasisGIRCColor1");
    private static readonly int idRcColor2 = Shader.PropertyToID("_BasisGIRCColor2");
    private static readonly int idRcColor3 = Shader.PropertyToID("_BasisGIRCColor3");
    private static readonly int idRcDepth1 = Shader.PropertyToID("_BasisGIRCDepth1");
    private static readonly int idRcDepth2 = Shader.PropertyToID("_BasisGIRCDepth2");
    private static readonly int idRcDepth3 = Shader.PropertyToID("_BasisGIRCDepth3");
    private static readonly int idRcSourceColor = Shader.PropertyToID("_BasisGIRCSourceColor");
    private static readonly int idRcSourceDepth = Shader.PropertyToID("_BasisGIRCSourceDepth");
    private static readonly int idRcProbes = Shader.PropertyToID("_BasisGIRCProbes");
    private static readonly int idRcUpperProbes = Shader.PropertyToID("_BasisGIRCUpperProbes");
    private static readonly int idRcUpper = Shader.PropertyToID("_BasisGIRCUpper");
    private static readonly int idRcSh0 = Shader.PropertyToID("_BasisGIRCSH0");
    private static readonly int idRcSh1 = Shader.PropertyToID("_BasisGIRCSH1");
    private static readonly int idRcSh2 = Shader.PropertyToID("_BasisGIRCSH2");
    private static readonly int idRcSh3 = Shader.PropertyToID("_BasisGIRCSH3");
    private static readonly int idRcLevels = Shader.PropertyToID("_BasisGIRCLevels");
    private static readonly int idRcSolidAngle = Shader.PropertyToID("_BasisGIRCSolidAngle");
    private static readonly int idRcCascade = Shader.PropertyToID("_BasisGIRCCascade");
    private static readonly int idRcInterval = Shader.PropertyToID("_BasisGIRCInterval");
    private static readonly int idRcGrid = Shader.PropertyToID("_BasisGIRCGrid");
    private static readonly int idRcJitter = Shader.PropertyToID("_BasisGIRCJitter");
    private static readonly int idRcRotation = Shader.PropertyToID("_BasisGIRCRotation");

    [NoAutoStaticsCleanup] private static readonly ProfilingSampler samplerCascades = new ProfilingSampler("Basis GI Radiance Cascades");
    [NoAutoStaticsCleanup] private static readonly float[] cascadeSolidAngles = ComputeCascadeSolidAngles();

    public static float GpuMsCascades => samplerCascades.gpuElapsedTime;

    private readonly TextureHandle[] cascadeProbes = new TextureHandle[CascadeMax];
    private readonly Vector4[] cascadeLevels = new Vector4[CascadeLevels];

    public static Vector3 CascadeDirection(int x, int y, int resolution)
    {
        float fx = (x + 0.5f) / resolution * 2f - 1f, fy = (y + 0.5f) / resolution * 2f - 1f;
        return OctahedralDecode(fx, fy);
    }

    public static Vector3 OctahedralDecode(float fx, float fy)
    {
        Vector3 n = new Vector3(fx, fy, 1f - Mathf.Abs(fx) - Mathf.Abs(fy));
        float t = Mathf.Max(-n.z, 0f);
        n.x += n.x >= 0f ? -t : t;
        n.y += n.y >= 0f ? -t : t;
        return n.normalized;
    }

    public static float[] ComputeCascadeSolidAngles()
    {
        const int sub = 32;
        int count = CascadeDirections * CascadeDirections;
        float[] angles = new float[count];
        double total = 0.0;
        for (int index = 0; index < count; index++)
        {
            int x = index % CascadeDirections, y = index / CascadeDirections;
            double area = 0.0;
            for (int sy = 0; sy < sub; sy++)
            {
                for (int sx = 0; sx < sub; sx++)
                {
                    float u0 = (x + sx / (float)sub) / CascadeDirections * 2f - 1f, u1 = (x + (sx + 1) / (float)sub) / CascadeDirections * 2f - 1f;
                    float v0 = (y + sy / (float)sub) / CascadeDirections * 2f - 1f, v1 = (y + (sy + 1) / (float)sub) / CascadeDirections * 2f - 1f;
                    Vector3 a = OctahedralDecode(u0, v0), b = OctahedralDecode(u1, v0), c = OctahedralDecode(u1, v1), d = OctahedralDecode(u0, v1);
                    area += SphericalTriangleArea(a, b, c) + SphericalTriangleArea(a, c, d);
                }
            }
            angles[index] = (float)area;
            total += area;
        }
        float scale = (float)(4.0 * Mathf.PI / total);
        for (int index = 0; index < count; index++) { angles[index] *= scale; }
        return angles;
    }

    private static double SphericalTriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        double triple = Mathf.Abs(Vector3.Dot(a, Vector3.Cross(b, c)));
        double denominator = 1.0 + Vector3.Dot(a, b) + Vector3.Dot(b, c) + Vector3.Dot(c, a);
        return 2.0 * System.Math.Atan2(triple, denominator);
    }

    public static int EffectiveCascadeCount(int requested, int spacing, int tracedWidth, int tracedHeight)
    {
        int cascades = Mathf.Clamp(requested, 1, CascadeMax);
        int limit = Mathf.Max(1, Mathf.Min(tracedWidth, tracedHeight) / 4);
        while (cascades > 2 && (spacing << (cascades - 1)) > limit) { cascades--; }
        return cascades;
    }

    public static Vector2 CascadeInterval(int cascade, int cascades, float range)
    {
        float first = range * 3f / (Mathf.Pow(4f, cascades) - 1f);
        float start = first * (Mathf.Pow(4f, cascade) - 1f) / 3f;
        return new Vector2(start, start + first * Mathf.Pow(4f, cascade));
    }

    private static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        for (int i = index + 1; i > 0; i /= radix, fraction /= radix) { result += fraction * (i % radix); }
        return result;
    }

    public static void CascadeJitter(int frame, float amount, int spacing, out Vector2 offset, out Quaternion rotation)
    {
        offset = Vector2.zero;
        rotation = Quaternion.identity;
        if (amount <= 0f) { return; }
        int index = frame % CascadeJitterPeriod;
        offset = new Vector2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f) * (spacing * amount);
        float u1 = Halton(index, 5), u2 = Halton(index, 7), u3 = Halton(index, 11);
        float a = Mathf.Sqrt(1f - u1), b = Mathf.Sqrt(u1);
        Quaternion uniform = new Quaternion(a * Mathf.Sin(2f * Mathf.PI * u2), a * Mathf.Cos(2f * Mathf.PI * u2), b * Mathf.Sin(2f * Mathf.PI * u3), b * Mathf.Cos(2f * Mathf.PI * u3));
        rotation = Quaternion.Slerp(Quaternion.identity, uniform, Mathf.Clamp01(amount));
    }

    private void RecordRadianceCascades(RenderGraph renderGraph, UniversalResourceData resourceData, BasisGlobalIlluminationSettings settings,
        in RenderTextureDescriptor tracedDescriptor, in RenderTextureDescriptor descriptor, TextureHandle sceneColor, TextureHandle tracedDepth,
        TextureHandle normals, TextureHandle traced, int tracedWidth, int tracedHeight, int frame, int emitterCount, in BasisGlobalIlluminationRayTracer.SkyBinding sky)
    {
        int spacing = Mathf.Max(1, settings.ResolvedCascadeSpacing());
        bool nearField = settings.worldSpace && worldAlbedo != null;
        int requested = nearField && !settings.cascadeOverride ? Mathf.Max(2, settings.ResolvedCascadeCount() - 1) : settings.ResolvedCascadeCount();
        int cascades = EffectiveCascadeCount(requested, spacing, tracedWidth, tracedHeight);
        float reach = nearField ? Mathf.Min(settings.maxRayLength, settings.worldVoxelSize * WorldNearFieldVoxels) : settings.maxRayLength;
        int steps = Mathf.Max(1, settings.ResolvedCascadeSteps());
        float firstInterval = CascadeInterval(0, cascades, reach).y;
        float jitterAmount = settings.temporalFilter ? settings.jitter : 0f;
        CascadeJitter(frame, jitterAmount, spacing, out Vector2 jitter, out Quaternion rotation);
        Matrix4x4 rotationMatrix = Matrix4x4.Rotate(rotation);

        RenderTextureDescriptor colorDescriptor = tracedDescriptor;
        colorDescriptor.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
        RenderTextureDescriptor depthDescriptor = tracedDescriptor;
        depthDescriptor.graphicsFormat = GraphicsFormat.R16G16_SFloat;

        cascadeLevels[0] = new Vector4(tracedWidth, tracedHeight, 0f, 0f);
        TextureHandle color1 = TextureHandle.nullHandle, color2 = TextureHandle.nullHandle, color3 = TextureHandle.nullHandle;
        TextureHandle depth1 = TextureHandle.nullHandle, depth2 = TextureHandle.nullHandle, depth3 = TextureHandle.nullHandle;
        TextureHandle sourceColor = sceneColor, sourceDepth = tracedDepth;
        int levelWidth = tracedWidth, levelHeight = tracedHeight;
        for (int level = 1; level < CascadeLevels; level++)
        {
            int sourceWidth = levelWidth, sourceHeight = levelHeight;
            levelWidth = Mathf.Max(1, (levelWidth + 1) / 2);
            levelHeight = Mathf.Max(1, (levelHeight + 1) / 2);
            cascadeLevels[level] = new Vector4(levelWidth, levelHeight, 0f, 0f);
            colorDescriptor.width = depthDescriptor.width = levelWidth;
            colorDescriptor.height = depthDescriptor.height = levelHeight;
            TextureHandle levelColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, colorDescriptor, "_BasisGIRCColor" + level, false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle levelDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDescriptor, "_BasisGIRCDepth" + level, false, FilterMode.Point, TextureWrapMode.Clamp);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Cascade Pyramid", out PassData passData, samplerCascades))
            {
                ResetCascadeData(passData);
                passData.stage = Stage.Cascade;
                passData.material = material;
                passData.materialPass = PassCascadeReduce;
                passData.source = sourceColor;
                passData.rcSourceColor = sourceColor;
                passData.rcSourceDepth = sourceDepth;
                passData.rcJitter = new Vector4(0f, 0f, sourceWidth, sourceHeight);
                builder.SetRenderAttachment(levelColor, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(levelDepth, 1, AccessFlags.WriteAll);
                builder.UseTexture(sourceColor);
                builder.UseTexture(sourceDepth);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            if (level == 1) { color1 = levelColor; depth1 = levelDepth; }
            else if (level == 2) { color2 = levelColor; depth2 = levelDepth; }
            else { color3 = levelColor; depth3 = levelDepth; }
            sourceColor = levelColor;
            sourceDepth = levelDepth;
        }

        int atlasWidth = 1, atlasHeight = 1;
        RenderTextureDescriptor probeDescriptor = tracedDescriptor;
        probeDescriptor.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
        for (int cascade = 0; cascade < cascades; cascade++)
        {
            int cascadeSpacing = spacing << cascade, resolution = CascadeDirections << cascade;
            int gridWidth = (tracedWidth + cascadeSpacing - 1) / cascadeSpacing, gridHeight = (tracedHeight + cascadeSpacing - 1) / cascadeSpacing;
            atlasWidth = Mathf.Max(atlasWidth, gridWidth * resolution);
            atlasHeight = Mathf.Max(atlasHeight, gridHeight * resolution);
            probeDescriptor.width = gridWidth;
            probeDescriptor.height = gridHeight;
            cascadeProbes[cascade] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, probeDescriptor, "_BasisGIRCProbes" + cascade, false, FilterMode.Point, TextureWrapMode.Clamp);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Cascade Probes", out PassData passData, samplerCascades))
            {
                ResetCascadeData(passData);
                passData.stage = Stage.Cascade;
                passData.material = material;
                passData.materialPass = PassCascadeProbe;
                passData.source = resourceData.cameraDepthTexture;
                passData.normals = normals;
                passData.rcCascade = new Vector4(cascade, cascadeSpacing, resolution, 0f);
                passData.rcGrid = new Vector4(gridWidth, gridHeight, 0f, 0f);
                passData.rcJitter = new Vector4(jitter.x, jitter.y, 0f, 0f);
                builder.SetRenderAttachment(cascadeProbes[cascade], 0, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraDepthTexture);
                if (normals.IsValid()) { builder.UseTexture(normals); }
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
        }

        RenderTextureDescriptor atlasDescriptor = tracedDescriptor;
        atlasDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        atlasDescriptor.width = atlasWidth;
        atlasDescriptor.height = atlasHeight;
        TextureHandle atlasA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, atlasDescriptor, "_BasisGIRCAtlasA", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
        TextureHandle atlasB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, atlasDescriptor, "_BasisGIRCAtlasB", false, FilterMode.Bilinear, TextureWrapMode.Clamp);

        TextureHandle upper = TextureHandle.nullHandle;
        for (int cascade = cascades - 1; cascade >= 0; cascade--)
        {
            int cascadeSpacing = spacing << cascade, resolution = CascadeDirections << cascade;
            int gridWidth = (tracedWidth + cascadeSpacing - 1) / cascadeSpacing, gridHeight = (tracedHeight + cascadeSpacing - 1) / cascadeSpacing;
            int upperSpacing = cascadeSpacing << 1;
            int upperWidth = (tracedWidth + upperSpacing - 1) / upperSpacing, upperHeight = (tracedHeight + upperSpacing - 1) / upperSpacing;
            bool hasUpper = upper.IsValid();
            TextureHandle target = ((cascades - 1 - cascade) & 1) == 0 ? atlasA : atlasB;

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Cascade", out PassData passData, samplerCascades))
            {
                ResetCascadeData(passData);
                passData.stage = Stage.Cascade;
                passData.material = material;
                passData.materialPass = PassCascade;
                passData.source = sceneColor;
                passData.sceneColor = sceneColor;
                passData.tracedDepth = tracedDepth;
                passData.tracedDepthValid = true;
                passData.rcColor1 = color1; passData.rcColor2 = color2; passData.rcColor3 = color3;
                passData.rcDepth1 = depth1; passData.rcDepth2 = depth2; passData.rcDepth3 = depth3;
                passData.rcProbes = cascadeProbes[cascade];
                passData.rcUpperProbes = hasUpper ? cascadeProbes[cascade + 1] : TextureHandle.nullHandle;
                passData.rcUpper = upper;
                passData.rcCascade = new Vector4(cascade, cascadeSpacing, resolution, hasUpper ? 1f : 0f);
                passData.rcInterval = new Vector4(firstInterval, CascadePenumbra * spacing, steps, frame % 64);
                passData.rcGrid = new Vector4(gridWidth, gridHeight, upperWidth, upperHeight);
                passData.rcJitter = new Vector4(jitter.x, jitter.y, atlasWidth, atlasHeight);
                passData.rcRotation = rotationMatrix;
                System.Array.Copy(cascadeLevels, passData.rcLevels, CascadeLevels);
                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.UseTexture(sceneColor);
                builder.UseTexture(tracedDepth);
                builder.UseTexture(color1); builder.UseTexture(color2); builder.UseTexture(color3);
                builder.UseTexture(depth1); builder.UseTexture(depth2); builder.UseTexture(depth3);
                builder.UseTexture(cascadeProbes[cascade]);
                if (hasUpper) { builder.UseTexture(cascadeProbes[cascade + 1]); builder.UseTexture(upper); }
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
            upper = target;
        }

        int baseWidth = (tracedWidth + spacing - 1) / spacing, baseHeight = (tracedHeight + spacing - 1) / spacing;
        RenderTextureDescriptor harmonicsDescriptor = tracedDescriptor;
        harmonicsDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        harmonicsDescriptor.width = baseWidth;
        harmonicsDescriptor.height = baseHeight;
        TextureHandle sh0 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, harmonicsDescriptor, "_BasisGIRCSH0", false, FilterMode.Point, TextureWrapMode.Clamp);
        TextureHandle sh1 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, harmonicsDescriptor, "_BasisGIRCSH1", false, FilterMode.Point, TextureWrapMode.Clamp);
        TextureHandle sh2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, harmonicsDescriptor, "_BasisGIRCSH2", false, FilterMode.Point, TextureWrapMode.Clamp);
        TextureHandle sh3 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, harmonicsDescriptor, "_BasisGIRCSH3", false, FilterMode.Point, TextureWrapMode.Clamp);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Cascade Resolve", out PassData passData, samplerCascades))
        {
            ResetCascadeData(passData);
            passData.stage = Stage.Cascade;
            passData.material = material;
            passData.materialPass = PassCascadeResolve;
            passData.source = upper;
            passData.rcUpper = upper;
            passData.rcProbes = cascadeProbes[0];
            passData.rcCascade = new Vector4(0f, spacing, CascadeDirections, 0f);
            passData.rcGrid = new Vector4(baseWidth, baseHeight, 0f, 0f);
            passData.rcRotation = rotationMatrix;
            builder.SetRenderAttachment(sh0, 0, AccessFlags.WriteAll);
            builder.SetRenderAttachment(sh1, 1, AccessFlags.WriteAll);
            builder.SetRenderAttachment(sh2, 2, AccessFlags.WriteAll);
            builder.SetRenderAttachment(sh3, 3, AccessFlags.WriteAll);
            builder.UseTexture(upper);
            builder.UseTexture(cascadeProbes[0]);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Cascade Gather", out PassData passData, samplerCascades))
        {
            ResetCascadeData(passData);
            passData.stage = Stage.Cascade;
            passData.material = material;
            passData.materialPass = PassCascadeGather;
            passData.source = resourceData.cameraDepthTexture;
            passData.normals = normals;
            passData.rcProbes = cascadeProbes[0];
            passData.rcSh0 = sh0; passData.rcSh1 = sh1; passData.rcSh2 = sh2; passData.rcSh3 = sh3;
            passData.rcCascade = new Vector4(0f, spacing, CascadeDirections, 0f);
            passData.rcGrid = new Vector4(baseWidth, baseHeight, 0f, 0f);
            passData.rcJitter = new Vector4(jitter.x, jitter.y, 0f, 0f);
            builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
            builder.UseTexture(cascadeProbes[0]);
            builder.UseTexture(sh0); builder.UseTexture(sh1); builder.UseTexture(sh2); builder.UseTexture(sh3);
            builder.UseTexture(resourceData.cameraDepthTexture);
            if (normals.IsValid()) { builder.UseTexture(normals); }
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }
    }

    private static void ResetCascadeData(PassData passData)
    {
        passData.rcColor1 = passData.rcColor2 = passData.rcColor3 = TextureHandle.nullHandle;
        passData.rcDepth1 = passData.rcDepth2 = passData.rcDepth3 = TextureHandle.nullHandle;
        passData.rcSourceColor = passData.rcSourceDepth = TextureHandle.nullHandle;
        passData.rcProbes = passData.rcUpperProbes = passData.rcUpper = TextureHandle.nullHandle;
        passData.rcSh0 = passData.rcSh1 = passData.rcSh2 = passData.rcSh3 = TextureHandle.nullHandle;
        passData.normals = TextureHandle.nullHandle;
        passData.tracedDepth = TextureHandle.nullHandle;
        passData.tracedDepthValid = false;
        passData.rcCascade = passData.rcInterval = passData.rcGrid = passData.rcJitter = Vector4.zero;
        passData.rcRotation = Matrix4x4.identity;
    }

    private static void BindCascades(RasterCommandBuffer cmd, PassData data)
    {
        if (data.sceneColor.IsValid()) { cmd.SetGlobalTexture(idSceneColor, data.sceneColor); }
        if (data.tracedDepthValid) { BindTracedDepth(cmd, data); }
        if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
        if (data.rcColor1.IsValid()) { cmd.SetGlobalTexture(idRcColor1, data.rcColor1); }
        if (data.rcColor2.IsValid()) { cmd.SetGlobalTexture(idRcColor2, data.rcColor2); }
        if (data.rcColor3.IsValid()) { cmd.SetGlobalTexture(idRcColor3, data.rcColor3); }
        if (data.rcDepth1.IsValid()) { cmd.SetGlobalTexture(idRcDepth1, data.rcDepth1); }
        if (data.rcDepth2.IsValid()) { cmd.SetGlobalTexture(idRcDepth2, data.rcDepth2); }
        if (data.rcDepth3.IsValid()) { cmd.SetGlobalTexture(idRcDepth3, data.rcDepth3); }
        if (data.rcSourceColor.IsValid()) { cmd.SetGlobalTexture(idRcSourceColor, data.rcSourceColor); }
        if (data.rcSourceDepth.IsValid()) { cmd.SetGlobalTexture(idRcSourceDepth, data.rcSourceDepth); }
        if (data.rcProbes.IsValid()) { cmd.SetGlobalTexture(idRcProbes, data.rcProbes); }
        if (data.rcUpperProbes.IsValid()) { cmd.SetGlobalTexture(idRcUpperProbes, data.rcUpperProbes); }
        if (data.rcUpper.IsValid()) { cmd.SetGlobalTexture(idRcUpper, data.rcUpper); }
        if (data.rcSh0.IsValid())
        {
            cmd.SetGlobalTexture(idRcSh0, data.rcSh0);
            cmd.SetGlobalTexture(idRcSh1, data.rcSh1);
            cmd.SetGlobalTexture(idRcSh2, data.rcSh2);
            cmd.SetGlobalTexture(idRcSh3, data.rcSh3);
        }
        cmd.SetGlobalVectorArray(idRcLevels, data.rcLevels);
        cmd.SetGlobalFloatArray(idRcSolidAngle, cascadeSolidAngles);
        cmd.SetGlobalVector(idRcCascade, data.rcCascade);
        cmd.SetGlobalVector(idRcInterval, data.rcInterval);
        cmd.SetGlobalVector(idRcGrid, data.rcGrid);
        cmd.SetGlobalVector(idRcJitter, data.rcJitter);
        cmd.SetGlobalMatrix(idRcRotation, data.rcRotation);
    }
}
