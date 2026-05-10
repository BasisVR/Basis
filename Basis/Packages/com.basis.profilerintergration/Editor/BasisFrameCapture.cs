#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Basis.Scripts.Profiler;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace Basis.Scripts.Profiler.EditorTools
{
    /// <summary>
    /// One-shot frame capture. Pulls full hierarchical sample data for every
    /// recorded thread of the most recent frame via ProfilerDriver +
    /// RawFrameDataView, plus FrameTimingManager CPU/GPU numbers, plus the
    /// existing Basis network counters and BasisEventDriverProfilerData
    /// fields. Writes:
    ///   ProfilerCaptures/frame_&lt;ts&gt;.json  – full per-thread sample tree
    ///   ProfilerCaptures/frame_&lt;ts&gt;.md    – human-readable digest (clipboard too)
    ///   ProfilerCaptures/frame_&lt;ts&gt;.data  – native Profiler trace, loadable in Profiler window
    /// </summary>
    public static class BasisFrameCapture
    {
        private const string MenuPath = "Basis/Profiler/Capture Single Frame";
        private const string CaptureDirName = "ProfilerCaptures";

        // Samples shorter than this are dropped from the JSON dump (root sample is always kept).
        // Set to 0 to capture every marker. 10_000 ns = 10 µs.
        private const long MinSampleDurationNs = 10_000;

        private const int TopMainThreadHotspots = 40;
        private const int TopOtherThreadHotspots = 10;
        private const int FramesToWait = 3; // start recorders → next frame runs → next frame is fully sampled

        private static readonly (ProfilerCategory cat, string stat, string display)[] BuiltInStats =
        {
            (ProfilerCategory.Internal, "Main Thread",            "Main Thread (ns)"),
            (ProfilerCategory.Internal, "Render Thread",          "Render Thread (ns)"),
            (ProfilerCategory.Memory,   "System Used Memory",     "System Used Memory (bytes)"),
            (ProfilerCategory.Memory,   "Total Used Memory",      "Total Used Memory (bytes)"),
            (ProfilerCategory.Memory,   "Total Reserved Memory",  "Total Reserved Memory (bytes)"),
            (ProfilerCategory.Memory,   "GC Used Memory",         "GC Used Memory (bytes)"),
            (ProfilerCategory.Memory,   "GC Reserved Memory",     "GC Reserved Memory (bytes)"),
            (ProfilerCategory.Memory,   "Texture Memory",         "Texture Memory (bytes)"),
            (ProfilerCategory.Memory,   "Mesh Memory",            "Mesh Memory (bytes)"),
            (ProfilerCategory.Memory,   "GC Allocated In Frame",  "GC Alloc In Frame (bytes)"),
            (ProfilerCategory.Render,   "Draw Calls Count",       "Draw Calls"),
            (ProfilerCategory.Render,   "Triangles Count",        "Triangles"),
            (ProfilerCategory.Render,   "Vertices Count",         "Vertices"),
            (ProfilerCategory.Render,   "SetPass Calls Count",    "SetPass Calls"),
            (ProfilerCategory.Render,   "Batches Count",          "Batches"),
            (ProfilerCategory.Render,   "Shadow Casters Count",   "Shadow Casters"),
        };

        private static readonly string[] BasisNetworkLabels =
        {
            BasisNetworkProfiler.AudioSegmentDataMessageText,
            BasisNetworkProfiler.AuthenticationMessageText,
            BasisNetworkProfiler.AvatarDataMessageText,
            BasisNetworkProfiler.CreateAllRemoteMessageText,
            BasisNetworkProfiler.CreateSingleRemoteMessageText,
            BasisNetworkProfiler.LocalAvatarSyncMessageText,
            BasisNetworkProfiler.OwnershipTransferMessageText,
            BasisNetworkProfiler.RequestOwnershipTransferMessageText,
            BasisNetworkProfiler.PlayerIdMessageText,
            BasisNetworkProfiler.PlayerMetaDataMessageText,
            BasisNetworkProfiler.ReadyMessageText,
            BasisNetworkProfiler.SceneDataMessageText,
            BasisNetworkProfiler.ServerAudioSegmentMessageText,
            BasisNetworkProfiler.ServerAvatarChangeMessageText,
            BasisNetworkProfiler.ServerSideSyncPlayerMessageText,
            BasisNetworkProfiler.AudioRecipientsMessageText,
            BasisNetworkProfiler.AvatarChangeMessageText,
            BasisNetworkProfiler.ServerAvatarDataMessageText,
            BasisNetworkProfiler.DisconnectionMessageText,
            BasisNetworkProfiler.ShoutVoiceMessageText,
            BasisNetworkProfiler.GetOwnershipMessageText,
            BasisNetworkProfiler.ChangeOwnershipMessageText,
            BasisNetworkProfiler.RemoveOwnershipMessageText,
            BasisNetworkProfiler.PlayerAvatarMessageText,
            BasisNetworkProfiler.NetIDAssignMessageText,
            BasisNetworkProfiler.NetIDAssignsMessageText,
            BasisNetworkProfiler.LoadResourceMessageText,
            BasisNetworkProfiler.UnloadResourceMessageText,
            BasisNetworkProfiler.AdminMessageText,
            BasisNetworkProfiler.ContentShareMessageText,
            BasisNetworkProfiler.ContentShareCleanupMessageText,
            BasisNetworkProfiler.ChatMessageText,
            BasisNetworkProfiler.StoreDatabaseMessageText,
            BasisNetworkProfiler.ServerStatisticsMessageText,
            BasisNetworkProfiler.CameraPIPStateMessageText,
            BasisNetworkProfiler.CameraPIPPositionMessageText,
            BasisNetworkProfiler.SpawnPreloadedMessageText,
            BasisNetworkProfiler.EventsMessageText,
        };

        private static readonly List<ProfilerRecorder> Active = new();
        private static int FramesWaited;

        // ── Data shapes ────────────────────────────────────────────────────
        private struct SampleEntry
        {
            public int Index;
            public int Parent;
            public int Depth;
            public long DurationNs;
            public long StartNs;
            public string Name;
        }

        private struct Hotspot
        {
            public string Name;
            public float SelfTimeMs;
            public float TotalTimeMs;
            public int Calls;
            public long GcAllocBytes;
        }

        private struct ThreadCapture
        {
            public int ThreadIndex;
            public string Name;
            public string Group;
            public long FrameTimeNs;
            public int SampleCount;
            public List<SampleEntry> Samples;
            public List<Hotspot> Hotspots;
        }

        private struct FrameTimingResult
        {
            public bool Valid;
            public double CpuFrameMs;
            public double CpuMainThreadMs;
            public double CpuRenderThreadMs;
            public double CpuMainThreadPresentWaitMs;
            public double GpuFrameMs;
        }

        // ── Menu entry ─────────────────────────────────────────────────────
        [MenuItem(MenuPath, true)]
        private static bool ValidateCapture() => EditorApplication.isPlaying;

        [MenuItem(MenuPath)]
        public static void Capture()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[BasisFrameCapture] Enter Play Mode before capturing.");
                return;
            }

            DisposeAll();
            FramesWaited = 0;

            UnityEngine.Profiling.Profiler.enabled = true;
            ProfilerDriver.enabled = true;
            ProfilerDriver.profileEditor = false;

            try { FrameTimingManager.CaptureFrameTimings(); } catch { /* not enabled in player settings */ }

            foreach (var (cat, stat, _) in BuiltInStats)
                Active.Add(ProfilerRecorder.StartNew(cat, stat));
            foreach (var label in BasisNetworkLabels)
                Active.Add(ProfilerRecorder.StartNew(BasisNetworkProfiler.Category, label));

            EditorApplication.update += Tick;
            Debug.Log("[BasisFrameCapture] Recording... will dump in a few frames.");
        }

        private static void Tick()
        {
            FramesWaited++;
            if (FramesWaited < FramesToWait) return;

            EditorApplication.update -= Tick;
            try
            {
                try { FrameTimingManager.CaptureFrameTimings(); } catch { /* ignore */ }
                WriteCapture();
            }
            catch (Exception e)
            {
                Debug.LogError($"[BasisFrameCapture] Capture failed: {e}");
            }
            finally
            {
                DisposeAll();
            }
        }

        private static void WriteCapture()
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CaptureDirName));
            Directory.CreateDirectory(root);

            int firstFrame = ProfilerDriver.firstFrameIndex;
            int lastFrame = ProfilerDriver.lastFrameIndex;
            if (lastFrame < 0 || lastFrame < firstFrame)
            {
                Debug.LogWarning("[BasisFrameCapture] ProfilerDriver has no recorded frames. Open the Profiler window once or ensure Profiler.enabled is true.");
                return;
            }
            // lastFrameIndex can be the frame currently being assembled; back off one.
            int frameIdx = Mathf.Max(firstFrame, lastFrame - 1);

            var threads = ScrapeThreads(frameIdx);
            var ft = ScrapeFrameTimings();

            string jsonPath = Path.Combine(root, $"frame_{ts}.json");
            string mdPath = Path.Combine(root, $"frame_{ts}.md");
            string dataPath = Path.Combine(root, $"frame_{ts}.data");

            File.WriteAllText(jsonPath, BuildJson(frameIdx, threads, ft));
            string md = BuildMarkdown(frameIdx, threads, ft);
            File.WriteAllText(mdPath, md);
            EditorGUIUtility.systemCopyBuffer = md;

            try
            {
                if (File.Exists(dataPath)) File.Delete(dataPath);
                ProfilerDriver.SaveProfile(dataPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BasisFrameCapture] SaveProfile failed: {e.Message}");
            }

            Debug.Log($"[BasisFrameCapture] Frame {frameIdx} captured ({threads.Count} threads).\n  {jsonPath}\n  {mdPath}\n  {dataPath} (open via Profiler window → Load)\nMarkdown digest copied to clipboard.");
        }

        // ── Scraping ───────────────────────────────────────────────────────
        private static int GetThreadCount(int frameIdx)
        {
            using var it = new ProfilerFrameDataIterator();
            return it.GetThreadCount(frameIdx);
        }

        private static List<ThreadCapture> ScrapeThreads(int frameIdx)
        {
            var result = new List<ThreadCapture>();
            int threadCount = GetThreadCount(frameIdx);
            for (int t = 0; t < threadCount; t++)
            {
                ThreadCapture cap;
                using (var raw = ProfilerDriver.GetRawFrameDataView(frameIdx, t))
                {
                    if (raw == null || !raw.valid) continue;
                    cap = new ThreadCapture
                    {
                        ThreadIndex = t,
                        Name = string.IsNullOrEmpty(raw.threadName) ? $"Thread {t}" : raw.threadName,
                        Group = raw.threadGroupName ?? string.Empty,
                        FrameTimeNs = (long)raw.frameTimeNs,
                        SampleCount = raw.sampleCount,
                        Samples = new List<SampleEntry>(Math.Min(raw.sampleCount, 4096)),
                    };
                    if (raw.sampleCount > 0)
                        WalkSamples(raw, cap.Samples);
                }
                bool isMain = cap.Name == "Main Thread";
                cap.Hotspots = ScrapeHotspots(frameIdx, t, isMain ? TopMainThreadHotspots : TopOtherThreadHotspots);
                result.Add(cap);
            }
            return result;
        }

        // Iterative DFS over the sample tree. Children of a sample occupy the next
        // (childCountRecursive + 1) indices, with the immediate first child at idx+1.
        private static void WalkSamples(RawFrameDataView v, List<SampleEntry> outList)
        {
            var pending = new Stack<(int idx, int parent, int depth)>(64);
            pending.Push((0, -1, 0));
            while (pending.Count > 0)
            {
                var (idx, parent, depth) = pending.Pop();
                long dur = (long)v.GetSampleTimeNs(idx);
                if (depth == 0 || dur >= MinSampleDurationNs)
                {
                    outList.Add(new SampleEntry
                    {
                        Index = idx,
                        Parent = parent,
                        Depth = depth,
                        DurationNs = dur,
                        StartNs = (long)v.GetSampleStartTimeNs(idx),
                        Name = v.GetSampleName(idx) ?? string.Empty,
                    });
                }

                int cc = v.GetSampleChildrenCount(idx);
                if (cc <= 0) continue;

                // Compute child start indices, push reversed so they pop in pre-order.
                Span<int> children = cc <= 64 ? stackalloc int[cc] : new int[cc];
                int childIdx = idx + 1;
                for (int c = 0; c < cc; c++)
                {
                    children[c] = childIdx;
                    childIdx += v.GetSampleChildrenCountRecursive(childIdx) + 1;
                }
                for (int c = cc - 1; c >= 0; c--)
                    pending.Push((children[c], idx, depth + 1));
            }
        }

        private static List<Hotspot> ScrapeHotspots(int frameIdx, int threadIdx, int topN)
        {
            var items = new List<Hotspot>(topN);
            using var hfd = ProfilerDriver.GetHierarchyFrameDataView(
                frameIdx, threadIdx,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnSelfTime,
                sortAscending: false);
            if (hfd == null || !hfd.valid) return items;

            int rootId = hfd.GetRootItemID();
            var children = new List<int>();
            hfd.GetItemChildren(rootId, children);

            int take = Math.Min(topN, children.Count);
            for (int i = 0; i < take; i++)
            {
                int id = children[i];
                items.Add(new Hotspot
                {
                    Name = hfd.GetItemName(id) ?? string.Empty,
                    SelfTimeMs = hfd.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnSelfTime),
                    TotalTimeMs = hfd.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnTotalTime),
                    Calls = (int)hfd.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnCalls),
                    GcAllocBytes = (long)hfd.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnGcMemory),
                });
            }
            return items;
        }

        private static FrameTimingResult ScrapeFrameTimings()
        {
            var result = new FrameTimingResult();
            try
            {
                var arr = new FrameTiming[1];
                uint count = FrameTimingManager.GetLatestTimings(1, arr);
                if (count == 0) return result;
                result.Valid = true;
                result.CpuFrameMs = arr[0].cpuFrameTime;
                result.CpuMainThreadMs = arr[0].cpuMainThreadFrameTime;
                result.CpuRenderThreadMs = arr[0].cpuRenderThreadFrameTime;
                result.CpuMainThreadPresentWaitMs = arr[0].cpuMainThreadPresentWaitTime;
                result.GpuFrameMs = arr[0].gpuFrameTime;
            }
            catch { /* FrameTiming stats not enabled in PlayerSettings */ }
            return result;
        }

        // ── JSON output ────────────────────────────────────────────────────
        private static string BuildJson(int frameIdx, List<ThreadCapture> threads, FrameTimingResult ft)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(64 * 1024);
            sb.Append("{\n");
            sb.Append($"  \"timestamp\": \"{DateTime.Now:o}\",\n");
            sb.Append($"  \"unityVersion\": \"{Application.unityVersion}\",\n");
            sb.Append($"  \"platform\": \"{Application.platform}\",\n");
            sb.Append($"  \"frameIndex\": {frameIdx},\n");
            sb.Append($"  \"unityFrameCount\": {Time.frameCount},\n");
            sb.Append($"  \"deltaTime\": {Time.deltaTime.ToString("G9", ci)},\n");
            sb.Append($"  \"unscaledDeltaTime\": {Time.unscaledDeltaTime.ToString("G9", ci)},\n");
            sb.Append($"  \"minSampleDurationNs\": {MinSampleDurationNs},\n");

            // FrameTimingManager
            sb.Append("  \"frameTiming\": ");
            if (ft.Valid)
            {
                sb.Append("{");
                sb.Append("\"cpuFrameMs\":").Append(ft.CpuFrameMs.ToString("G9", ci));
                sb.Append(",\"cpuMainThreadMs\":").Append(ft.CpuMainThreadMs.ToString("G9", ci));
                sb.Append(",\"cpuRenderThreadMs\":").Append(ft.CpuRenderThreadMs.ToString("G9", ci));
                sb.Append(",\"cpuMainThreadPresentWaitMs\":").Append(ft.CpuMainThreadPresentWaitMs.ToString("G9", ci));
                sb.Append(",\"gpuFrameMs\":").Append(ft.GpuFrameMs.ToString("G9", ci));
                sb.Append("},\n");
            }
            else sb.Append("null,\n");

            // Built-in summary counters
            sb.Append("  \"builtIn\": {\n");
            for (int i = 0; i < BuiltInStats.Length; i++)
            {
                long v = Active[i].Valid ? Active[i].LastValue : 0;
                sb.Append("    \"").Append(EscapeJson(BuiltInStats[i].display)).Append("\": ").Append(v);
                sb.Append(i == BuiltInStats.Length - 1 ? "\n" : ",\n");
            }
            sb.Append("  },\n");

            // Basis network counters
            sb.Append("  \"basisNetwork\": {\n");
            int netStart = BuiltInStats.Length;
            for (int i = 0; i < BasisNetworkLabels.Length; i++)
            {
                long v = Active[netStart + i].Valid ? Active[netStart + i].LastValue : 0;
                sb.Append("    \"").Append(EscapeJson(BasisNetworkLabels[i])).Append("\": ").Append(v);
                sb.Append(i == BasisNetworkLabels.Length - 1 ? "\n" : ",\n");
            }
            sb.Append("  },\n");

            // BasisEventDriverProfilerData
            sb.Append("  \"basisEventDriver\": {\n");
            var fields = GetEventDriverFields();
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                object v = f.GetValue(null);
                sb.Append("    \"").Append(f.Name).Append("\": ").Append(JsonValue(v, ci));
                sb.Append(i == fields.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  },\n");

            // Per-thread sample tree
            sb.Append("  \"threads\": [\n");
            for (int ti = 0; ti < threads.Count; ti++)
            {
                var th = threads[ti];
                sb.Append("    {\n");
                sb.Append($"      \"name\": \"{EscapeJson(th.Name)}\",\n");
                sb.Append($"      \"group\": \"{EscapeJson(th.Group)}\",\n");
                sb.Append($"      \"threadIndex\": {th.ThreadIndex},\n");
                sb.Append($"      \"frameTimeNs\": {th.FrameTimeNs},\n");
                sb.Append($"      \"sampleCountTotal\": {th.SampleCount},\n");
                sb.Append($"      \"samplesEmitted\": {th.Samples.Count},\n");

                sb.Append("      \"hotspots\": [\n");
                for (int h = 0; h < th.Hotspots.Count; h++)
                {
                    var hs = th.Hotspots[h];
                    sb.Append("        {");
                    sb.Append($"\"name\":\"{EscapeJson(hs.Name)}\"");
                    sb.Append($",\"selfMs\":{hs.SelfTimeMs.ToString("G9", ci)}");
                    sb.Append($",\"totalMs\":{hs.TotalTimeMs.ToString("G9", ci)}");
                    sb.Append($",\"calls\":{hs.Calls}");
                    sb.Append($",\"gcBytes\":{hs.GcAllocBytes}");
                    sb.Append("}");
                    sb.Append(h == th.Hotspots.Count - 1 ? "\n" : ",\n");
                }
                sb.Append("      ],\n");

                sb.Append("      \"samples\": [\n");
                for (int s = 0; s < th.Samples.Count; s++)
                {
                    var x = th.Samples[s];
                    sb.Append("        {");
                    sb.Append($"\"i\":{x.Index},\"p\":{x.Parent},\"d\":{x.Depth},\"ns\":{x.DurationNs},\"start\":{x.StartNs},\"n\":\"{EscapeJson(x.Name)}\"");
                    sb.Append("}");
                    sb.Append(s == th.Samples.Count - 1 ? "\n" : ",\n");
                }
                sb.Append("      ]\n");
                sb.Append(ti == threads.Count - 1 ? "    }\n" : "    },\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // ── Markdown digest ────────────────────────────────────────────────
        private static string BuildMarkdown(int frameIdx, List<ThreadCapture> threads, FrameTimingResult ft)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(8192);
            float dtMs = Time.deltaTime * 1000f;
            float fps = 1f / Mathf.Max(Time.deltaTime, 1e-6f);

            sb.AppendLine($"# Basis Frame Capture — frame {frameIdx} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"- Unity: {Application.unityVersion}");
            sb.AppendLine($"- Platform: {Application.platform}");
            sb.AppendLine($"- Time.frameCount: {Time.frameCount}");
            sb.AppendLine($"- deltaTime: {dtMs:F2} ms ({fps:F1} FPS)");
            sb.AppendLine($"- Threads recorded: {threads.Count}");
            sb.AppendLine();

            sb.AppendLine("## Frame timing (FrameTimingManager)");
            if (ft.Valid)
            {
                sb.AppendLine("| Metric | ms |");
                sb.AppendLine("|---|---:|");
                sb.AppendLine($"| CPU frame | {ft.CpuFrameMs.ToString("F3", ci)} |");
                sb.AppendLine($"| CPU main thread | {ft.CpuMainThreadMs.ToString("F3", ci)} |");
                sb.AppendLine($"| CPU render thread | {ft.CpuRenderThreadMs.ToString("F3", ci)} |");
                sb.AppendLine($"| CPU main thread present wait | {ft.CpuMainThreadPresentWaitMs.ToString("F3", ci)} |");
                sb.AppendLine($"| GPU frame | {ft.GpuFrameMs.ToString("F3", ci)} |");
            }
            else
            {
                sb.AppendLine("_FrameTimingManager returned no data — enable PlayerSettings → Frame Timing Stats and let it run a few frames._");
            }
            sb.AppendLine();

            sb.AppendLine("## Per-thread totals");
            sb.AppendLine("| Thread | Group | Total ms | Samples (kept / total) |");
            sb.AppendLine("|---|---|---:|---:|");
            foreach (var th in threads.OrderByDescending(x => x.FrameTimeNs))
            {
                sb.AppendLine($"| {th.Name} | {th.Group} | {(th.FrameTimeNs / 1_000_000.0).ToString("F3", ci)} | {th.Samples.Count} / {th.SampleCount} |");
            }
            sb.AppendLine();

            // Hotspots — main thread first, then anything with non-trivial time
            var ordered = threads.OrderBy(t => t.Name == "Main Thread" ? 0 : 1).ThenByDescending(t => t.FrameTimeNs);
            foreach (var th in ordered)
            {
                if (th.Hotspots == null || th.Hotspots.Count == 0) continue;
                if (th.Name != "Main Thread" && th.FrameTimeNs < 100_000) continue; // skip idle threads

                sb.AppendLine($"## Hotspots — {th.Name}  ({(th.FrameTimeNs / 1_000_000.0).ToString("F3", ci)} ms total)");
                sb.AppendLine("| Sample | Self ms | Total ms | Calls | GC alloc (B) |");
                sb.AppendLine("|---|---:|---:|---:|---:|");
                foreach (var h in th.Hotspots)
                {
                    sb.AppendLine($"| {Escape(h.Name)} | {h.SelfTimeMs.ToString("F3", ci)} | {h.TotalTimeMs.ToString("F3", ci)} | {h.Calls} | {h.GcAllocBytes} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Built-in counters");
            sb.AppendLine("| Stat | Value |");
            sb.AppendLine("|---|---:|");
            for (int i = 0; i < BuiltInStats.Length; i++)
            {
                long v = Active[i].Valid ? Active[i].LastValue : 0;
                sb.AppendLine($"| {BuiltInStats[i].display} | {FormatBuiltIn(BuiltInStats[i].display, v)} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Basis network bytes (per-message, last frame)");
            sb.AppendLine("| Message | Bytes |");
            sb.AppendLine("|---|---:|");
            int netStart = BuiltInStats.Length;
            long netTotal = 0;
            for (int i = 0; i < BasisNetworkLabels.Length; i++)
            {
                long v = Active[netStart + i].Valid ? Active[netStart + i].LastValue : 0;
                if (v == 0) continue;
                netTotal += v;
                sb.AppendLine($"| {BasisNetworkLabels[i]} | {v} |");
            }
            sb.AppendLine($"| **Total non-zero** | **{netTotal}** |");
            sb.AppendLine();

            sb.AppendLine("## BasisEventDriver subsystem timings");
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|---|---:|");
            foreach (var f in GetEventDriverFields())
            {
                object v = f.GetValue(null);
                sb.AppendLine($"| {f.Name} | {FormatField(v)} |");
            }
            sb.AppendLine();

            sb.AppendLine("> Full hierarchical sample tree (every thread, every marker ≥ "
                + (MinSampleDurationNs / 1000.0).ToString("F1", ci) + " µs) is in the JSON sibling file. The .data file is loadable in the Profiler window.");

            return sb.ToString();
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static List<FieldInfo> GetEventDriverFields() =>
            typeof(BasisEventDriverProfilerData)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && f.FieldType.IsValueType)
                .OrderBy(f => f.Name)
                .ToList();

        private static string FormatBuiltIn(string display, long v)
        {
            if (display.EndsWith("(ns)")) return (v / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture) + " ms";
            if (display.EndsWith("(bytes)"))
            {
                if (v >= 1L << 30) return (v / (double)(1L << 30)).ToString("F2", CultureInfo.InvariantCulture) + " GiB";
                if (v >= 1L << 20) return (v / (double)(1L << 20)).ToString("F2", CultureInfo.InvariantCulture) + " MiB";
                if (v >= 1L << 10) return (v / (double)(1L << 10)).ToString("F2", CultureInfo.InvariantCulture) + " KiB";
                return v + " B";
            }
            return v.ToString();
        }

        private static string FormatField(object v) => v switch
        {
            double d => d.ToString("F4", CultureInfo.InvariantCulture),
            float f  => f.ToString("F4", CultureInfo.InvariantCulture),
            bool b   => b ? "true" : "false",
            _        => v?.ToString() ?? "null",
        };

        private static string JsonValue(object v, CultureInfo ci) => v switch
        {
            null     => "null",
            bool b   => b ? "true" : "false",
            double d => double.IsFinite(d) ? d.ToString("G17", ci) : "null",
            float f  => float.IsFinite(f) ? f.ToString("G9", ci) : "null",
            _        => v.ToString(),
        };

        private static string EscapeJson(string s) =>
            s == null ? string.Empty :
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        private static string Escape(string s) =>
            s == null ? string.Empty : s.Replace("|", "\\|");

        private static void DisposeAll()
        {
            foreach (var r in Active) if (r.Valid) r.Dispose();
            Active.Clear();
        }
    }
}
#endif
