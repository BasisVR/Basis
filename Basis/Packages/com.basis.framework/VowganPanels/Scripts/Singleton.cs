using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public class Singleton<T> : MonoBehaviour where T: MonoBehaviour
    {
        public static T Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = FindAnyObjectByType<T>();
                    if (_instance) return _instance;

                    GameObject obj = new GameObject(typeof(T).Name);
                    _instance = obj.AddComponent<T>();
                }
                return _instance;
            }
        }

        private static T _instance;

        private void Awake()
        {
            if (_instance && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this as T;
            DontDestroyOnLoad(_instance);
            OnAwakeValid();
        }

        protected virtual void OnAwakeValid()
        {

        }
    }
}
