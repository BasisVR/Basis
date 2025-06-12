using System.Collections.Generic;
using UnityEngine;

namespace JigglePhysics
{

    internal class JiggleRigHandler<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T instance;

        protected static List<IJiggleAdvancable> jiggleRigs = new List<IJiggleAdvancable>();
        protected static IJiggleAdvancable[] jiggleRigsArray;
        protected static int JiggleRigCount;
        private static void CreateInstanceIfNeeded()
        {
            if (instance)
            {
                return;
            }
            jiggleRigs.Clear();
            GameObject obj = new GameObject("JiggleRigHandler", typeof(T))
            {
                hideFlags = HideFlags.DontSave
            };
            if (!obj.TryGetComponent(out instance))
            {
                throw new UnityException("Should never happen!");
            }
            DontDestroyOnLoad(obj);
        }

        private static void RemoveInstanceIfNeeded()
        {
            if (jiggleRigs.Count != 0) return;
            if (!instance) return;
            if (Application.isPlaying)
            {
                Destroy(instance.gameObject);
            }
            else
            {
                DestroyImmediate(instance.gameObject);
            }
            instance = null;
        }
        internal static void AddJiggleRigAdvancable(IJiggleAdvancable advancable)
        {
            CreateInstanceIfNeeded();
            if (jiggleRigs.Contains(advancable))
            {
                return;
            }
            jiggleRigs.Add(advancable);
            jiggleRigsArray = jiggleRigs.ToArray();
            JiggleRigCount = jiggleRigsArray.Length;
        }

        internal static void RemoveJiggleRigAdvancable(IJiggleAdvancable advancable)
        {
            if (!jiggleRigs.Contains(advancable))
            {
                RemoveInstanceIfNeeded();
                return;
            }
            jiggleRigs.Remove(advancable);
            jiggleRigsArray = jiggleRigs.ToArray();
            JiggleRigCount = jiggleRigsArray.Length;
            RemoveInstanceIfNeeded();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }
    }
}
