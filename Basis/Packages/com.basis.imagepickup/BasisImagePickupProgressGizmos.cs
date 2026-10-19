using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Text;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Billboarded transfer readout under a pickup card: how much of the image or animation has moved and
    /// how fast it is moving. Fed by <see cref="BasisImagePickupManager"/> once per tick for every transfer
    /// still in flight, and torn down the moment one completes, so a card only carries a label while it has
    /// something to report.
    ///
    /// These are created outside the debug-gizmo toggles — <c>BasisGizmoManager.Render</c> runs every frame
    /// regardless — so a player watching an image arrive sees the progress without turning anything on. The
    /// toggle going off still destroys every gizmo, hence the master hook.
    /// </summary>
    [AutoStaticsCleanup]
    internal static partial class BasisImagePickupProgressGizmos
    {
        private const float LabelBaseScale = 0.02f;
        private static readonly Color InboundColor = new Color(0.55f, 0.80f, 1f, 1f);
        private static readonly Color OutboundColor = new Color(0.35f, 0.70f, 1f, 1f);

        private struct ProgressLabel
        {
            public int Label;
            public int TextKey;
            public string Text;
        }

        private static readonly Dictionary<Guid, ProgressLabel> _labels = new();
        private static readonly HashSet<Guid> _seen = new();
        private static readonly List<Guid> _stale = new();
        private static readonly StringBuilder _text = new(32);
        private static Vector3 _cameraPosition;
        private static float _scale = 1f;
        private static bool _hooked;

        internal static int ActiveCount => _labels.Count;

        internal static void BeginFrame()
        {
            EnsureMasterHook();
            _seen.Clear();
            _cameraPosition = BasisLocalCameraDriver.Position;
            float scale = BasisHeightDriver.ScaledToMatchValue;
            _scale = scale > 0f ? scale : 1f;
        }

        /// <summary>
        /// Raises or refreshes the label for one in-flight transfer. <paramref name="progress"/> is a
        /// fraction of the whole and <paramref name="bytesPerSecond"/> is the smoothed throughput.
        /// </summary>
        internal static void Report(Guid id, Vector3 anchor, float progress, float bytesPerSecond, bool outbound)
        {
            _seen.Add(id);
            _labels.TryGetValue(id, out ProgressLabel entry);

            int percent = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f), 0, 100);
            Color color = outbound ? OutboundColor : InboundColor;

            if (entry.Label <= 0 || BasisGizmoManager.IsTextVisible(entry.Label))
            {
                int key = TextKey(percent, bytesPerSecond, outbound);
                if (entry.Label <= 0 || key != entry.TextKey || entry.Text == null)
                {
                    entry.TextKey = key;
                    entry.Text = BuildText(percent, bytesPerSecond, outbound);
                }
            }

            Quaternion rotation = BasisGizmoManager.BillboardRotation(anchor, _cameraPosition);
            if (entry.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo(
                    $"ImagePickup_Progress_{id:N}",
                    out entry.Label,
                    anchor,
                    entry.Text,
                    color
                );
            }
            BasisGizmoManager.UpdateTextGizmo(
                entry.Label,
                anchor,
                rotation,
                LabelBaseScale * _scale,
                entry.Text,
                color
            );

            _labels[id] = entry;
        }

        internal static void EndFrame()
        {
            if (_labels.Count == _seen.Count)
                return;

            _stale.Clear();
            foreach (KeyValuePair<Guid, ProgressLabel> entry in _labels)
            {
                if (!_seen.Contains(entry.Key))
                    _stale.Add(entry.Key);
            }
            int staleCount = _stale.Count;
            for (int i = 0; i < staleCount; i++)
                Remove(_stale[i]);
        }

        internal static void Remove(Guid id)
        {
            if (!_labels.TryGetValue(id, out ProgressLabel entry))
                return;
            if (entry.Label > 0)
                BasisGizmoManager.DestroyGizmo(entry.Label);
            _labels.Remove(id);
        }

        internal static void Shutdown()
        {
            if (_labels.Count == 0)
                return;
            foreach (KeyValuePair<Guid, ProgressLabel> entry in _labels)
            {
                if (entry.Value.Label > 0)
                    BasisGizmoManager.DestroyGizmo(entry.Value.Label);
            }
            _labels.Clear();
            _seen.Clear();
        }

        internal static int TextKey(int percent, float bytesPerSecond, bool outbound)
        {
            int rateBucket = Mathf.RoundToInt(Mathf.Max(0f, bytesPerSecond) / 512f);
            return (percent * 397) ^ (rateBucket << 8) ^ (outbound ? 1 << 30 : 0);
        }

        internal static string BuildText(int percent, float bytesPerSecond, bool outbound)
        {
            _text.Clear();
            if (outbound)
                _text.Append("tx ");
            _text.Append(percent).Append("%  ").Append(FormatRate(bytesPerSecond));
            return _text.ToString();
        }

        internal static string FormatRate(float bytesPerSecond)
        {
            if (bytesPerSecond < 0f)
                bytesPerSecond = 0f;
            if (bytesPerSecond >= 1024f * 1024f)
                return (bytesPerSecond / (1024f * 1024f)).ToString("0.0") + " MB/s";
            if (bytesPerSecond >= 1024f)
                return (bytesPerSecond / 1024f).ToString("0.0") + " KB/s";
            return Mathf.RoundToInt(bytesPerSecond) + " B/s";
        }

        private static void EnsureMasterHook()
        {
            if (_hooked)
                return;
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            _hooked = true;
        }

        private static void OnMasterToggleChanged(bool state)
        {
            if (!state)
                _labels.Clear();
        }
    }
}
