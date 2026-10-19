using Unity.Scripting.LifecycleManagement;
#if UNITY_EDITOR
using System;
using Unity.Profiling;
using Unity.Profiling.Editor;

namespace Basis.Scripts.Profiler.EditorTools
{
    [Serializable]
    [ProfilerModuleMetadata("Basis Direct Connection")]
    [NoAutoStaticsCleanup]
    public class BasisDirectConnectionProfilerModule : ProfilerModule
    {
        private static readonly ProfilerCounterDescriptor[] k_Counters = new[]
        {
            new ProfilerCounterDescriptor(BasisNetworkProfiler.OutboundAvatarServerText, ProfilerCategory.Network),
            new ProfilerCounterDescriptor(BasisNetworkProfiler.OutboundAvatarP2PText, ProfilerCategory.Network),
            new ProfilerCounterDescriptor(BasisNetworkProfiler.InboundAvatarP2PText, ProfilerCategory.Network),
            new ProfilerCounterDescriptor(BasisNetworkProfiler.LocalAvatarSyncMessageText, ProfilerCategory.Network),
            new ProfilerCounterDescriptor(BasisNetworkProfiler.P2PConnectedSessionsText, ProfilerCategory.Network),
        };

        public BasisDirectConnectionProfilerModule() : base(k_Counters) { }
    }
}
#endif
