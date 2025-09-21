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
using System.Linq;
using Meta.XR.ImmersiveDebugger.UserInterface;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.ImmersiveDebugger.Utils
{
    /// <summary>
    /// Manages the instance cache, clear up cache when needed
    /// </summary>
    internal class InstanceCache
    {
        internal readonly Dictionary<Type, List<InstanceHandle>> CacheData = new Dictionary<Type, List<InstanceHandle>>();
        private readonly List<InstanceHandle> _emptyCache = new List<InstanceHandle>();

        public event Action<Type> OnCacheChangedForTypeEvent;
        public event Func<InstanceHandle, IInspector> OnInstanceAdded;
        public event Action<InstanceHandle> OnInstanceRemoved;

        /// <summary>
        /// This function will return active instances for a class (UnityEngine components), return cached result first.
        /// </summary>
        /// <param name="classType">specify which classType to query</param>
        /// <returns>fetched instance result, could be empty list if no instances</returns>
        public List<InstanceHandle> GetCacheDataForClass(Type classType)
        {
            CacheData.TryGetValue(classType, out var instances);
            return instances ?? _emptyCache;
        }

        private List<InstanceHandle> FetchObjectsHandlesOfType(Type classType)
        {
            var objects = Object.FindObjectsByType(classType, FindObjectsSortMode.None);
            var objectHandles = objects.Select(obj => new InstanceHandle(classType, obj)).ToList();
            return objectHandles;
        }

        /// <summary>
        /// Register class type for caching purpose, also attempt to fetch instance
        /// </summary>
        public void RegisterClassType(Type classType)
        {
            if (CacheData.ContainsKey(classType))
            {
                return;
            }

            CacheData[classType] = new List<InstanceHandle>();
        }

        /// <summary>
        /// Register list of class types for caching purpose, also attempt to fetch instance
        /// </summary>
        public void RegisterClassTypes(IEnumerable<Type> types)
        {
            foreach (var type in types)
            {
                RegisterClassType(type);
            }
        }

        /// <summary>
        /// Scan the CacheData to find out newly activated and deactivated instances.
        /// </summary>
        internal void RetrieveInstances()
        {
            Dictionary<Type, List<InstanceHandle>> originalCacheData = new Dictionary<Type, List<InstanceHandle>>(CacheData);
            foreach (var dataPair in originalCacheData)
            {
                var type = dataPair.Key;
                var instances = dataPair.Value;
                bool instancesChanged = false;

                var newInstances = FetchObjectsHandlesOfType(type);
                foreach (var newInstance in newInstances)
                {
                    if (!instances.Contains(newInstance))
                    {
                        instances.Add(newInstance);
                        instancesChanged = true;

                        OnInstanceAdded?.Invoke(newInstance);
                    }
                }
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (!instances[i].Valid)
                    {
                        OnInstanceRemoved?.Invoke(instances[i]);

                        instances.RemoveAt(i);
                        instancesChanged = true;
                    }
                }

                if (instancesChanged)
                {
                    CacheData[type] = instances;
                    OnCacheChangedForTypeEvent?.Invoke(type);
                }
            }
        }

        // Utility methods used for register handle directly, used by DebugInspectorManager
        internal void RegisterHandle(InstanceHandle handle)
        {
            RegisterClassType(handle.Type);

            if (CacheData.TryGetValue(handle.Type, out var handles))
            {
                handles.Add(handle);
            }
        }

        internal void UnregisterHandle(InstanceHandle handle)
        {
            if (CacheData.TryGetValue(handle.Type, out var handles))
            {
                handles.Remove(handle);
            }
        }
    }
}

