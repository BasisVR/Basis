using Unity.Scripting.LifecycleManagement;
using System;
using System.Threading.Tasks;

namespace Basis.Scripts.BasisSdk.Players
{
    public interface IBasisLocalPlayer
    {
        Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle);
    }

    // SDK-side local player data. Framework's BasisLocalPlayer writes Instance
    // when present; otherwise the SDK editor preview writes a stand-in (gated
    // by BASIS_FRAMEWORK_EXISTS so only one writer ever runs).
    [AutoStaticsCleanup]
    public static partial class BasisLocalPlayerData
    {
        public static IBasisLocalPlayer Instance;
        public static bool PlayerReady;
        public static event Action OnLocalPlayerInitialized;

        public static void RaiseLocalPlayerInitialized()
        {
            PlayerReady = true;
            OnLocalPlayerInitialized?.Invoke();
        }
    }
}
