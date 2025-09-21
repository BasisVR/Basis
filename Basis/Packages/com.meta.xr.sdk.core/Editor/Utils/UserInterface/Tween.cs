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
using Meta.XR.Editor.Id;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    /// <summary>
    /// This Tween is actually not a 0 to 1 tween but rather a continuous smooth to a potentially moving target.
    /// It's update is disconnected to the owner's update  to ensure its own refresh and avoid cluttering the
    /// owner's logic with bespoke code.
    /// </summary>
    internal class Tween
    {
        public static class Registry
        {
            private static readonly Dictionary<string, Tween> Tweens = new();
            public static readonly List<Tween> ActiveTweens = new();
            private static double _lastUpdateTime;

            static Registry()
            {
                EditorApplication.update -= UpdateEditor;
                EditorApplication.update += UpdateEditor;
            }

            public static void UpdateEditor()
            {
                if (ActiveTweens.Count == 0) return;

                const float deltaTimeThreshold = 0.033f; // A delta time threshold in case we have a large delta from system
                var timeSinceStartup = EditorApplication.timeSinceStartup;
                var deltaTime = Mathf.Min(deltaTimeThreshold, (float)(timeSinceStartup - _lastUpdateTime));
                _lastUpdateTime = timeSinceStartup;
                var activeTweens = ActiveTweens.ToList();
                foreach (var tween in activeTweens)
                {
                    tween.Update(deltaTime);
                }
            }

            public static Tween Fetch(IIdentified owner, Action<float> set)
            {
                var uid = owner.Id + set?.GetHashCode();
                if (!Tweens.TryGetValue(uid, out var tween))
                {
                    tween = new Tween()
                    {
                        Setter = set
                    };
                    Tweens.Add(uid, tween);
                }

                return tween;
            }
        }

        /// <summary>
        /// Tweens are stored in a registry, to avoid the need for the owner to keep track of them.
        /// The registry handles the update as well.
        /// </summary>
        public static Tween Fetch(IIdentified owner, Action<float> set = null)
            => Registry.Fetch(owner, set);

        /// <summary>
        /// The Setter that will be updated throughout the Tween lifetime
        /// </summary>
        public Action<float> Setter { get; set; }

        /// <summary>
        /// The Target of the Tween
        /// </summary>
        public float Target { get; set; }

        /// <summary>
        /// The Start value of the Tween, if it is not already in progress
        /// </summary>
        public float Start { get; set; }

        /// <summary>
        /// An arbitrary multiplier factor for the smoothing
        /// </summary>
        public float Speed { get; set; } = 10.0f;

        /// <summary>
        /// Epsilon value used to compute whether or not Current reached to Target
        /// </summary>
        public float Epsilon { get; set; } = 5f;
        public bool Active { get; private set; }
        public float Current;
        private bool _hasStarted;

        /// <summary>
        /// Start the Tween's updates
        /// </summary>
        public void Activate()
        {
            if (Active) return;

            Update(0.0f);

            if (!_hasStarted)
            {
                Current = Start;
                _hasStarted = true;
            }

            Active = true;
            Registry.ActiveTweens.Add(this);
        }

        /// <summary>
        /// Stop the Tween updates
        /// </summary>
        public void Deactivate()
        {
            if (!Active) return;

            Active = false;
            Registry.ActiveTweens.Remove(this);
        }

        private void Update(float deltaTime)
        {
            if (Math.Abs(Target - Current) <= Epsilon)
            {
                Current = Target;
                Deactivate();
            }
            else
            {
                Current = Mathf.Lerp(Current, Target, 1f - Mathf.Exp(-Speed * deltaTime));
            }

            Setter?.Invoke(Current);
        }

        public void Reset()
        {
            _hasStarted = false;
        }
    }
}
