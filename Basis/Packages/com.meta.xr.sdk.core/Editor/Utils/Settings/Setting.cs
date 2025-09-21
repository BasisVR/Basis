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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.Editor.Settings
{
    internal abstract class Setting : IIdentified
    {
        /// <summary>
        /// Prefix used for all settings key
        /// </summary>
        public const string KeyPrefix = "Meta.XR.SDK";

        /// <summary>
        /// Link to an identifiable owner : ToolDescriptor, Tag, BlockBaseData, etc.
        /// The owner is used for telemetry, is injected into the key and helps keeping track of the setting item.
        /// </summary>
        public IIdentified Owner { get; set; }

        /// <summary>
        /// Label used for User Interface
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// More descriptive information, used for User Interface
        /// </summary>
        public string Tooltip { get; set; }

        /// <summary>
        /// Content used for User Interface (combination of label and tooltip)
        /// </summary>
        public GUIContent Content => _content ??= new GUIContent(Label ?? Uid, Tooltip);
        private GUIContent _content;

        /// <summary>
        /// Whether or not this setting should be instrumented by telemetry
        /// </summary>
        public bool SendTelemetry { get; set; }

        /// <summary>
        /// Suffix identifier, not necessarily unique, but unique per owner
        /// </summary>
        public string Uid { get; set; }

        public string OldKey { get; set; }


        /// <summary>
        /// The unique key used by this Settings Item
        /// </summary>
        public string Key => _key ??= (Owner != null) ? $"{KeyPrefix}.{Owner.Id}.{Uid}" : $"{KeyPrefix}.{Uid}";
        private string _key;
        public string Id => Key;

        protected void OnChanged(Origins origin, IIdentified originData)
        {
            SendTelemetryMarker(origin, originData);
        }

        private void SendTelemetryMarker(Origins origin, IIdentified originData)
        {
            if (!SendTelemetry) return;

            var marker = OVRTelemetry.Start(Telemetry.MarkerId.SettingsChanged)
                .AddAnnotation(Telemetry.AnnotationType.Label, Label ?? Uid)
                .AddAnnotation(Telemetry.AnnotationType.Action, Uid)
                .AddAnnotation(Telemetry.AnnotationType.ActionData, Owner?.Id)
                .AddAnnotation(Telemetry.AnnotationType.ActionType, GetType().Name)
                .AddAnnotation(Telemetry.AnnotationType.Origin, origin.ToString())
                .AddAnnotation(Telemetry.AnnotationType.OriginData, originData?.Id);


            AddAnnotations(marker).Send();
        }

        protected abstract OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker);

        public virtual void Reset() { }

        protected virtual void DrawForMenuImplementation(GenericMenu menu, Origins origin, IIdentified originData, Action callback = null) { }
        protected virtual void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null) { }

        public void DrawForMenu(GenericMenu menu, Origins origin, IIdentified originData, Action callback = null)
        {
            DrawForMenuImplementation(menu, origin, originData, callback);
        }

        public void DrawForGUI(Origins origin, IIdentified originData, Action callback = null)
        {
            DrawForGUIImplementation(origin, originData, callback);
        }
    }

    internal abstract class Setting<T> : Setting
    {
        public T Default { get; set; }
        public abstract T Value { get; protected set; }

        public void SetValue(T value, Origins origin = Origins.Unknown, IIdentified originData = null, Action callback = null)
        {
            // Value may not need to change
            var previousValue = Value;
            if (Equals(Value, value)) return;
            Value = value;

            // Value might have changed
            if (Equals(Value, previousValue)) return;

            // On changed callbacks
            callback?.Invoke();
            OnChanged(origin, originData);
        }

        public virtual bool Equals(T lhs, T rhs) => lhs.Equals(rhs);

        public override void Reset()
        {
            base.Reset();
            Value = Default;
        }

        protected override OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker)
        {
            return marker.AddAnnotation(Telemetry.AnnotationType.Type, typeof(T).Name)
                .AddAnnotation(Telemetry.AnnotationType.Value, Value.ToString());
        }
    }

    internal abstract class CustomSetting<T> : Setting<T>
    {
        public Func<T> Get;
        public Action<T> Set;

        public override T Value
        {
            get => Get.Invoke();
            protected set => Set?.Invoke(value);
        }
    }

    internal class ConstSetting<T> : Setting<T>
    {
        public override T Value
        {
            get => Default;
            protected set { }
        }
    }

    internal class CustomBool : CustomSetting<bool>
    {
        protected override void DrawForMenuImplementation(GenericMenu menu, Origins origin, IIdentified originData, Action callback = null)
        {
            menu.AddItem(Content, Value, () =>
            {
                SetValue(!Value, origin, originData, callback);
            });
        }

        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
        {
            UIHelpers.DrawToggle(() => Value, value => { SetValue(value, origin, originData, callback); }, Content);
        }

        public void DrawForGUI(Origins origin, IIdentified originData, Action callback, bool toggleLeft)
            => UIHelpers.DrawToggle(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content, toggleLeft);

        public struct DrawOptions
        {
            public Origins origin;
            public IIdentified originData;
            public Action callback;
            public bool OnLeft;
            public bool Inverted;
            public GUIContent Content;
        }

        public void DrawForGUI(DrawOptions options)
            => UIHelpers.DrawToggle(() => options.Inverted ? !Value : Value, value =>
            {
                SetValue(options.Inverted ? !value : value, options.origin, options.originData, options.callback);
            }, options.Content, options.OnLeft);
    }

    internal class UserBool : CustomBool
    {
        public UserBool()
        {
            Get = () => EditorPrefs.GetBool(Key, string.IsNullOrEmpty(OldKey) ? Default : EditorPrefs.GetBool(OldKey, Default));
            Set = value => EditorPrefs.SetBool(Key, value);
        }
    }

    internal class CustomInt : CustomSetting<int>
    {
        protected override void DrawForMenuImplementation(GenericMenu menu, Origins origin, IIdentified originData, Action callback = null)
        {
        }

        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawIntField(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class UserInt : CustomInt
    {
        public UserInt()
        {
            Get = () => EditorPrefs.GetInt(Key, string.IsNullOrEmpty(OldKey) ? Default : EditorPrefs.GetInt(OldKey, Default));
            Set = value => EditorPrefs.SetInt(Key, value);
        }
    }

    internal class CustomLayer : CustomInt
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawLayerField(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class CustomFloat : CustomSetting<float>
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawFloatField(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class UserFloat : CustomFloat
    {
        public UserFloat()
        {
            Get = () => EditorPrefs.GetFloat(Key, string.IsNullOrEmpty(OldKey) ? Default : EditorPrefs.GetFloat(OldKey, Default));
            Set = value => EditorPrefs.SetFloat(Key, value);
        }
    }

    internal class CustomEnum<T> : CustomSetting<T>
        where T : Enum
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawPopup(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class CustomFlags<T> : CustomEnum<T>
        where T : Enum
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawFlagsPopup<T>(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class CustomString : CustomSetting<string>
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawTextField(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class UserString : CustomString
    {
        public UserString()
        {
            Get = () => EditorPrefs.GetString(Key, string.IsNullOrEmpty(OldKey) ? Default : EditorPrefs.GetString(OldKey, Default));
            Set = value => EditorPrefs.SetString(Key, value);
        }
    }

    internal class UserPassword : UserString
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawPasswordField(() => Value, value =>
            {
                SetValue(value, origin, originData, callback);
            }, Content);
    }

    internal class CustomObject<T> : CustomSetting<T>
        where T : Object
    {
        protected override void DrawForGUIImplementation(Origins origin, IIdentified originData, Action callback = null)
            => UIHelpers.DrawObjectField(() => Value, value =>
            {
                SetValue(value as T, origin, originData, callback);
            }, Content, typeof(T));
    }

    internal class OnlyOncePerSessionBool : CustomBool
    {
        public OnlyOncePerSessionBool()
        {
            Get = OnlyOnce;
            Set = null;
        }

        private static readonly HashSet<string> OnlyOnceSettings = new();

        private CustomFloat _previousTimestamp;
        private CustomFloat PreviousTimestamp => _previousTimestamp ??= new UserFloat
        {
            Owner = this,
            Uid = $"{Uid}_timestamp",
            Default = 0.0f,
            SendTelemetry = false
        };

        private CustomFloat _previousTimeSinceStartup;
        private CustomFloat PreviousTimeSinceStartup => _previousTimeSinceStartup ??= new UserFloat
        {
            Owner = this,
            Uid = $"{Uid}_timesincestartup",
            Default = 0.0f,
            SendTelemetry = false
        };

        public override void Reset()
        {
            base.Reset();

            OnlyOnceSettings.Remove(Uid);
            PreviousTimestamp.Reset();
            PreviousTimeSinceStartup.Reset();
        }

        private bool OnlyOnce()
        {
            if (OnlyOnceSettings.Contains(Uid))
            {
                // If the tuple was found, this means we already went through this test
                // So either it was the first time the previous time
                // or it wasn't the first time...so neither is current time
                return false;
            }

            // From now on, we can be sure next time won't be the first
            // So we add it to the HashSet
            OnlyOnceSettings.Add(Uid);

            var currentTimestamp = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
            // Better timestamp would be absolute number of seconds
            // But this is too big to be stored in float or int, and EditorPrefs do not allow double or long.
            var currentTimeSinceStartup = (float)EditorApplication.timeSinceStartup;

            // The tuple was not found, so this is the first time we're testing it
            // since last compile
            // We're getting the previous saved timestamps
            var timeStampDiff = currentTimestamp - PreviousTimestamp.Value;
            var timeSinceStartupDiff = currentTimeSinceStartup - PreviousTimeSinceStartup.Value;

            // If the difference between timestamps is very close
            // to the difference between timeSinceStartups
            // Then we're very probably on the same instance of the Editor
            const float closeThreshold = 5.0f;
            if (Mathf.Abs(timeSinceStartupDiff - timeStampDiff) < closeThreshold)
            {
                return false;
            }

            // And we're storing the current timestamp for next time
            PreviousTimestamp.SetValue(currentTimestamp);
            PreviousTimeSinceStartup.SetValue(currentTimeSinceStartup);
            return true;
        }
    }
}
