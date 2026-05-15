using System;
using System.Threading.Tasks;

namespace Basis.Scripts.BasisSdk.Players
{
    public interface IBasisLocalPlayer
    {
        Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle);
    }

    // SDK-side local player data. Framework's BasisLocalPlayer writes Instance
    // when present; otherwise the SDK's editor preview writes a stand-in
    // (the preview only registers when Instance is still null, so only one writer
    // ever wins).
    public static class BasisLocalPlayerData
    {
        public static IBasisLocalPlayer Instance;
        public static bool PlayerReady;
        public static event Action OnLocalPlayerInitalized;

        public static void RaiseLocalPlayerInitialized()
        {
            PlayerReady = true;
            OnLocalPlayerInitalized?.Invoke();
        }
    }
}
