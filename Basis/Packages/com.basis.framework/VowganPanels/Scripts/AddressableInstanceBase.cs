using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    /// <summary>
    /// Type used by objects instantiated through Addressable instances.
    /// For UI behaviours, AddressableUIInstanceBase should be used instead.
    /// </summary>
    public abstract class AddressableInstanceBase : MonoBehaviour
    {

        public Action OnDestroyed;

        /// <summary>
        /// Runs immediately after instantiation from Addressables.
        /// </summary>
        protected virtual void OnCreateEvent(){}

        /// <summary>
        /// Runs immediately before destruction and release from Addressables.
        /// </summary>
        protected virtual void OnDestroyEvent(){}


        /// <summary>
        /// Create a new Addressable Instance from a given path.
        /// </summary>
        public static TInstance CreateNew<TInstance>(string referencePath) where TInstance: AddressableInstanceBase
        {
            GameObject obj = Addressables.InstantiateAsync(referencePath).WaitForCompletion();
            TInstance instance = obj.GetComponent<TInstance>();
            instance.OnCreateEvent();
            return instance;
        }

        /// <summary>
        /// Create a new Addressable Instance from a given path with an assigned parent.
        /// </summary>
        public static TElement CreateNew<TElement>(string referencePath, Transform parent) where TElement: AddressableInstanceBase
        {
            GameObject obj = Addressables.InstantiateAsync(referencePath,
                    new InstantiationParameters(parent, false)).WaitForCompletion();
            TElement element = obj.GetComponent<TElement>();
            element.OnCreateEvent();
            return element;
        }

        /// <summary>
        /// Destroy this addressable instance. Callbacks will run first, followed by a call to destroy it.
        /// This object is released from Addressables during it's OnDestroy event.
        /// </summary>
        public void DestroyInstance()
        {
            OnDestroyEvent();
            OnDestroyed?.Invoke();
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            Addressables.ReleaseInstance(gameObject);
        }
    }
}
