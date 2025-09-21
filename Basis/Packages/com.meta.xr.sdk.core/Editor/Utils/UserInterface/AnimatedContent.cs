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

using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    public class AnimatedContent : ScriptableObject
    {
        public Texture2D[] frames;
        public float frameDuration;

        private int _currentIndex;
        private float _lastTimer = 0.0f;

        public Texture2D CurrentFrame => frames[_currentIndex];

        public void Update()
        {
            if (frameDuration == 0)
            {
                return;
            }

            var newTimer = Time.realtimeSinceStartup;
            var delta = newTimer - _lastTimer;
            if (delta > frameDuration)
            {
                var numberOfDeltas = Mathf.Floor(delta / frameDuration);
                _lastTimer = _lastTimer + numberOfDeltas * frameDuration;
                _currentIndex = (_currentIndex + 1) % frames.Length;
            }
        }

        public void OnValidate()
        {
            _lastTimer = Time.realtimeSinceStartup;
        }
    }
}
