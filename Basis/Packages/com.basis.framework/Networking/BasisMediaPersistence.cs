using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Settings;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// The local player's choice to leave their shared images and video/audio clips behind when they
    /// leave. On, the server hands them to a remaining player and keeps them until the instance empties.
    /// Off keeps the normal behaviour: they go with you, unless the server's admin toggle keeps them anyway.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class BasisMediaPersistence
    {
        public static BasisSettingsBinding<bool> Enabled =
            new("mediapersistence", new BasisPlatformDefault<bool>(false));
    }
}
