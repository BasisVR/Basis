using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    public abstract class AddressableUIInstanceBase : UIBehaviour, IAddressableInstance
    {
        protected override void OnEnable()
        {
            base.OnEnable();
            if (!HasRunCreateEvent) OnCreateEvent();
        }

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

        public bool HasRunCreateEvent => _hasRunCreateEvent;
        protected bool _hasRunCreateEvent;

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

        public Action OnReleased
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
        public virtual void OnCreateEvent()
        {
            _hasRunCreateEvent = true;
        }

        /// <summary>
        /// Runs immediately before destruction and release from Addressables.
        /// </summary>
        public virtual void OnReleaseEvent()
        {
        }

        /// <summary>
        /// Destroy this addressable instance.
        /// Callbacks will run first, followed by an Addressables Release.
        /// </summary>
        public void ReleaseInstance()
        {
            _isReleased = true;
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
