#if UNITY_EDITOR
using System;
using System.IO;
using Basis.Scripts.Drivers;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Profiler.EditorTools
{
    /// <summary>
    /// Multi-frame capture of BasisRenderProfileHistory's per-pass GPU/CPU breakdown. Unlike
    /// BasisFrameCapture (one frame, every CPU sample), this spans a rolling window of rendered
    /// frames and reports statistics per named pass — the shape a rendering optimization pass wants.
    /// Writes:
    ///   ProfilerCaptures/renderpasses_&lt;ts&gt;.json — machine-readable, schemaVersion 1
    ///   ProfilerCaptures/renderpasses_&lt;ts&gt;.md   — human-readable digest (clipboard too)
    /// </summary>
    public static class BasisRenderPassCapture
    {
        private const string MenuPath = "Basis/Debug/Profiler/Capture Render Passes (300 frames)";
        private const string CaptureDirName = "ProfilerCaptures";

        [MenuItem(MenuPath, true)]
        private static bool Validate() => EditorApplication.isPlaying && !BasisRenderProfileHistory.IsCapturing;

        [MenuItem(MenuPath)]
        public static void Capture()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[BasisRenderPassCapture] Enter Play Mode before capturing.");
                return;
            }

            bool started = BasisRenderProfileHistory.StartCapture(
                "editor-menu", BasisRenderProfileHistory.DefaultFrames, OnCaptureComplete);
            if (started)
            {
                Debug.Log($"[BasisRenderPassCapture] Recording {BasisRenderProfileHistory.DefaultFrames} frames... will dump when done.");
            }
        }

        private static void OnCaptureComplete(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("[BasisRenderPassCapture] A capture was already running; this request was ignored.");
                return;
            }

            string md = BasisRenderProfileHistory.BuildMarkdown();
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CaptureDirName));
            Directory.CreateDirectory(root);

            string jsonPath = Path.Combine(root, $"renderpasses_{ts}.json");
            string mdPath = Path.Combine(root, $"renderpasses_{ts}.md");

            File.WriteAllText(jsonPath, json);
            File.WriteAllText(mdPath, md);
            EditorGUIUtility.systemCopyBuffer = md;

            Debug.Log($"[BasisRenderPassCapture] Captured.\n  {jsonPath}\n  {mdPath}\nMarkdown digest copied to clipboard.");
        }
    }
}
#endif
