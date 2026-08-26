using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Basis.Scripts.UI
{
    public static class BasisUINeedsVisibleTrackers
    {
        private static readonly HashSet<MonoBehaviour> requesters = new();
        private static bool wasVisible;
        private static bool dirty;

        public static bool ShouldBeVisible => requesters.Count > 0;

        public static void Add(MonoBehaviour requester)
        {
            if (requesters.Add(requester))
            {
                dirty = true;
            }
        }

        public static void Remove(MonoBehaviour requester)
        {
            if (requesters.Remove(requester))
            {
                dirty = true;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            requesters.Clear();
            wasVisible = false;
            dirty = false;

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(PostLateUpdate)) continue;

                var sub = loop.subSystemList[i];
                var existing = sub.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var list = new List<PlayerLoopSystem>(existing.Length + 1);
                for (int j = 0; j < existing.Length; j++)
                {
                    if (existing[j].type != typeof(BasisUINeedsVisibleTrackers))
                    {
                        list.Add(existing[j]);
                    }
                }
                list.Add(new PlayerLoopSystem
                {
                    type = typeof(BasisUINeedsVisibleTrackers),
                    updateDelegate = Tick,
                });
                sub.subSystemList = list.ToArray();
                loop.subSystemList[i] = sub;
                break;
            }
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static void Tick()
        {
            if (!dirty) return;
            if (BasisDeviceManagement.Instance == null) return;
            dirty = false;

            var shouldBeVisible = ShouldBeVisible;
            if (shouldBeVisible != wasVisible)
            {
                wasVisible = shouldBeVisible;
                BasisDeviceManagement.VisibleTrackers(shouldBeVisible);
            }
        }
    }
}
