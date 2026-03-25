using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.BasisUI
{
    /// <summary>
    /// Type used by objects instantiated through Addressable instances.
    /// For UI behaviours, AddressableUIInstanceBase should be used instead.
    /// </summary>
    public abstract class AddressableInstanceBase : MonoBehaviour, IAddressableInstance
    {

        public Action OnInstanceReleased
        {
            get => _onReleased;
            set => _onReleased = value;
        }
        protected Action _onReleased;

        public bool IsReleased => _isReleased;
        protected bool _isReleased;

        /// <summary>
        /// Runs immediately after instantiation from Addressables.
        /// </summary>
        public virtual void OnCreateEvent(){}

        /// <summary>
        /// Runs immediately before destruction and release from Addressables.
        /// </summary>
        public virtual void OnReleaseEvent(){}

        public bool HasRunCreateEvent => _hasRunCreateEvent;
        protected bool _hasRunCreateEvent;

        /// <summary>
        /// Create a new Addressable UI Instance from a given path.
        /// </summary>
        public static TInstance CreateNew<TInstance>(string referencePath) where TInstance: AddressableInstanceBase
        {
            try
            {
                GameObject obj = Addressables.InstantiateAsync(referencePath).WaitForCompletion();
                TInstance instance = obj.GetComponent<TInstance>();
                if (!instance.HasRunCreateEvent) instance.OnCreateEvent();
                return instance;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load Addressable at path:\n{referencePath} {e}");
                return null;
            }
        }

        /// <summary>
        /// Create a new Addressable UI Instance from a given path with an assigned parent.
        /// "Parent" takes a component for easier assignment.
        /// </summary>
        public static TElement CreateNew<TElement>(string referencePath, Component parent) where TElement: AddressableInstanceBase
        {
            GameObject obj = Addressables.InstantiateAsync(referencePath,
                new InstantiationParameters(parent.transform, false)).WaitForCompletion();
            TElement element = obj.GetComponent<TElement>();
            if (!element.HasRunCreateEvent) element.OnCreateEvent();
            return element;
        }

        /// <summary>
        /// Destroy this addressable instance.
        /// Callbacks will run first, followed by an Addressables Release.
        /// </summary>
        public void ReleaseInstance()
        {
            _isReleased = true;
            OnReleaseEvent();
            OnInstanceReleased?.Invoke();
            Addressables.ReleaseInstance(gameObject);
        }

        protected virtual void OnDestroy()
        {
            if (!IsReleased)
                ReleaseInstance();
        }
    }
}
