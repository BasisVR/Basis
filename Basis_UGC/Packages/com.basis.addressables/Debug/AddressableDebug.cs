using UnityEngine;

namespace Basis.Scripts.Addressable_Driver.DebugError
{
    public static class AddressableDebug
    {
        public static void DebugError(string message, AddressableLoadResourceBase loadStruct)
        {
            loadStruct.OnLoadFailure.Invoke(message);
            Debug.LogError(message);
        }
    }
}