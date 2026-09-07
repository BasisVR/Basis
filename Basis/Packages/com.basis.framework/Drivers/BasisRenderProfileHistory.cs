using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Highlight;
using Basis.Scripts.Networking;
using Basis.Scripts.Rendering;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
#if BASIS_HAS_RTAO && !UNITY_ANDROID
using Basis.Rendering.RTAO;
#endif

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Multi-frame statistical capture over BasisPerformanceBarData's per-pass GPU/CPU segments plus
    /// the finer per-substage GI/RTAO statics, exported as JSON. Where BasisPerformanceBarData holds
    /// one EMA-smoothed instant per segment for a live HUD, this records every sampled frame into a
    /// fixed-size buffer for the capture window and reports min/avg/median/p95/max/stddev.
    /// </summary>
    public static class BasisRenderProfileHistory
    {
        public const int DefaultFrames = 300;
        private const double NsToMs = 1.0 / 1_000_000.0;
        private const ProfilerRecorderOptions MarkerOptions =
            ProfilerRecorderOptions.WrapAroundWhenCapacityReached |
            ProfilerRecorderOptions.StartImmediately |
            ProfilerRecorderOptions.SumAllSamplesInFrame;
        private static readonly string[] AlwaysIncludeMarkerPrefixes = { "BasisVisibility.", "BasisNamePlate." };
        private const int MaxDiscoveredMarkers = 500;
        private const int TopMarkerReportCount = 25;
        // Found by inspecting real captures, not derivable from the API: these enumerate as
        // TimeNanoseconds stats but are wait/idle/cumulative counters, not per-frame work durations
        // (e.g. "Idle" read 480ms average inside an ~8ms frame). MaxSaneMarkerMs alone doesn't catch
        // the smaller ones (EngineJob ~9ms, WaitForTargetFPS ~3ms — both plausible-looking but still
        // not real per-frame cost). This list is necessarily incomplete; expect to extend it as new
        // ones turn up in future captures rather than treating it as exhaustive.
        private static readonly string[] ExcludedMarkerNames =
        {
            "BeginJob", "EndJob", "ScheduleJob", "WaitForCompleted", "ScheduleAllocJob", "KickJobs",
            "Idle", "Semaphore.WaitForSignal", "EngineJob", "WaitForTargetFPS",
        };

        private struct Stat
        {
            public float Min, Max, Avg, Median, P95, StdDev;
            public int Count;
        }

        private sealed class Series
        {
            public readonly string Name, Category;
            public readonly Func<float> Read;
            public readonly float[] Values;
            public int Count;
            public Series(string name, string category, Func<float> read, int capacity)
            {
                Name = name; Category = category; Read = read; Values = new float[capacity];
            }
        }

        private sealed class MarkerSeries
        {
            public readonly string Name;
            public ProfilerRecorder Recorder;
            public readonly float[] Values;
            public int Count;
            public MarkerSeries(string name, int capacity) { Name = name; Values = new float[capacity]; }
        }

        private static bool capturing;
        private static int framesTarget, framesDone;
        private static string captureReason;
        private static Action<string> onComplete;
        private static BasisFrameBottleneckReading lastReading;
        private static List<Series> frameSeries, gpuSeries, cpuSeries, invocationSeries;
        private static List<MarkerSeries> markerSeries;
        private static readonly List<ProfilerRecorderHandle> handleScratch = new List<ProfilerRecorderHandle>(1024);

        public static bool IsCapturing => capturing;
        public static string LastWrittenPath { get; private set; }

        /// <summary>
        /// Begins a capture spanning the next <paramref name="frames"/> rendered frames (via
        /// BasisFrameClock, so it advances identically in the Editor and a Development Build).
        /// <paramref name="onJsonReady"/> receives the built JSON, or null if a capture was already
        /// running and this call was refused.
        /// </summary>
        public static bool StartCapture(string reason, int frames, Action<string> onJsonReady)
        {
            if (capturing)
            {
                onJsonReady?.Invoke(null);
                return false;
            }

            capturing = true;
            captureReason = string.IsNullOrEmpty(reason) ? "manual" : reason;
            framesTarget = Mathf.Max(1, frames);
            framesDone = 0;
            onComplete = onJsonReady;

            BuildFrameSeries(framesTarget);
            BuildGpuSeries(framesTarget);
            BuildCpuSeries(framesTarget);
            BuildInvocationSeries(framesTarget);
            DiscoverMarkerSeries(framesTarget);

            BasisPerformanceBarData.AddSubscriber();
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += OnTick;
            return true;
        }

        /// <summary>Runs a capture and writes the JSON to persistentDataPath/ProfilerCaptures — the
        /// writable location on every platform including Quest/Android, unlike Application.dataPath.</summary>
        public static bool CaptureToDisk(string reason, int frames = DefaultFrames)
        {
            return StartCapture(reason, frames, json =>
            {
                if (string.IsNullOrEmpty(json)) return;
                try
                {
                    string dir = Path.Combine(Application.persistentDataPath, "ProfilerCaptures");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "renderpasses_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json");
                    File.WriteAllText(path, json);
                    LastWrittenPath = path;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[BasisRenderProfileHistory] Failed to write capture: " + e.Message);
                }
            });
        }

        private static void OnTick()
        {
            BasisPerformanceBarData.Sample();
            lastReading = BasisFrameBottleneck.Read();
            PushAll(frameSeries);
            PushAll(gpuSeries);
            PushAll(cpuSeries);
            PushAll(invocationSeries);
            PushMarkers();
            framesDone++;
            if (framesDone >= framesTarget) FinishCapture();
        }

        private static void PushAll(List<Series> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Series s = list[i];
                if (s.Count < s.Values.Length) s.Values[s.Count++] = s.Read();
            }
        }

        private static void PushMarkers()
        {
            for (int i = 0; i < markerSeries.Count; i++)
            {
                MarkerSeries m = markerSeries[i];
                if (!m.Recorder.Valid || m.Recorder.Count == 0 || m.Count >= m.Values.Length) continue;
                m.Values[m.Count++] = (float)(m.Recorder.GetSample(m.Recorder.Count - 1).Value * NsToMs);
            }
        }

        private static void FinishCapture()
        {
            BasisFrameClock.OnTick -= OnTick;
            BasisFrameClock.RemoveRequest();
            BasisPerformanceBarData.RemoveSubscriber();
            DisposeMarkers();
            capturing = false;

            string json = BuildJson();
            Action<string> callback = onComplete;
            onComplete = null;
            callback?.Invoke(json);
        }

        private static void BuildFrameSeries(int capacity)
        {
            frameSeries = new List<Series>(6)
            {
                new Series("FrameMs", "Frame", () => (float)lastReading.FrameMs, capacity),
                new Series("MainThreadMs", "Frame", () => (float)lastReading.MainThreadMs, capacity),
                new Series("RenderThreadMs", "Frame", () => (float)lastReading.RenderThreadMs, capacity),
                new Series("PresentWaitMs", "Frame", () => (float)lastReading.PresentWaitMs, capacity),
                new Series("CpuBusyMs", "Frame", () => (float)lastReading.CpuBusyMs, capacity),
                new Series("GpuMs", "Frame", () => (float)lastReading.GpuMs, capacity),
            };
        }

        private static void AddGpuSegment(BasisPerformanceGpuSegment segment, string category, int capacity)
        {
            int index = (int)segment;
            gpuSeries.Add(new Series(segment.ToString(), category, () => BasisPerformanceBarData.GpuMs[index], capacity));
        }

        private static void BuildGpuSeries(int capacity)
        {
            gpuSeries = new List<Series>(24);
            AddGpuSegment(BasisPerformanceGpuSegment.Shadows, "Shadows", capacity);
            AddGpuSegment(BasisPerformanceGpuSegment.Opaque, "Opaque", capacity);
            AddGpuSegment(BasisPerformanceGpuSegment.Transparent, "Transparent", capacity);
            AddGpuSegment(BasisPerformanceGpuSegment.Other, "Other", capacity);
#if BASIS_HAS_GI && !UNITY_ANDROID
            AddGpuSegment(BasisPerformanceGpuSegment.GlobalIllumination, "GlobalIllumination", capacity);
            gpuSeries.Add(new Series("GI RayPrepass", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsRayPrepass, capacity));
            gpuSeries.Add(new Series("GI RayTrace", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsRayTrace, capacity));
            gpuSeries.Add(new Series("GI RayResolve", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsRayResolve, capacity));
            gpuSeries.Add(new Series("GI CopyColor", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsCopyColor, capacity));
            gpuSeries.Add(new Series("GI CoarseDepth", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsCoarseDepth, capacity));
            gpuSeries.Add(new Series("GI Trace", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsTrace, capacity));
            gpuSeries.Add(new Series("GI Temporal", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsTemporal, capacity));
            gpuSeries.Add(new Series("GI Blur", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsBlur, capacity));
            gpuSeries.Add(new Series("GI Composite", "GlobalIllumination", () => BasisGlobalIlluminationPass.GpuMsComposite, capacity));

            AddGpuSegment(BasisPerformanceGpuSegment.Reflections, "Reflections", capacity);
            gpuSeries.Add(new Series("Reflections Prepass", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsPrepass, capacity));
            gpuSeries.Add(new Series("Reflections Trace", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsTrace, capacity));
            gpuSeries.Add(new Series("Reflections Resolve", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsResolve, capacity));
            gpuSeries.Add(new Series("Reflections Temporal", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsTemporal, capacity));
            gpuSeries.Add(new Series("Reflections Blur", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsBlur, capacity));
            gpuSeries.Add(new Series("Reflections Upsample", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsUpsample, capacity));
            gpuSeries.Add(new Series("Reflections Publish", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.GpuMsPublish, capacity));
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            AddGpuSegment(BasisPerformanceGpuSegment.Rtao, "Rtao", capacity);
            gpuSeries.Add(new Series("Rtao AfterOpaque", "Rtao", () => BasisRTAOAfterOpaquePass.GpuMs, capacity));
#endif
            gpuSeries.Add(new Series("Highlight", "Other", () => BasisHighlightPass.GpuMs, capacity));
            gpuSeries.Add(new Series("VariableRateShading", "Other", () => BasisVariableRateShadingPass.GpuMs, capacity));
        }

        private static void AddCpuSegment(BasisPerformanceCpuSegment segment, int capacity)
        {
            int index = (int)segment;
            cpuSeries.Add(new Series(segment.ToString(), segment.ToString(), () => BasisPerformanceBarData.CpuMs[index], capacity));
        }

        private static void BuildCpuSeries(int capacity)
        {
            cpuSeries = new List<Series>(9);
            AddCpuSegment(BasisPerformanceCpuSegment.EventDriver, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Ik, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Movement, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.AvatarLoad, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Networking, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Jiggle, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Voice, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.RenderDispatch, capacity);
            AddCpuSegment(BasisPerformanceCpuSegment.Other, capacity);
        }

        private static void BuildInvocationSeries(int capacity)
        {
            invocationSeries = new List<Series>(3);
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            invocationSeries.Add(new Series("Rtao", "Rtao", () => BasisRTAOPass.InvocationsThisFrame, capacity));
#endif
#if BASIS_HAS_GI && !UNITY_ANDROID
            invocationSeries.Add(new Series("GlobalIllumination", "GlobalIllumination", () => BasisGlobalIlluminationPass.InvocationsThisFrame, capacity));
            invocationSeries.Add(new Series("Reflections", "Reflections", () => BasisGlobalIlluminationPass.SpecularPass.InvocationsThisFrame, capacity));
#endif
        }

        /// <summary>
        /// One-time unfiltered scan of every TimeNanoseconds stat in the process — the same breadth
        /// BasisPerformanceBarData's RescanCpuMarkers uses, minus its indefinite-session rescan/backoff
        /// (a capture is seconds long, one pass is enough). BasisPerformanceBarData's own 9 CPU segments
        /// only classify a handful of known "BasisDriver."/"BasisEerie." prefixes and silently fold
        /// everything else into its "Other" bucket — on a live capture "Other" was found to dwarf every
        /// named segment combined and to be the only thing that actually scaled with avatar count, so
        /// this exists to find out what is actually in there rather than staying blind to it. Includes
        /// non-Basis engine stats (physics/animation/GC/etc.) deliberately — the answer might not be
        /// Basis code at all.
        /// </summary>
        private static void DiscoverMarkerSeries(int capacity)
        {
            markerSeries = new List<MarkerSeries>(64);
            handleScratch.Clear();
            ProfilerRecorderHandle.GetAvailable(handleScratch);
            for (int h = 0; h < handleScratch.Count && markerSeries.Count < MaxDiscoveredMarkers; h++)
            {
                ProfilerRecorderHandle handle = handleScratch[h];
                ProfilerRecorderDescription desc = ProfilerRecorderHandle.GetDescription(handle);
                if (desc.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;

                MarkerSeries series = new MarkerSeries(desc.Name, capacity);
                try { series.Recorder = new ProfilerRecorder(handle, 1, MarkerOptions); }
                catch { continue; }
                markerSeries.Add(series);
            }
        }

        /// <summary>Every always-include marker, plus the costliest others up to the report cap.</summary>
        // A live sweep found Unity's own Job System counters (BeginJob/EndJob/ScheduleJob/...) enumerate
        // as TimeNanoseconds stats but report a lifetime cumulative total, not a per-frame duration —
        // hundreds of millions of "ms" once converted. SumAllSamplesInFrame does not fix this because
        // the values were never per-frame samples to begin with. Genuine per-frame CPU cost is well
        // under a frame budget; anything past this is provably not that, so it is excluded outright
        // rather than merely deprioritized (leaving it eligible for the ranking would just mean it wins
        // every time and the real top costs never surface).
        private const float MaxSaneMarkerMs = 1000f;

        private static List<MarkerSeries> SelectReportedMarkers()
        {
            List<MarkerSeries> always = new List<MarkerSeries>();
            List<MarkerSeries> rest = new List<MarkerSeries>();
            for (int i = 0; i < markerSeries.Count; i++)
            {
                MarkerSeries m = markerSeries[i];
                bool forced = false;
                for (int p = 0; p < AlwaysIncludeMarkerPrefixes.Length; p++)
                {
                    if (m.Name.StartsWith(AlwaysIncludeMarkerPrefixes[p], StringComparison.Ordinal)) { forced = true; break; }
                }
                if (!forced && Array.IndexOf(ExcludedMarkerNames, m.Name) >= 0) continue;
                if (!forced && Compute(m.Values, m.Count).Avg > MaxSaneMarkerMs) continue;
                (forced ? always : rest).Add(m);
            }
            rest.Sort((a, b) => Compute(b.Values, b.Count).Avg.CompareTo(Compute(a.Values, a.Count).Avg));
            if (rest.Count > TopMarkerReportCount) rest.RemoveRange(TopMarkerReportCount, rest.Count - TopMarkerReportCount);
            always.AddRange(rest);
            return always;
        }

        private static string CategoryFor(string markerName)
        {
            for (int p = 0; p < AlwaysIncludeMarkerPrefixes.Length; p++)
            {
                if (markerName.StartsWith(AlwaysIncludeMarkerPrefixes[p], StringComparison.Ordinal)) return "Rendering";
            }
            return "Diagnostic";
        }

        private static void DisposeMarkers()
        {
            for (int i = 0; i < markerSeries.Count; i++)
                if (markerSeries[i].Recorder.Valid) markerSeries[i].Recorder.Dispose();
        }

        private static Stat Compute(float[] values, int count)
        {
            if (count == 0) return default;

            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            for (int i = 0; i < count; i++)
            {
                float v = values[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            float avg = sum / count;

            float variance = 0f;
            for (int i = 0; i < count; i++)
            {
                float d = values[i] - avg;
                variance += d * d;
            }

            float[] sorted = new float[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);

            return new Stat
            {
                Min = min,
                Max = max,
                Avg = avg,
                StdDev = Mathf.Sqrt(variance / count),
                Median = Percentile(sorted, 0.5f),
                P95 = Percentile(sorted, 0.95f),
                Count = count
            };
        }

        private static float Percentile(float[] sorted, float p)
        {
            int n = sorted.Length;
            if (n == 1) return sorted[0];
            float idx = p * (n - 1);
            int lo = (int)idx;
            int hi = Mathf.Min(lo + 1, n - 1);
            float frac = idx - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        private static string QualityTierName(int tier)
        {
            switch (tier)
            {
                case BasisQualityTier.VeryLow: return "VeryLow";
                case BasisQualityTier.Low: return "Low";
                case BasisQualityTier.Medium: return "Medium";
                case BasisQualityTier.High: return "High";
                default: return "Ultra";
            }
        }

        private static string BuildJson()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(24 * 1024);
            sb.Append("{\n");
            sb.Append("  \"schemaVersion\": 1,\n");
            sb.Append("  \"capturedAt\": \"").Append(DateTime.Now.ToString("o", ci)).Append("\",\n");
            sb.Append("  \"reason\": \"").Append(EscapeJson(captureReason)).Append("\",\n");
            sb.Append("  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
            sb.Append("  \"platform\": \"").Append(Application.platform).Append("\",\n");
            sb.Append("  \"graphicsDeviceType\": \"").Append(SystemInfo.graphicsDeviceType).Append("\",\n");
            sb.Append("  \"xrActive\": ").Append(UnityEngine.XR.XRSettings.isDeviceActive ? "true" : "false").Append(",\n");
            sb.Append("  \"screenWidth\": ").Append(Screen.width).Append(",\n");
            sb.Append("  \"screenHeight\": ").Append(Screen.height).Append(",\n");
            sb.Append("  \"qualityTier\": \"").Append(QualityTierName(BasisQualityTier.Current)).Append("\",\n");
            sb.Append("  \"performanceModeActive\": ").Append(BasisPerformanceMode.IsActive ? "true" : "false").Append(",\n");
            sb.Append("  \"performanceModeLevel\": \"").Append(BasisPerformanceMode.ActiveLevel).Append("\",\n");
#if BASIS_HAS_GI && !UNITY_ANDROID
            sb.Append("  \"giQuality\": \"").Append(BasisGlobalIlluminationSettings.Current.quality).Append("\",\n");
#endif
            sb.Append("  \"remoteAvatarCount\": ").Append(BasisNetworkPlayers.ReceiverCount).Append(",\n");
            sb.Append("  \"sampleFrames\": ").Append(framesDone).Append(",\n");

            sb.Append("  \"frameTiming\": {\n");
            for (int i = 0; i < frameSeries.Count; i++)
            {
                Series s = frameSeries[i];
                sb.Append("    \"").Append(s.Name).Append("\": ");
                AppendStat(sb, Compute(s.Values, s.Count), ci);
                sb.Append(i == frameSeries.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  },\n");

            sb.Append("  \"gpuPasses\": [\n");
            for (int i = 0; i < gpuSeries.Count; i++)
            {
                Series s = gpuSeries[i];
                sb.Append("    {\"name\":\"").Append(EscapeJson(s.Name)).Append("\",\"category\":\"").Append(EscapeJson(s.Category)).Append("\",\"gpuMs\":");
                AppendStat(sb, Compute(s.Values, s.Count), ci);
                sb.Append("}");
                sb.Append(i == gpuSeries.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"invocationsPerFrame\": [\n");
            for (int i = 0; i < invocationSeries.Count; i++)
            {
                Series s = invocationSeries[i];
                sb.Append("    {\"name\":\"").Append(EscapeJson(s.Name)).Append("\",\"count\":");
                AppendStat(sb, Compute(s.Values, s.Count), ci);
                sb.Append("}");
                sb.Append(i == invocationSeries.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"cpuSegments\": [\n");
            List<MarkerSeries> reportedMarkers = SelectReportedMarkers();
            int totalCpu = cpuSeries.Count + reportedMarkers.Count;
            int idx = 0;
            for (int i = 0; i < cpuSeries.Count; i++, idx++)
            {
                Series s = cpuSeries[i];
                sb.Append("    {\"name\":\"").Append(EscapeJson(s.Name)).Append("\",\"category\":\"").Append(EscapeJson(s.Category)).Append("\",\"cpuMs\":");
                AppendStat(sb, Compute(s.Values, s.Count), ci);
                sb.Append("}");
                sb.Append(idx == totalCpu - 1 ? "\n" : ",\n");
            }
            for (int i = 0; i < reportedMarkers.Count; i++, idx++)
            {
                MarkerSeries m = reportedMarkers[i];
                sb.Append("    {\"name\":\"").Append(EscapeJson(m.Name)).Append("\",\"category\":\"").Append(CategoryFor(m.Name)).Append("\",\"cpuMs\":");
                AppendStat(sb, Compute(m.Values, m.Count), ci);
                sb.Append("}");
                sb.Append(idx == totalCpu - 1 ? "\n" : ",\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void AppendStat(StringBuilder sb, Stat s, CultureInfo ci)
        {
            sb.Append("{\"avg\":").Append(s.Avg.ToString("G9", ci));
            sb.Append(",\"min\":").Append(s.Min == float.MaxValue ? "0" : s.Min.ToString("G9", ci));
            sb.Append(",\"median\":").Append(s.Median.ToString("G9", ci));
            sb.Append(",\"p95\":").Append(s.P95.ToString("G9", ci));
            sb.Append(",\"max\":").Append(s.Max == float.MinValue ? "0" : s.Max.ToString("G9", ci));
            sb.Append(",\"stddev\":").Append(s.StdDev.ToString("G9", ci));
            sb.Append(",\"samples\":").Append(s.Count);
            sb.Append("}");
        }

        private static string EscapeJson(string s)
        {
            return string.IsNullOrEmpty(s) ? string.Empty :
                s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Human-readable digest of the same data BuildJson produced, valid to call any time after a
        /// capture finishes (the series buffers stay populated until the next StartCapture).
        /// </summary>
        public static string BuildMarkdown()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine($"# Basis Render Pass Capture — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"- Reason: {captureReason}");
            sb.AppendLine($"- Unity: {Application.unityVersion}  Platform: {Application.platform}  Graphics API: {SystemInfo.graphicsDeviceType}");
            sb.AppendLine($"- Resolution: {Screen.width}x{Screen.height}  XR: {UnityEngine.XR.XRSettings.isDeviceActive}");
            sb.AppendLine($"- Quality tier: {QualityTierName(BasisQualityTier.Current)}  Performance mode: {BasisPerformanceMode.ActiveLevel}");
            sb.AppendLine($"- Sample frames: {framesDone}  Remote avatars: {BasisNetworkPlayers.ReceiverCount}");
            sb.AppendLine();

            sb.AppendLine("## Frame timing (ms)");
            AppendStatTable(sb, frameSeries, ci);

            sb.AppendLine("## GPU passes (ms)");
            AppendStatTable(sb, gpuSeries, ci);

            sb.AppendLine("## Invocations per frame");
            AppendStatTable(sb, invocationSeries, ci);

            sb.AppendLine("## CPU segments (ms)");
            sb.AppendLine("| Name | Avg | Median | P95 | Max | Samples |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            for (int i = 0; i < cpuSeries.Count; i++)
            {
                AppendStatRow(sb, cpuSeries[i].Name, Compute(cpuSeries[i].Values, cpuSeries[i].Count), ci);
            }
            List<MarkerSeries> reportedMarkers = SelectReportedMarkers();
            for (int i = 0; i < reportedMarkers.Count; i++)
            {
                MarkerSeries m = reportedMarkers[i];
                AppendStatRow(sb, m.Name, Compute(m.Values, m.Count), ci);
            }
            sb.AppendLine();

            return sb.ToString();
        }

        private static void AppendStatTable(StringBuilder sb, List<Series> list, CultureInfo ci)
        {
            sb.AppendLine("| Name | Avg | Median | P95 | Max | Samples |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            for (int i = 0; i < list.Count; i++)
            {
                AppendStatRow(sb, list[i].Name, Compute(list[i].Values, list[i].Count), ci);
            }
            sb.AppendLine();
        }

        private static void AppendStatRow(StringBuilder sb, string name, Stat s, CultureInfo ci)
        {
            sb.AppendLine($"| {name} | {s.Avg.ToString("F4", ci)} | {s.Median.ToString("F4", ci)} | {s.P95.ToString("F4", ci)} | {s.Max.ToString("F4", ci)} | {s.Count} |");
        }
    }
}
