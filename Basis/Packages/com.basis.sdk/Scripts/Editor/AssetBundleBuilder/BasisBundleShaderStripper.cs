using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public enum BasisBundleContentKind { None, Avatar, Prop, Scene }

[NoAutoStaticsCleanup]
public static class BasisBundleShaderStripScope
{
    public static BasisBundleContentKind Kind = BasisBundleContentKind.None;
    public static BuildTarget Target;
    public static bool StripLodCrossFade, StripBakeOnlyPasses, StripSpaceWarpPass, StripDotsInstancing, WriteReport = true;
    private static readonly Dictionary<string, ShaderTally> Tallies = new Dictionary<string, ShaderTally>();
    private static readonly object Gate = new object();
    private static Stopwatch Clock;
    private static int TotalIn, TotalOut;

    public struct ShaderTally { public int In, Out; }

    public static bool TargetUsesApplicationSpaceWarp => Target == BuildTarget.Android;

    public static void Begin(BasisBundleContentKind kind, BasisAssetBundleObject settings, BuildTarget target)
    {
        lock (Gate)
        {
            Kind = kind;
            Target = target;
            StripLodCrossFade = settings == null || settings.StripLodCrossFadeVariants;
            StripBakeOnlyPasses = settings == null || settings.StripBakeOnlyShaderPasses;
            StripSpaceWarpPass = settings == null || settings.StripSpaceWarpPassOffQuest;
            StripDotsInstancing = settings != null && settings.StripDotsInstancingVariants;
            WriteReport = settings == null || settings.WriteShaderVariantReport;
            Tallies.Clear();
            BasisBundleShaderStripper.ClearKeywordCache();
            TotalIn = 0;
            TotalOut = 0;
            Clock = Stopwatch.StartNew();
        }
    }

    public static void Record(string shaderName, int variantsIn, int variantsOut)
    {
        lock (Gate)
        {
            TotalIn += variantsIn;
            TotalOut += variantsOut;
            Tallies.TryGetValue(shaderName, out ShaderTally tally);
            tally.In += variantsIn;
            tally.Out += variantsOut;
            Tallies[shaderName] = tally;
        }
    }

    public static void End(string bundleName)
    {
        lock (Gate)
        {
            if (Kind == BasisBundleContentKind.None) return;
            long elapsed = Clock != null ? Clock.ElapsedMilliseconds : 0;
            if (TotalIn > 0)
            {
                int removed = TotalIn - TotalOut;
                BasisDebug.Log($"Shader variants for {Kind} bundle on {Target}: {TotalOut}/{TotalIn} kept, {removed} stripped ({(removed * 100f) / TotalIn:F1}%), bundle built in {elapsed} ms");
                if (WriteReport) Write(bundleName, elapsed);
            }
            Kind = BasisBundleContentKind.None;
            Clock = null;
        }
    }

    private static void Write(string bundleName, long elapsed)
    {
        try
        {
            if (!Directory.Exists(AssetBundleBuilder.ReportDirectoryPath)) Directory.CreateDirectory(AssetBundleBuilder.ReportDirectoryPath);
            ShaderVariantReport report = new ShaderVariantReport { kind = Kind.ToString(), platform = Target.ToString(), bundle = bundleName, variantsBefore = TotalIn, variantsAfter = TotalOut, buildMilliseconds = elapsed };
            List<ShaderVariantReport.Entry> entries = new List<ShaderVariantReport.Entry>(Tallies.Count);
            foreach (KeyValuePair<string, ShaderTally> pair in Tallies) entries.Add(new ShaderVariantReport.Entry { shader = pair.Key, variantsBefore = pair.Value.In, variantsAfter = pair.Value.Out });
            entries.Sort((a, b) => (b.variantsBefore - b.variantsAfter).CompareTo(a.variantsBefore - a.variantsAfter));
            report.shaders = entries.ToArray();
            File.WriteAllText(Path.Combine(AssetBundleBuilder.ReportDirectoryPath, $"ShaderVariants_{Target}.json"), JsonUtility.ToJson(report, true));
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Failed to write shader variant report: {ex.Message}");
        }
    }

    [Serializable]
    public class ShaderVariantReport
    {
        public string kind, platform, bundle;
        public int variantsBefore, variantsAfter;
        public long buildMilliseconds;
        public Entry[] shaders;

        [Serializable]
        public struct Entry { public string shader; public int variantsBefore, variantsAfter; }
    }
}

[NoAutoStaticsCleanup]
public class BasisBundleShaderStripper : IPreprocessShaders
{
    public int callbackOrder => 90;

    private static readonly string[] LodCrossFadeKeywords = { "LOD_FADE_CROSSFADE" };
    private static readonly string[] DotsInstancingKeywords = { "DOTS_INSTANCING_ON" };
    private static readonly Dictionary<Shader, LocalKeyword[]> DoomedByShader = new Dictionary<Shader, LocalKeyword[]>();
    private const string MetaPassName = "Meta", Universal2DPassName = "Universal2D", SpaceWarpPassName = "XRMotionVectors";

    public static void ClearKeywordCache() { DoomedByShader.Clear(); }

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (BasisBundleShaderStripScope.Kind != BasisBundleContentKind.Avatar) return;
        int before = data.Count;
        if (before == 0) return;

        if (IsDeadPass(snippet))
        {
            for (int index = data.Count - 1; index >= 0; index--) data.RemoveAt(index);
            BasisBundleShaderStripScope.Record(shader.name, before, data.Count);
            return;
        }

        LocalKeyword[] doomed = CollectDoomedKeywords(shader);
        if (doomed.Length != 0)
        {
            for (int index = data.Count - 1; index >= 0; index--)
            {
                if (AnyEnabled(data[index].shaderKeywordSet, doomed)) data.RemoveAt(index);
            }
        }
        BasisBundleShaderStripScope.Record(shader.name, before, data.Count);
    }

    private static bool IsDeadPass(ShaderSnippetData snippet)
    {
        if (BasisBundleShaderStripScope.StripSpaceWarpPass && !BasisBundleShaderStripScope.TargetUsesApplicationSpaceWarp && snippet.passName == SpaceWarpPassName) return true;
        if (!BasisBundleShaderStripScope.StripBakeOnlyPasses) return false;
        return snippet.passType == PassType.Meta || snippet.passName == MetaPassName || snippet.passName == Universal2DPassName;
    }

    private static LocalKeyword[] CollectDoomedKeywords(Shader shader)
    {
        if (DoomedByShader.TryGetValue(shader, out LocalKeyword[] cached)) return cached;
        List<LocalKeyword> doomed = new List<LocalKeyword>(LodCrossFadeKeywords.Length + DotsInstancingKeywords.Length);
        if (BasisBundleShaderStripScope.StripLodCrossFade) Append(shader, LodCrossFadeKeywords, doomed);
        if (BasisBundleShaderStripScope.StripDotsInstancing) Append(shader, DotsInstancingKeywords, doomed);
        cached = doomed.ToArray();
        DoomedByShader[shader] = cached;
        return cached;
    }

    private static void Append(Shader shader, string[] names, List<LocalKeyword> into)
    {
        LocalKeywordSpace space = shader.keywordSpace;
        for (int index = 0; index < names.Length; index++)
        {
            LocalKeyword keyword = space.FindKeyword(names[index]);
            if (keyword.isValid) into.Add(keyword);
        }
    }

    private static bool AnyEnabled(ShaderKeywordSet set, LocalKeyword[] keywords)
    {
        for (int index = 0; index < keywords.Length; index++)
        {
            if (set.IsEnabled(keywords[index])) return true;
        }
        return false;
    }
}
