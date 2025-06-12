using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Enums;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.Scripts.Addressable_Driver.Validation
{
    public static class AddressableValidation
    {
        public static bool ValidateLoadResource(AddressableLoadResourceBase loadRequest)
        {
            if (string.IsNullOrEmpty(loadRequest.Key))
            {
                AddressableDebug.DebugError("Key Provided was Null or Empty", loadRequest);
                return false;
            }
            return true;
        }

        public static bool HasExpectedResult(AddressableLoadResourceBase loadRequest)
        {
            if (loadRequest.ExpectedResult == AddressableExpectedResult.IgnoreThisCheck)
            {
                return true;
            }

            if (loadRequest.ExpectedResult == AddressableExpectedResult.SingleItem)
            {
                if (loadRequest.ResourceLocationHandles.Count != 1)
                {
                    Debug.Log("Addressable with keys " + loadRequest.Key + " has more than one resource locator");
                    return false;
                }
            }
            else if (loadRequest.ExpectedResult == AddressableExpectedResult.MulitpleItems)
            {
                if (loadRequest.ResourceLocationHandles.Count <= 1)
                {
                    Debug.Log("Addressable with keys " + loadRequest.Key + " has more than one resource locator but only returned " + loadRequest.ResourceLocationHandles.Count);
                    return false;
                }
            }

            return true;
        }

        public static bool ValidHandle(AddressableLoadResourceBase loadRequest, AsyncOperationHandle handle)
        {
            if (handle.IsValid())
            {
                return true;
            }
            else
            {
                AddressableDebug.DebugError("Resource handles were empty... [" + loadRequest.Key + "]", loadRequest);
                return false;
            }
        }
    }
}