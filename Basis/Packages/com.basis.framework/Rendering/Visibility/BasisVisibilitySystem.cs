using System.Collections.Generic;
using System.Diagnostics;
using Basis.Scripts.Drivers;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Rendering
{
    public static class BasisVisibilitySystem
    {
        public const int MaxCameras = 16;

        /// <summary>
        /// Read-only on purpose: turning the system off has to run <see cref="SetEnabled"/> so
        /// <c>RestoreAll</c> clears every <c>forceRenderingOff</c> it set. A plain assignment would
        /// leave culled avatars invisible forever.
        /// </summary>
        public static bool Enabled { get; private set; }

        public static float FrustumMargin = 0.5f;

        /// <summary>Extra margin granted to entries that are already on screen. Pure hysteresis.</summary>
        public static float StickyMargin = 1.5f;

        public static int MaxApplyPerTick = 32;
        public static float MaxApplyMillisecondsPerTick = 1f;

        private static NativeArray<BasisVisibilityCamera> _cameras;
        private static NativeList<int> _changed;

        private static readonly List<Camera> _cameraScratch = new List<Camera>(8);
        private static readonly Plane[] _planeScratch = new Plane[6];
        private static readonly Stopwatch _stopwatch = new Stopwatch();

        private static int _cameraCount;
        private static JobHandle _handle;
        private static bool _scheduled;

        public static void EnsureCreated()
        {
            BasisVisibilityDatabase.EnsureCreated();
            if (!_cameras.IsCreated)
            {
                _cameras = new NativeArray<BasisVisibilityCamera>(MaxCameras, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            if (!_changed.IsCreated)
            {
                _changed = new NativeList<int>(64, Allocator.Persistent);
            }
        }

        public static void Dispose()
        {
            if (_scheduled)
            {
                _handle.Complete();
                _scheduled = false;
            }
            if (_cameras.IsCreated) _cameras.Dispose();
            if (_changed.IsCreated) _changed.Dispose();
            _cameraCount = 0;
            _cameraScratch.Clear();
            BasisVisibilityDatabase.Dispose();
        }

        /// <summary>
        /// Clears anything a disabled domain reload left behind. Mandatory: with reload disabled the
        /// statics survive exiting play mode, so the persistent arrays would both leak and be handed
        /// to the next session. <see cref="Enabled"/> is assigned directly rather than through
        /// <see cref="SetEnabled"/> because the database has just been torn down with it — there are
        /// no renderers left to restore.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Dispose();
            Enabled = false;
            _handle = default;
        }

        public static void CollectCameras()
        {
            using var _scope = BasisVisibilityMarkers.CollectCameras.Auto();
            _cameraCount = 0;
            if (!_cameras.IsCreated)
            {
                return;
            }

            _cameraScratch.Clear();
            Camera main = BasisLocalCameraDriver.CameraInstance;
            if (main != null)
            {
                _cameraScratch.Add(main);
            }
            BasisCullingCameraRegistry.CollectInto(_cameraScratch);

            int count = _cameraScratch.Count;
            for (int index = 0; index < count && _cameraCount < MaxCameras; index++)
            {
                Camera camera = _cameraScratch[index];
                if (camera == null)
                {
                    continue;
                }

                GeometryUtility.CalculateFrustumPlanes(camera, _planeScratch);
                _cameras[_cameraCount] = new BasisVisibilityCamera
                {
                    Plane0 = ToFloat4(_planeScratch[0]),
                    Plane1 = ToFloat4(_planeScratch[1]),
                    Plane2 = ToFloat4(_planeScratch[2]),
                    Plane3 = ToFloat4(_planeScratch[3]),
                    Plane4 = ToFloat4(_planeScratch[4]),
                    Plane5 = ToFloat4(_planeScratch[5]),
                    Position = camera.transform.position,
                };
                _cameraCount++;
            }
        }

        public static JobHandle Schedule(JobHandle dependsOn)
        {
            if (!Enabled)
            {
                return dependsOn;
            }

            EnsureCreated();

            int count = BasisVisibilityDatabase.Count;
            if (count <= 0)
            {
                return dependsOn;
            }

            if (BasisVisibilityDatabase.CullableCount == 0 && BasisVisibilityDatabase.CulledCount == 0)
            {
                return dependsOn;
            }

            CollectCameras();

            using var _dispatchScope = BasisVisibilityMarkers.Dispatch.Auto();

            JobHandle boundsHandle = dependsOn;
            if (BasisVisibilityDatabase.DynamicCount > 0)
            {
                var boundsJob = new BasisVisibilityBoundsJob
                {
                    DenseToSlot = BasisVisibilityDatabase.DenseToSlot.AsArray(),
                    BaseExtents = BasisVisibilityDatabase.BaseExtents,
                    Centers = BasisVisibilityDatabase.Centers,
                    Extents = BasisVisibilityDatabase.Extents,
                };
                boundsHandle = boundsJob.Schedule(BasisVisibilityDatabase.Roots, dependsOn);
            }

            var frustumJob = new BasisVisibilityFrustumJob
            {
                Centers = BasisVisibilityDatabase.Centers,
                Extents = BasisVisibilityDatabase.Extents,
                Flags = BasisVisibilityDatabase.Flags,
                Cameras = _cameras,
                AppliedVisible = BasisVisibilityDatabase.AppliedVisible,
                CameraCount = _cameraCount,
                Margin = FrustumMargin,
                StickyMargin = StickyMargin,
                VisibleMask = BasisVisibilityDatabase.VisibleMask,
            };
            JobHandle frustumHandle = frustumJob.Schedule(count, 64, boundsHandle);

            var changeJob = new BasisVisibilityChangeJob
            {
                VisibleMask = BasisVisibilityDatabase.VisibleMask,
                AppliedVisible = BasisVisibilityDatabase.AppliedVisible,
                Flags = BasisVisibilityDatabase.Flags,
                Count = count,
                Changed = _changed,
            };
            _handle = changeJob.Schedule(frustumHandle);
            _scheduled = true;
            return _handle;
        }

        public static void CompleteAndApply()
        {
            if (!_scheduled)
            {
                return;
            }

            using (BasisVisibilityMarkers.Join.Auto())
            {
                _handle.Complete();
            }
            _scheduled = false;

            if (!_changed.IsCreated)
            {
                return;
            }

            int changeCount = _changed.Length;
            if (changeCount == 0)
            {
                return;
            }

            using var _applyScope = BasisVisibilityMarkers.Apply.Auto();
            _stopwatch.Restart();
            int applied = 0;

            for (int index = 0; index < changeCount; index++)
            {
                if (applied >= MaxApplyPerTick)
                {
                    break;
                }
                if (applied > 0 && _stopwatch.Elapsed.TotalMilliseconds >= MaxApplyMillisecondsPerTick)
                {
                    break;
                }

                int slot = _changed[index];
                if (!BasisVisibilityDatabase.IsValid(slot))
                {
                    continue;
                }

                bool visible = BasisVisibilityDatabase.VisibleMask[slot] != 0u;
                ApplyToRenderers(BasisVisibilityDatabase.Bindings[slot], visible);
                BasisVisibilityDatabase.SetApplied(slot, visible);
                applied++;
            }

            _stopwatch.Stop();
        }

        /// <summary>
        /// Joins an in-flight cull without consuming its result. Every database mutation calls this:
        /// between the schedule and the tail join the job holds those arrays, and an avatar load or
        /// leave landing in that window would otherwise race it. The apply at the tail still runs.
        /// </summary>
        public static void CompleteIfScheduled()
        {
            if (_scheduled)
            {
                _handle.Complete();
            }
        }

        public static void ApplyFromSettings()
        {
            SetEnabled(Basis.BasisUI.BasisSettingsDefaults.UseAvatarVisibilityCull.RawValue);
        }

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }
            Enabled = enabled;
            if (enabled)
            {
                EnsureCreated();
            }
            else
            {
                if (_scheduled)
                {
                    _handle.Complete();
                    _scheduled = false;
                }
                BasisVisibilityDatabase.RestoreAll();
            }
        }

        private static void ApplyToRenderers(BasisVisibilityBinding binding, bool visible)
        {
            Renderer[] renderers = binding?.Renderers;
            if (renderers == null)
            {
                return;
            }

            bool off = !visible;
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer != null && renderer.forceRenderingOff != off)
                {
                    renderer.forceRenderingOff = off;
                }
            }
        }

        private static Unity.Mathematics.float4 ToFloat4(Plane plane)
        {
            return new Unity.Mathematics.float4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
        }
    }
}
