using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    [AutoStaticsCleanup]
    public static partial class BasisUserActivation
    {
        private struct Press
        {
            public GameObject Target;
            public float At;
            public bool Resolved;
            public BasisContentBase Content;
        }

        private const int Capacity = 8;
        [NoAutoStaticsCleanup] private static readonly Press[] presses = new Press[Capacity];
        private static int next;

        public static void Note(GameObject target)
        {
            if (target == null) return;
            presses[next] = new Press { Target = target, At = Time.realtimeSinceStartup };
            next = (next + 1) % Capacity;
        }

        public static void Note(Component target)
        {
            if (target != null) Note(target.gameObject);
        }

        public static bool Recent(BasisContentBase content, float seconds)
        {
            if (content == null) return false;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < Capacity; i++)
            {
                ref Press press = ref presses[i];
                if (press.Target == null) continue;
                float age = now - press.At;
                if (age < 0f || age > seconds) continue;
                if (!press.Resolved)
                {
                    press.Resolved = true;
                    BasisContent.TryResolve(press.Target, out press.Content);
                }
                if (press.Content != null && press.Content == content) return true;
            }
            return false;
        }

        public static void Clear()
        {
            for (int i = 0; i < Capacity; i++) presses[i] = default;
            next = 0;
        }
    }
}
