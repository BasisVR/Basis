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


using Meta.XR.ImmersiveDebugger.Manager;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using System.Collections.Generic;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    public class Values : Controller
    {
        protected readonly List<Value> _values = new List<Value>();

        internal List<Value> GetValues => _values;

        internal Watch Watch { get; private set; }

        internal ImageStyle BackgroundStyle
        {
            set
            {
                foreach (var valueItem in _values)
                {
                    valueItem.BackgroundStyle = value;
                }
            }
        }

        internal TextStyle TextStyle
        {
            set
            {
                foreach (var valueItem in _values)
                {
                    valueItem.TextStyle = value;
                }
            }
        }

        internal void Setup(Watch watch)
        {
            if (watch == Watch) return;
            Watch = watch;

            // Destroy previously added values if any
            foreach (var value in _values)
            {
                Owner.Remove(value, true);
            }
            _values.Clear();

            // Append new values
            for (var i = 0; i < Watch?.NumberOfValues; i++)
            {
                var value = Owner.Append<Value>($"value {i}");
                value.LayoutStyle = Style.Instantiate<LayoutStyle>("MemberValueDynamic");
                value.TextStyle = Style.Load<TextStyle>("MemberValue");
                value.BackgroundStyle = Style.Load<ImageStyle>("MemberValueBackground");
                _values.Add(value);
            }
        }

        private void Update()
        {
            if (Watch is not { Valid: true }) return;

            var watchValues = Watch.Values;

            var index = Watch.NumberOfValues;
            foreach (var value in _values)
            {
                value.Content = watchValues[--index];
            }
        }
    }
}

