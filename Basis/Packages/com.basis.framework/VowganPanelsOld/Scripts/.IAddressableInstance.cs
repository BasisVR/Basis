using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = System.Object;

namespace Basis.VowganUI
{
    public interface IAddressableInstance
    {
        public Action OnDestroyed { get; }

        /// <summary>
        /// Runs immediately after instantiation from Addressables.
        /// </summary>
        public void OnCreateEvent();

        /// <summary>
        /// Runs immediately before destruction and release from Addressables.
        /// </summary>
        public void OnDestroyEvent();

        public static IAddressableInstance CreateNew<TInstance>(string referencePath)
            where TInstance : IAddressableInstance
        {
            GameObject obj = Addressables.InstantiateAsync(referencePath).WaitForCompletion();
            TInstance instance = obj.GetComponent<TInstance>();
            instance.OnCreateEvent();
            return instance;
        }

        /// <summary>
        /// Destroy this addressable instance. Callbacks will run first, followed by a call to destroy it.
        /// This object is released from Addressables during it's OnDestroy event.
        /// </summary>
        public void DestroyInstance()
        {
            OnDestroyEvent();
            OnDestroyed?.Invoke();
            UnityEngine.Object.Destroy(((MonoBehaviour)this).gameObject);
        }
    }
}
