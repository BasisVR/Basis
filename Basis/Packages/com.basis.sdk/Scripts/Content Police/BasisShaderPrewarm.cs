using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public static class BasisShaderPrewarm
{
    // PassTypes worth attempting for the active render pipeline. URP/HDRP route every pass through
    // PassType.ScriptableRenderPipeline (LightMode tag distinguishes UniversalForward / ShadowCaster /
    // DepthOnly / Meta / etc within that category), so under SRP the seven legacy PassTypes can never
    // match and would just cost us rejected ArgumentExceptions. Resolved once on first use; Basis
    // doesn't switch pipelines at runtime.
    private static readonly PassType[] CandidatePassTypes = ResolveCandidatePassTypes();

    private static PassType[] ResolveCandidatePassTypes()
    {
        RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
        if (activePipeline != null)
        {
            // BasisDebug.Log($"BasisShaderPrewarm: detected SRP ({activePipeline.GetType().Name}), warming ScriptableRenderPipeline + DefaultUnlit only", BasisDebug.LogTag.Event);
            return new PassType[]
            {
                PassType.ScriptableRenderPipeline,
                PassType.ScriptableRenderPipelineDefaultUnlit,
            };
        }
        BasisDebug.Log("BasisShaderPrewarm: detected built-in render pipeline, warming legacy PassTypes", BasisDebug.LogTag.Event);
        return new PassType[]
        {
            PassType.Normal,
            PassType.ForwardBase,
            PassType.ForwardAdd,
            PassType.Deferred,
            PassType.ShadowCaster,
            PassType.MotionVectors,
            PassType.Meta,
        };
    }

    /// <summary>
    /// Compiles shader variants for every shared material on every renderer in <paramref name="renderers"/>
    /// before they're rendered for the first time.
    /// Caller is expected to have already walked the hierarchy (e.g. ContentPoliceControl's component
    /// pass) so we don't pay for a second GetComponentsInChildren.
    /// Synchronous: the caller is presumably on a loading screen, so the WarmUp stall is masked.
    /// </summary>
    public static void Warm(IList<Renderer> renderers, string label)
    {
        if (renderers == null)
        {
            return;
        }

        int count = renderers.Count;
        if (count == 0)
        {
            return;
        }

        ShaderVariantCollection svc = new ShaderVariantCollection();
        HashSet<MaterialKey> seen = new HashSet<MaterialKey>();
        PrewarmStats stats = default;

        for (int i = 0; i < count; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            Material[] mats = renderer.sharedMaterials;
            if (mats == null)
            {
                continue;
            }

            for (int j = 0; j < mats.Length; j++)
            {
                AddMaterial(mats[j], svc, seen, ref stats);
            }
        }

        WarmAndDestroy(svc, label, count, stats);
    }

    private struct PrewarmStats
    {
        public int materialsExamined;
        public int uniqueShaderKeywordCombos;
        public int variantsAttempted;
        public int variantsRejected;
        //   public string firstRejectionSample;
    }

    private static void AddMaterial(Material material, ShaderVariantCollection svc, HashSet<MaterialKey> seen, ref PrewarmStats stats)
    {
        if (material == null) return;
        Shader shader = material.shader;
        if (shader == null) return;

        stats.materialsExamined++;

        string[] keywords = material.shaderKeywords;
        keywords ??= Array.Empty<string>();
        // Sort keywords so two materials with the same set in different order share a dedup key
        // and the SVC sees a canonical variant signature.
        if (keywords.Length > 1)
        {
            Array.Sort(keywords, StringComparer.Ordinal);
        }

        MaterialKey key = new MaterialKey(shader.GetInstanceID(), JoinKeywords(keywords));
        if (!seen.Add(key))
        {
            return;
        }

        stats.uniqueShaderKeywordCombos++;

        for (int Index = 0; Index < CandidatePassTypes.Length; Index++)
        {
            PassType passType = CandidatePassTypes[Index];
            stats.variantsAttempted++;
            bool added = false;
            try
            {
                ShaderVariantCollection.ShaderVariant variant = new ShaderVariantCollection.ShaderVariant(shader, passType, keywords);
                svc.Add(variant);
                added = true;
            }
            catch (ArgumentException ex)
            {
                stats.variantsRejected++;
                //// if (stats.firstRejectionSample == null)
                //   {
                // One sample so a "0 variants" log explains why.
                //   stats.firstRejectionSample = $"shader '{shader.name}', pass={passType}, keywords=[{string.Join(",", keywords)}]: {ex.Message}";
                //}
            }

            // Locked / pre-baked shaders (Poiyomi-locked, ShaderGraph variants, etc.) inline every
            // toggle into the compiled bytecode and end up with no real multi_compile keywords —
            // their single valid variant is keywordless. material.shaderKeywords still reports the
            // pre-lock labels, which makes the keyword-aware lookup above miss. Retry with an empty
            // keyword set so those shaders still get warmed.
            if (!added && keywords.Length > 0)
            {
                stats.variantsAttempted++;
                try
                {
                    ShaderVariantCollection.ShaderVariant variant = new ShaderVariantCollection.ShaderVariant(shader, passType, Array.Empty<string>());
                    svc.Add(variant);
                }
                catch (ArgumentException)
                {
                    stats.variantsRejected++;
                }
            }
        }
    }

    private static void WarmAndDestroy(ShaderVariantCollection svc, string label, int rendererCount, PrewarmStats stats)
    {
        try
        {
            int shaderCount = svc.shaderCount;
            int variantCount = svc.variantCount;
            if (shaderCount == 0)
            {
                // Always log the empty case so a missing prewarm never fails silently —
                // surfaces stripped/broken shaders or PassType/keyword mismatches.
                //    string detail = stats.firstRejectionSample != null   ? $", first rejection: {stats.firstRejectionSample}" : string.Empty;
                //   BasisDebug.Log($"Shader prewarm '{label}': no variants collected ({rendererCount} renderer(s), {stats.materialsExamined} material(s), {stats.uniqueShaderKeywordCombos} unique combo(s), {stats.variantsRejected}/{stats.variantsAttempted} rejected{detail})", BasisDebug.LogTag.Event);
                return;
            }
            // Time the WarmUp specifically — the synchronous GPU compile cost we're surfacing.
            //  System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            svc.WarmUp();
            //  sw.Stop();
            //  BasisDebug.Log($"Shader prewarm '{label}': {rendererCount} renderer(s), {stats.materialsExamined} material(s), {shaderCount} shader(s), {variantCount} variant(s) ({stats.variantsRejected}/{stats.variantsAttempted} rejected), {sw.ElapsedMilliseconds} ms", BasisDebug.LogTag.Event);
        }
        finally
        {
            UnityEngine.Object.Destroy(svc);
        }
    }

    private static string JoinKeywords(string[] keywords)
    {
        if (keywords.Length == 0) return string.Empty;
        return string.Join("|", keywords);
    }

    private readonly struct MaterialKey : IEquatable<MaterialKey>
    {
        private readonly int shaderId;
        private readonly string keywords;
        public MaterialKey(int shaderId, string keywords)
        {
            this.shaderId = shaderId;
            this.keywords = keywords;
        }
        public bool Equals(MaterialKey other) => shaderId == other.shaderId && keywords == other.keywords;
        public override bool Equals(object obj) => obj is MaterialKey k && Equals(k);
        public override int GetHashCode() => (shaderId * 397) ^ (keywords != null ? keywords.GetHashCode() : 0);
    }
}
