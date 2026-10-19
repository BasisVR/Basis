using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using HVR.Vixxy;
using Unity.Profiling;

namespace HVR.Basis.Comms
{
    /// <summary>
    /// Batches the per-frame work of the comms components into a single update pumped by
    /// BasisEventDriver, replacing one MonoBehaviour.Update() invocation per instance.
    /// Components register on enable and unregister on disable.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class HVRCommsUpdateDriver
    {
        private static readonly TickList<HVRVixxyOrchestrator> Orchestrators = new();
        private static readonly TickList<FaceTrackingActivityRelay> ActivityRelays = new();
        private static readonly TickList<HVRVariableNetworking> VariableNetworkers = new();
        private static readonly TickList<EyeTrackingBoneActuation> EyeActuations = new();

        private static readonly ProfilerMarker VixxyMarker = new ProfilerMarker("HVRComms.VixxyOrchestrator");
        private static readonly ProfilerMarker ActivityMarker = new ProfilerMarker("HVRComms.FaceTrackingActivity");
        private static readonly ProfilerMarker VariableNetworkingMarker = new ProfilerMarker("HVRComms.VariableNetworking");
        private static readonly ProfilerMarker EyeActuationMarker = new ProfilerMarker("HVRComms.EyeTrackingBoneActuation");

        internal static void Register(HVRVixxyOrchestrator instance) => Orchestrators.Add(instance);
        internal static void Unregister(HVRVixxyOrchestrator instance) => Orchestrators.Remove(instance);

        internal static void Register(EyeTrackingBoneActuation instance) => EyeActuations.Add(instance);
        internal static void Unregister(EyeTrackingBoneActuation instance) => EyeActuations.Remove(instance);

        internal static void Register(FaceTrackingActivityRelay instance) => ActivityRelays.Add(instance);
        internal static void Unregister(FaceTrackingActivityRelay instance) => ActivityRelays.Remove(instance);

        internal static void Register(HVRVariableNetworking instance) => VariableNetworkers.Add(instance);
        internal static void Unregister(HVRVariableNetworking instance) => VariableNetworkers.Remove(instance);

        /// <summary>
        /// Runs the full batch in its original order. Equivalent to
        /// <see cref="SimulateActuators"/> followed by <see cref="SimulateVariableNetworking"/>.
        /// The pieces are also exposed individually so the caller can slot each one in front of a
        /// different JobHandle.Complete() barrier, hiding the main-thread cost behind in-flight jobs.
        /// </summary>
        public static void SimulateAll()
        {
            SimulateActuators();
            SimulateVariableNetworking();
        }

        /// <summary>
        /// Eye actuation, Vixxy orchestration and face-tracking activity, in that order.
        /// Vixxy actuates blendshapes/materials here, so this must run before BasisBlendShapeDriver
        /// and before the avatar renders.
        /// </summary>
        public static void SimulateActuators()
        {
            SimulateEyeActuations();
            SimulateOrchestrators();
            SimulateActivityRelays();
            HVRVixxyPersistentStore.Tick();
        }

        public static void SimulateEyeActuations()
        {
            using (EyeActuationMarker.Auto())
            {
                var items = EyeActuations.Snapshot(out var count);
                for (var i = 0; i < count; i++)
                {
                    try { items[i].SimulateTick(); }
                    catch (Exception ex) { BasisDebug.LogError($"EyeTrackingBoneActuation update failed: {ex}"); }
                }
            }
        }

        public static void SimulateOrchestrators()
        {
            using (VixxyMarker.Auto())
            {
                var items = Orchestrators.Snapshot(out var count);
                for (var i = 0; i < count; i++)
                {
                    try { items[i].SimulateTick(); }
                    catch (Exception ex) { BasisDebug.LogError($"HVRVixxyOrchestrator update failed: {ex}"); }
                }
            }
        }

        public static void SimulateActivityRelays()
        {
            using (ActivityMarker.Auto())
            {
                var items = ActivityRelays.Snapshot(out var count);
                for (var i = 0; i < count; i++)
                {
                    try { items[i].SimulateTick(); }
                    catch (Exception ex) { BasisDebug.LogError($"FaceTrackingActivityRelay update failed: {ex}"); }
                }
            }
        }

        /// <summary>
        /// Advances the remote variable interpolation tapes and runs wearer variable sends.
        /// Produces no avatar-visible write this frame (it only Submits values that dirty Vixxy for
        /// the next tick), so it can be pumped in front of any later barrier in the frame.
        /// </summary>
        public static void SimulateVariableNetworking()
        {
            using (VariableNetworkingMarker.Auto())
            {
                var deltaTime = UnityEngine.Time.deltaTime;
                var items = VariableNetworkers.Snapshot(out var count);
                for (var i = 0; i < count; i++)
                {
                    try { items[i].SimulateTick(deltaTime); }
                    catch (Exception ex) { BasisDebug.LogError($"HVRVariableNetworking update failed: {ex}"); }
                }
            }
        }

        /// <summary>
        /// Registration list with a cached T[] view so tick loops avoid List get_Item/get_Count
        /// per element and stay safe against register/unregister during a tick. Alloc-free at steady state.
        /// </summary>
        private sealed class TickList<T>
        {
            private readonly List<T> _items = new();
            private T[] _array = Array.Empty<T>();
            private int _count;
            private bool _dirty;

            public void Add(T item)
            {
                _items.Add(item);
                _dirty = true;
            }

            public void Remove(T item)
            {
                if (_items.Remove(item)) _dirty = true;
            }

            public T[] Snapshot(out int count)
            {
                if (_dirty)
                {
                    int needed = _items.Count;
                    if (_array.Length < needed) _array = new T[Math.Max(needed, _array.Length * 2)];
                    _items.CopyTo(_array);
                    for (int i = needed; i < _count; i++) _array[i] = default;
                    _count = needed;
                    _dirty = false;
                }
                count = _count;
                return _array;
            }
        }
    }
}
