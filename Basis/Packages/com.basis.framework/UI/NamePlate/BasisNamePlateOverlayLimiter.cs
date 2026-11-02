using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Bounds the per-frame cost of the TMP-based nameplate overlays — chat bubbles and
    /// avatar-loading displays — regardless of player count: only the
    /// <see cref="MaxVisibleChatBubbles"/> / <see cref="MaxVisibleLoadingDisplays"/> nearest
    /// to the viewer actually render; the rest keep their state but their objects are
    /// deactivated and skip every text/mesh write. Fed by the driver's existing per-plate
    /// loop in <see cref="BasisRemoteNamePlateDriver.CompleteNamePlates"/> (BeginFrame →
    /// Consider per plate → Apply), so plates never register themselves and there is no
    /// stale bookkeeping. Selection math lives in <see cref="BasisNamePlateOverlayCore"/>.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisNamePlateOverlayLimiter
    {
        /// <summary>Chat bubbles rendered at once — the nearest win.</summary>
        public static int MaxVisibleChatBubbles = 24;

        /// <summary>
        /// Avatar-loading texts + bars rendered at once — the nearest win. Generous on
        /// purpose: the bucket quantization already bounds the TMP rebuild cost, so this
        /// only limits draw calls, and a mass join legitimately has dozens of players
        /// loading at once — capping tighter reads as "the loading bar is broken".
        /// </summary>
        public static int MaxVisibleLoadingDisplays = 64;

        /// <summary>
        /// Seconds a created-but-empty chat display may idle before its TMP + bubble objects
        /// are destroyed (they lazily re-create on the next message). Bounds the old
        /// "every player who ever chatted keeps a TMP forever" accumulation.
        /// </summary>
        public static double ChatDisplayIdleReleaseSeconds = 30.0;

        /// <summary>Loading-label quantization step — the text only rebuilds when the
        /// progress crosses into a new bucket (the bar still moves every report).</summary>
        public static float LoadingTextStepPercent = 5f;

        private static readonly List<BasisRemoteNamePlate> _chat = new List<BasisRemoteNamePlate>(64);
        private static readonly List<BasisRemoteNamePlate> _loading = new List<BasisRemoteNamePlate>(64);
        private static readonly List<float> _distances = new List<float>(64);
        private static readonly List<int> _indexScratch = new List<int>(64);
        private static readonly List<bool> _visible = new List<bool>(64);
        private static readonly HashSet<int> _trackedKeys = new HashSet<int>();
        private static readonly HashSet<int> _trackedScratch = new HashSet<int>();

        internal static void BeginFrame()
        {
            _chat.Clear();
            _loading.Clear();
        }

        internal static void Consider(BasisRemoteNamePlate plate)
        {
            if (plate.HasActiveChatOverlay)
            {
                _chat.Add(plate);
            }
            if (plate.HasActiveLoadingOverlay)
            {
                _loading.Add(plate);
            }
        }

        internal static void Apply(Vector3 viewerPosition)
        {
            int chatCount = _chat.Count;
            if (chatCount > 0)
            {
                RankDistances(_chat, viewerPosition);
                BasisNamePlateOverlayCore.SelectNearest(_distances, MaxVisibleChatBubbles, _indexScratch, _visible);
                for (int i = 0; i < chatCount; i++)
                {
                    _chat[i].SetChatOverlayCulled(!_visible[i]);
                    if (_visible[i]) TrackPlate(_chat[i]);
                }
            }

            int loadingCount = _loading.Count;
            if (loadingCount > 0)
            {
                RankDistances(_loading, viewerPosition);
                BasisNamePlateOverlayCore.SelectNearest(_distances, MaxVisibleLoadingDisplays, _indexScratch, _visible);
                for (int i = 0; i < loadingCount; i++)
                {
                    _loading[i].SetLoadingOverlayCulled(!_visible[i]);
                    if (_visible[i]) TrackPlate(_loading[i]);
                }
            }
            foreach (int key in _trackedKeys)
            {
                if (!_trackedScratch.Contains(key)) RemoteBoneJobSystem.SetNamePlateTracked(key, false);
            }
            _trackedKeys.Clear();
            _trackedKeys.UnionWith(_trackedScratch);
            _trackedScratch.Clear();
        }

        private static void TrackPlate(BasisRemoteNamePlate plate)
        {
            var receiver = plate.BasisRemotePlayer != null ? plate.BasisRemotePlayer.NetworkReceiver : null;
            if (receiver == null) return;
            int key = receiver.playerId;
            if (_trackedScratch.Add(key) && !_trackedKeys.Contains(key)) RemoteBoneJobSystem.SetNamePlateTracked(key, true);
        }

        private static void RankDistances(List<BasisRemoteNamePlate> plates, Vector3 viewerPosition)
        {
            _distances.Clear();
            int count = plates.Count;
            for (int i = 0; i < count; i++)
            {
                Transform self = plates[i].Self;
                _distances.Add(self != null ? (self.position - viewerPosition).sqrMagnitude : float.MaxValue);
            }
        }
    }
}
