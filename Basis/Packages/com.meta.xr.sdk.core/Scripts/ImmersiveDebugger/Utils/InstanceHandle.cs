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
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Meta.XR.ImmersiveDebugger.Utils
{
    [Serializable]
    internal readonly struct InstanceHandle : IEquatable<InstanceHandle>
    {
        public Object Instance { get; }
        public Type Type { get; }
        public int InstanceId { get; }

        public bool IsStatic => InstanceId == 0;
        public bool Valid => Type != null && (IsStatic || Instance != null || Type == typeof(Scene));

        public InstanceHandle(Type type, Object instance)
        {
            Type = type;
            Instance = instance;
            InstanceId = instance != null ? instance.GetInstanceID() : 0;
        }

        public InstanceHandle(Scene scene)
        {
            Type = typeof(Scene);
            Instance = null;
            InstanceId = scene.handle;
        }

        public static InstanceHandle Static(Type type) => new InstanceHandle(type, null);
        public bool Equals(InstanceHandle other) => InstanceId == other.InstanceId && Type == other.Type;
        public override bool Equals(object obj) => obj is InstanceHandle other && Equals(other);
        public override int GetHashCode() => unchecked(InstanceId.GetHashCode() * 486187739 + Type?.GetHashCode() ?? 0);
    }
}

