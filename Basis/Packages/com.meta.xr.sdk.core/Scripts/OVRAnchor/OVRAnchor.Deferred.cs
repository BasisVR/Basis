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
using UnityEngine;
using static OVRPlugin;

partial struct OVRAnchor
{
    private struct DeferredValue
    {
        public OVRTask<bool> Task;
        public bool EnabledDesired;
        public ulong RequestId;
        public double Timeout;
        public float StartTime;
    }

    private struct DeferredKey : IEquatable<DeferredKey>
    {
        public ulong Space;
        public SpaceComponentType ComponentType;
        public static DeferredKey FromEvent(OVRDeserialize.SpaceSetComponentStatusCompleteData eventData) => new()
        {
            Space = eventData.Space,
            ComponentType = eventData.ComponentType,
        };

        public bool Equals(DeferredKey other) => Space == other.Space && ComponentType == other.ComponentType;
        public override bool Equals(object obj) => obj is DeferredKey other && Equals(other);
        public override int GetHashCode() => unchecked(Space.GetHashCode() * 486187739 + ((int)ComponentType).GetHashCode());
    }

    private static readonly Dictionary<DeferredKey, List<DeferredValue>> _deferredTasks = new();

    internal static OVRTask<bool> CreateDeferredSpaceComponentStatusTask(ulong space, SpaceComponentType componentType, bool enabledDesired, double timeout
    )
    {
        var key = new DeferredKey
        {
            Space = space,
            ComponentType = componentType
        };

        if (!_deferredTasks.TryGetValue(key, out var list))
        {
            list = OVRObjectPool.List<DeferredValue>();
            _deferredTasks.Add(key, list);
        }

        var task = OVRTask.FromGuid<bool>(Guid.NewGuid());

        list.Add(new DeferredValue
        {
            EnabledDesired = enabledDesired,
            Task = task,
            Timeout = timeout,
            StartTime = Time.realtimeSinceStartup,
        });

        return task;
    }

    internal static void OnSpaceSetComponentStatusComplete(OVRDeserialize.SpaceSetComponentStatusCompleteData eventData)
    {
        var key = DeferredKey.FromEvent(eventData);
        if (!_deferredTasks.TryGetValue(key, out var list)) return;

        try
        {
            var isEnabled = eventData.Enabled != 0;
            for (var i = 0; i < list.Count; i++)
            {
                var value = list[i];
                var task = value.Task;
                bool? result = null;

                if (eventData.RequestId == value.RequestId)
                {
                    // If this result was initiated by us, then use that value
                    result = eventData.Result >= 0;
                }
                else if (isEnabled == value.EnabledDesired)
                {
                    // We're done!
                    result = true;
                }
                // Check to see if there is any other change pending
                else if (!GetSpaceComponentStatus(eventData.Space, eventData.ComponentType, out _,
                        out var changePending))
                {
                    result = false;
                }
                // If there's no other change pending, then try to change the component status
                else if (!changePending)
                {
                    var timeout = value.Timeout;
                    if (timeout > 0)
                    {
                        // Subtract elapsed time
                        timeout -= Time.realtimeSinceStartup - value.StartTime;
                        if (timeout <= 0)
                        {
                            result = false;
                        }
                    }

                    if (result == null)
                    {
                        ulong requestId;
                        if (SetSpaceComponentStatus(eventData.Space, eventData.ComponentType, value.EnabledDesired,
                                timeout, out requestId
                                ))
                        {
                            value.RequestId = requestId;
                            list[i] = value;
                        }
                        else
                        {
                            result = false;
                        }
                    }
                }

                if (result.HasValue)
                {
                    list.RemoveAt(i--);
                    task.SetResult(result.Value);
                }
            }
        }
        finally
        {
            if (list.Count == 0)
            {
                OVRObjectPool.Return(list);
                _deferredTasks.Remove(key);
            }
        }
    }
}
