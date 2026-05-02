using System;
using System.Threading.Tasks;

namespace Basis.Scripts.BasisSdk.Players
{
    public interface IBasisLocalPlayer
    {
        Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle);
    }

    // Use from sdk code; use BasisLocalPlayer.Instance from framework code.
    // Framework's BasisLocalPlayer registers when present; otherwise the SDK
    // editor preview registers a stand-in (gated by BASIS_FRAMEWORK_EXISTS so
    // only one ever registers).
    public static class BasisLocalPlayerService
    {
        public static IBasisLocalPlayer Instance { get; private set; }
        public static bool PlayerReady { get; private set; }
        public static event Action OnLocalPlayerInitalized;

        public static void Register(IBasisLocalPlayer impl) => Instance = impl;

        public static void Unregister(IBasisLocalPlayer impl)
        {
            if (Instance == impl)
            {
                Instance = null;
                PlayerReady = false;
            }
        }

        public static void RaiseLocalPlayerInitialized()
        {
            PlayerReady = true;
            OnLocalPlayerInitalized?.Invoke();
        }
    }
}
