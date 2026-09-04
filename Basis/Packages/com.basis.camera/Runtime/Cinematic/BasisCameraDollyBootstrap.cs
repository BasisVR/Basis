using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>
    /// Arms the shared dolly service for the session. It has to exist before any camera is taken out:
    /// a track arriving from somebody else is handled whether or not this client has ever opened one.
    /// </summary>
    public static class BasisCameraDollyBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisCameraDollyManager.Initialize();
            BasisCameraDollyShare.Register();
        }
    }
}
