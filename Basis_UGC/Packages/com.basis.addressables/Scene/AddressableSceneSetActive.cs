using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Enums;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace Basis.Scripts.Addressable_Driver.Scene
{
    public static class AddressableSceneSetActive
    {
        public static void SetSceneActive(AddressableLoadResourceBase LoadRequest)
        {
            if (LoadRequest.ResourceType == AddressableLoadResourceType.Scene && LoadRequest.Handles.Count != 0)
            {
                SceneInstance Instance = (SceneInstance)LoadRequest.Handles[0].Result;
                Debug.Log("Setting Active Scene to " + Instance.Scene.name);
                SceneManager.SetActiveScene(Instance.Scene);
                Debug.Log("Set Active Scene" + Instance.Scene.name);
                return;
            }
        }
    }
}