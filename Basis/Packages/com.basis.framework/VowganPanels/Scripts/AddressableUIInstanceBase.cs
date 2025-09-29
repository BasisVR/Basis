using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    /// <summary>
    /// Type used by UI elements instantiated through Addressable instances.
    /// This comes with the events for the Unity UI lifecycle.
    /// For non-UI behaviours, AddressableInstanceBase should be used instead.
    /// </summary>
    public abstract class AddressableUIInstanceBase : UIBehaviour
    {

        public Action OnReleased;
        [HideInInspector]public bool IsReleased;

        /// <summary>
        /// Lazy initialization for the self RectTransform.
        /// </summary>
        public RectTransform rectTransform
        {
            get
            {
                if (!_rectTransform) _rectTransform = GetComponent<RectTransform>();
                return _rectTransform;
            }
        }

        private RectTransform _rectTransform;

        /// <summary>
        /// Runs immediately after instantiation from Addressables.
        /// </summary>
        protected virtual void OnCreateEvent(){}

        /// <summary>
        /// Runs immediately before destruction and release from Addressables.
        /// </summary>
        protected virtual void OnReleaseEvent(){}


        /// <summary>
        /// Create a new Addressable UI Instance from a given path.
        /// </summary>
        public static TInstance CreateNew<TInstance>(string referencePath) where TInstance: AddressableUIInstanceBase
        {
            //TODO: if the string is an invalid path, this will error. Create better handling for this.
            GameObject obj = Addressables.InstantiateAsync(referencePath).WaitForCompletion();
            TInstance instance = obj.GetComponent<TInstance>();
            instance.OnCreateEvent();
            return instance;
        }

        /// <summary>
        /// Create a new Addressable UI Instance from a given path with an assigned parent.
        /// "Parent" takes a component for easier assignment.
        /// </summary>
        public static TElement CreateNew<TElement>(string referencePath, Component parent) where TElement: AddressableUIInstanceBase
        {
            GameObject obj = Addressables.InstantiateAsync(referencePath,
                new InstantiationParameters(parent.transform, false)).WaitForCompletion();
            TElement element = obj.GetComponent<TElement>();
            element.OnCreateEvent();
            return element;
        }

        /// <summary>
        /// Destroy this addressable instance.
        /// Callbacks will run first, followed by an Addressables Release.
        /// </summary>
        public void ReleaseInstance()
        {
            IsReleased = true;
            OnReleaseEvent();
            OnReleased?.Invoke();
            Addressables.ReleaseInstance(gameObject);
        }

        protected override void OnDestroy()
        {
            if (!IsReleased)
                ReleaseInstance();
        }
    }
}
