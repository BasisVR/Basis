using System;
using System.Threading.Tasks;

namespace Basis.Scripts.BasisSdk.Players
{
    public interface IBasisLocalPlayer
    {
        Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle);
    }

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
