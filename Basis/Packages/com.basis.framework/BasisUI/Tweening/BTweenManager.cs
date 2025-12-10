using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BTween
{
    public class BTweenManager : MonoBehaviour
    {
        public static BTweenManager Instance
        {
            get
            {
                if (!_instance) AssignInstance();
                return _instance;
            }
        }
        private static BTweenManager _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AssignInstance()
        {
            _instance = FindAnyObjectByType<BTweenManager>(FindObjectsInactive.Include);
            if (_instance) return;
            GameObject obj = new(nameof(BTweenManager));
            _instance = obj.AddComponent<BTweenManager>();
        }

        private void Awake()
        {
            if (_instance)
            {
                if (_instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
            }
            else
            {
                _instance = this;
                DontDestroyOnLoad(this);
            }
        }

        private const int MAX_GROUP_COUNT = 16;
        private static readonly List<Action<float>> _tweenProcessors = new(MAX_GROUP_COUNT);

        internal static void RegisterGroup(Action<float> processor)
        {
            if (processor == null) return;
            if (_tweenProcessors.Contains(processor)) return;
            _tweenProcessors.Add(processor);
        }

        private static void ProcessAll()
        {
            float currentTime = Time.realtimeSinceStartup;
            List<Action<float>> processors = _tweenProcessors;
            foreach (Action<float> tween in processors)
            {
                tween(currentTime);
            }
        }

        private void Update()
        {
            ProcessAll();
        }
    }
}
