using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Settings;

namespace Basis.Streaming
{
    /// <summary>
    /// Persisted settings for the streaming meta listener, relocated here from the framework's
    /// BasisSettingsDefaults. The binding keys are unchanged so existing saved values
    /// continue to load.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class BasisStreamingMetaSettings
    {
        static BasisStreamingMetaSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisStreamingMetaSettings));
        }

        /// <summary>
        /// TCP port used by the streaming meta listener. Stored as a string so
        /// it can be edited in a single-line numeric text field. Consumers
        /// should parse it and fall back to 9080 on invalid/out-of-range input.
        /// Declared before <see cref="EnableStreamingMeta"/> so the post-load reload
        /// applies a saved port before a saved "on" starts the listener.
        /// </summary>
        public static BasisSettingsBinding<string> StreamingMetaPort = new("streamingmetaport", new BasisPlatformDefault<string>("9080"));

        /// <summary>
        /// When on, the client runs a loopback-only HTTP listener on
        /// 127.0.0.1:<see cref="StreamingMetaPort"/> exposing /stats.json and
        /// /overlay.html so OBS Browser Source (or any local tool) can pull
        /// FPS / CCU / ping. Off by default — the listener is only opened
        /// after the user explicitly enables this.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableStreamingMeta = new("enablestreamingmeta", new BasisPlatformDefault<bool>(false));

        public static void EnsureLoaded()
        {
            _ = StreamingMetaPort;
            _ = EnableStreamingMeta;
        }
    }
}
