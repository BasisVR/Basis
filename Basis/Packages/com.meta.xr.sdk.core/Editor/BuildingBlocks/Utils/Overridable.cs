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

namespace Meta.XR.BuildingBlocks.Editor
{
    public class Overridable<T>
    {
        private readonly T _originalValue;
        private T _overrideValue;
        private bool _isOverridden;

        public Overridable(T originalValue)
        {
            _originalValue = originalValue;
        }

        public T Value => _isOverridden ? _overrideValue : _originalValue;

        public void SetOverride(T overrideValue)
        {
            if (overrideValue is null)
            {
                RemoveOverride();
                return;
            }

            _overrideValue = overrideValue;
            _isOverridden = true;
        }

        public void RemoveOverride()
        {
            _isOverridden = false;
        }

        public static implicit operator T(Overridable<T> overridable) => overridable.Value;
        public static implicit operator Overridable<T>(T value) => new(value);
        public override string ToString() => Value?.ToString() ?? string.Empty;
    }
}
