using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.BasisUI
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
                if (this == null) return null;
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
            GameObject obj = BasisAddressablePrefabCache.Instantiate(referencePath, null);
            if (obj == null) return null;
            TInstance instance = obj.GetComponent<TInstance>();
            if (!instance.HasRunCreateEvent) instance.OnCreateEvent();
            return instance;
        }

        /// <summary>
        /// Create a new Addressable UI Instance from a given path with an assigned parent.
        /// "Parent" takes a component for easier assignment.
        /// </summary>
        public static TElement CreateNew<TElement>(string referencePath, Component parent) where TElement: AddressableUIInstanceBase
        {
            if (parent == null)
            {
                BasisDebug.LogError($"Parent Missing! Requires Parent to function for UI!");
                return null;
            }
            try
            {
                GameObject obj = BasisAddressablePrefabCache.Instantiate(referencePath, parent.transform);

                if(obj == null)
                {
                    return null;
                }
                if (obj.TryGetComponent<TElement>(out TElement element))
                {
                    if (!element.HasRunCreateEvent)
                    {
                        element.OnCreateEvent();
                    }

                    return element;
                }
                else
                {
                    BasisDebug.LogError($"Failed to load Addressable at path:\n{referencePath} Missing {typeof(TElement)}");
                    return null;
                }
            }

            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to load Addressable at path:\n{referencePath} {e.Message}");
                return null;
            }

        }

        public Action OnInstanceReleased
        {
            get => onInstanceReleased;
            set => onInstanceReleased = value;
        }
        protected Action onInstanceReleased;

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
        public void ReleaseInstance() => ReleaseInstance(true);

        /// <summary>
        /// <paramref name="dropFromLayout"/> deactivates the object before releasing it.
        /// <see cref="BasisAddressablePrefabCache.DestroyInstance"/> destroys via
        /// <see cref="Object.Destroy"/>, which Unity defers to the end of the frame, so a released
        /// element otherwise stays an active child and keeps contributing to its parent layout
        /// group for the rest of this frame — the layout then lands wrong for exactly one frame
        /// (visible when a lazy tab replaces its placeholder). Deactivating applies immediately and
        /// drops it out of that pass. Skipped when the release is already being driven by
        /// <see cref="OnDestroy"/>, where changing active state is not valid.
        /// </summary>
        private void ReleaseInstance(bool dropFromLayout)
        {
            if (_isReleased) return;
            _isReleased = true;
            OnReleaseEvent();
            OnInstanceReleased?.Invoke();
            // gameObject may already be destroyed when this is driven by teardown (e.g. a menu
            // rebuild releasing stale buttons); the Unity-null check avoids the dead-component
            // gameObject getter that would otherwise throw a NullReferenceException.
            if (this != null)
            {
                if (dropFromLayout) gameObject.SetActive(false);
                BasisAddressablePrefabCache.DestroyInstance(gameObject);
            }
        }

        protected override void OnDestroy()
        {
            if (!IsReleased)
                ReleaseInstance(false);
        }
    }
}
