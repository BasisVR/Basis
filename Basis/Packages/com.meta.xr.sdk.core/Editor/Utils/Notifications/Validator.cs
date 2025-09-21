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
using Meta.XR.Editor.Settings;
using UnityEngine;

namespace Meta.XR.Editor.Notifications
{
    internal class Validator
    {
        private abstract class FilterChecker
        {
            public abstract bool CheckCondition(string @operator, string value);
        }

        private abstract class BaseFilterChecker<T> : FilterChecker
        {
            protected abstract T GetField();

            public override bool CheckCondition(string @operator, string value)
            {
                if (!TryParse(value, out var parsedValue))
                {
                    return false;
                }

                var field = GetField();

                return @operator switch
                {
                    "=" => Equals(field, parsedValue),
                    "!=" => !Equals(field, parsedValue),
                    ">" when field is IComparable comparableField => comparableField.CompareTo(parsedValue) > 0,
                    "<" when field is IComparable comparableField => comparableField.CompareTo(parsedValue) < 0,
                    ">=" when field is IComparable comparableField => comparableField.CompareTo(parsedValue) >= 0,
                    "<=" when field is IComparable comparableField => comparableField.CompareTo(parsedValue) <= 0,
                    _ => false
                };
            }

            private static bool TryParse(string value, out T result)
            {
                try
                {
                    if (typeof(T) == typeof(Version))
                    {
                        result = (T)(object)Version.Parse(value);
                        return true;
                    }

                    result = (T)Convert.ChangeType(value, typeof(T));
                    return true;
                }
                catch
                {
                    result = default;
                    return false;
                }
            }
        }

        private class ValueFilterChecker<T> : BaseFilterChecker<T>
        {
            private readonly T _field;

            public ValueFilterChecker(T field)
            {
                _field = field;
            }

            protected override T GetField() => _field;
        }

        private class CallbackFilterChecker<T> : BaseFilterChecker<T>
        {
            private readonly Func<T> _fieldCallback;

            public CallbackFilterChecker(Func<T> fieldCallback)
            {
                _fieldCallback = fieldCallback;
            }

            protected override T GetField() => _fieldCallback();
        }

        private readonly Dictionary<string, FilterChecker> _checkers;

        public Validator()
        {
            _checkers = new Dictionary<string, FilterChecker>
            {
                { "platform", new ValueFilterChecker<string>(Application.platform.ToString()) },
                { "unity_version", new ValueFilterChecker<Version>(ParseUnityVersion(Application.unityVersion)) },
                { "uses_bb", new CallbackFilterChecker<bool>(() => UsageSettings.UsesBuildingBlocks.Value)},
                { "uses_xrsim", new CallbackFilterChecker<bool>(() => UsageSettings.UsesXRSimulator.Value)},
                { "uses_id", new CallbackFilterChecker<bool>(() => UsageSettings.UsesImmersiveDebugger.Value)},
                { "uses_upst", new CallbackFilterChecker<bool>(() => UsageSettings.UsesProjectSetupTool.Value)},
            };

            var sdkVersion = GetSdkVersion();
            if (sdkVersion.HasValue)
            {
                _checkers.Add("sdk_version", new ValueFilterChecker<int>(sdkVersion.Value));
            }
        }

        public bool ValidateFilter(NotificationFilter filter)
        {
            return _checkers.TryGetValue(filter.field, out var checker) &&
                   checker.CheckCondition(filter.@operator, filter.value);
        }

        private static int? GetSdkVersion()
        {
            var versionZero = new Version(0, 0, 0);
            if (OVRPlugin.wrapperVersion == null || OVRPlugin.wrapperVersion == versionZero)
            {
                return null;
            }

            return OVRPlugin.wrapperVersion.Minor - 32;
        }

        private static Version ParseUnityVersion(string versionString)
        {
            var versionParts = versionString.Split('.');

            var major = int.Parse(versionParts[0]);
            var minor = int.Parse(versionParts[1]);
            var patch = 0;

            if (versionParts.Length <= 2)
            {
                return new Version(major, minor, patch);
            }

            var patchString = new string(versionParts[2].TakeWhile(char.IsDigit).ToArray());
            patch = int.Parse(patchString);
            return new Version(major, minor, patch);
        }
    }
}
