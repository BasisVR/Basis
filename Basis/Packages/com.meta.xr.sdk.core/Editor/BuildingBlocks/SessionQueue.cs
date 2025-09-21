/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public static class SessionQueue
    {
        public static void Enqueue<T>(T item, string queueKey) where T : struct
        {
            var queue = LoadQueue<T>(queueKey);
            queue.Enqueue(item);
            SaveQueue(queue, queueKey);
        }

        public static T? Dequeue<T>(string queueKey) where T : struct
        {
            var queue = LoadQueue<T>(queueKey);
            if (queue.Count <= 0)
            {
                return null;
            }

            var item = queue.Dequeue();
            SaveQueue(queue, queueKey);
            return item;
        }

        public static int Count<T>(string queueKey) where T : struct
        {
            var queue = LoadQueue<T>(queueKey);
            return queue.Count;
        }

        public static void Clear(string queueKey)
        {
            SessionState.EraseString(queueKey);
        }

        private static Queue<T> LoadQueue<T>(string key) where T : struct
        {
            var json = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return new Queue<T>();
            }

            var helper = JsonUtility.FromJson<SerializationHelper<T>>(json);
            return new Queue<T>(helper.list);
        }

        private static void SaveQueue<T>(IEnumerable<T> queue, string key) where T : struct
        {
            var list = new List<T>(queue);
            var json = JsonUtility.ToJson(new SerializationHelper<T>(list));
            SessionState.SetString(key, json);
        }

        [Serializable]
        private class SerializationHelper<TU> where TU : struct
        {
            public List<TU> list;

            public SerializationHelper(List<TU> list)
            {
                this.list = list;
            }
        }
    }
}
