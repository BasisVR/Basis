using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// The runtime's play-area outline (SteamVR's chaperone, Quest's guardian) in raw tracking space,
    /// polled from the active <see cref="XRInputSubsystem"/> and cached.
    /// <para>
    /// Not every runtime hands one over: it needs a floor-relative tracking origin and a boundary the
    /// user has actually drawn, and a seated-origin or roomless setup legitimately reports none. Callers
    /// check <see cref="HasBoundary"/> and say so rather than inventing a rectangle.
    /// </para>
    /// </summary>
    public static class BasisPlayspaceBoundary
    {
        private static readonly List<XRInputSubsystem> _subsystems = new List<XRInputSubsystem>();
        private static readonly List<Vector3> _scratch = new List<Vector3>();
        private static readonly List<Vector3> _points = new List<Vector3>();

        private static float _lastPollTime = float.NegativeInfinity;

        /// <summary>Play-area corners in tracking space, floor level. Empty when the runtime has none.</summary>
        public static IReadOnlyList<Vector3> Points => _points;

        /// <summary>Tracking origin the poses (and the outline) are relative to, Unknown when unqueried.</summary>
        public static TrackingOriginModeFlags OriginMode { get; private set; } = TrackingOriginModeFlags.Unknown;

        /// <summary>
        /// Extents of the cached outline, recomputed only when the outline itself is refreshed rather
        /// than by every consumer every frame. Invalid while there is no boundary.
        /// </summary>
        public static BasisPlayspaceBoundsMetrics Metrics { get; private set; }

        /// <summary>
        /// Refreshes the cached outline at most every <paramref name="refreshInterval"/> seconds. The
        /// boundary only changes when the user redraws it or the runtime recentres, so polling it every
        /// frame would pay a native round trip for a shape that is almost always identical.
        /// </summary>
        public static void Poll(float refreshInterval)
        {
            float now = Time.unscaledTime;
            if (now - _lastPollTime < refreshInterval)
            {
                return;
            }
            _lastPollTime = now;

            if (XRSettings.isDeviceActive == false)
            {
                Clear();
                return;
            }

            SubsystemManager.GetSubsystems(_subsystems);
            for (int Index = 0; Index < _subsystems.Count; Index++)
            {
                XRInputSubsystem subsystem = _subsystems[Index];
                if (subsystem == null || subsystem.running == false)
                {
                    continue;
                }

                OriginMode = subsystem.GetTrackingOriginMode();

                _scratch.Clear();
                if (subsystem.TryGetBoundaryPoints(_scratch) == false || _scratch.Count < 3)
                {
                    continue;
                }

                _points.Clear();
                _points.AddRange(_scratch);
                BasisPlayspaceGizmoCore.TryComputeBounds(_points, out BasisPlayspaceBoundsMetrics metrics);
                Metrics = metrics;
                return;
            }

            _points.Clear();
            Metrics = default;
        }

        public static void Clear()
        {
            _points.Clear();
            Metrics = default;
            OriginMode = TrackingOriginModeFlags.Unknown;
        }
    }
}
