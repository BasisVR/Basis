using Basis.Network.Core;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    public static class BasisNetworkPackageBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            int reported = BasisNetworkPackages.Failures.Count;
            BasisNetworkPackages.Initialize(typeof(BasisNetworkPackages).Assembly, typeof(NetworkClient).Assembly, typeof(NetworkServer).Assembly);
            System.Collections.Generic.IReadOnlyList<string> failures = BasisNetworkPackages.Failures;
            for (int index = reported; index < failures.Count; index++) BasisDebug.LogError(failures[index], BasisDebug.LogTag.Networking);
        }
    }
}
