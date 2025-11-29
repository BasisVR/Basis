using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Networking.Receivers
{
    public static class BasisAudioRemoteSource
    {
        public const string AudioSource = "Packages/com.basis.sdk/Prefabs/Players/AudioSource.prefab";
        private static GameObject LoadableAudioSource;
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> Loadable;
        public static void Initalize()
        {
            Loadable = Addressables.LoadAssetAsync<GameObject>(AudioSource);
            if(Loadable.IsValid() == false)
            {
                BasisDebug.LogError("Cant Find Audio Source!");
                return;
            }
            LoadableAudioSource = Loadable.WaitForCompletion();
            if(LoadableAudioSource.TryGetComponent<AudioSource>(out AudioSource v))
            {
                
            }
            else
            {
                BasisDebug.LogError("Loaded Audio Source does  not have a audio source!");
            }
        }
        public static void DeInitalize()
        {
            if (Loadable.IsValid())
            {
                Loadable.Release();
            }
        }
        public static GameObject RequestAudio(Transform Parent)
        {
            GameObject Object = GameObject.Instantiate(LoadableAudioSource, Parent);
            return Object;
        }
        public static void Return(GameObject obj)
        {
            GameObject.Destroy(obj);
        }
    }
}
