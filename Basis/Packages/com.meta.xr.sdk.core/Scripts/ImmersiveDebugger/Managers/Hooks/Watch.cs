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


using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Meta.XR.ImmersiveDebugger.UserInterface;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal static class WatchUtils
    {
        internal static readonly Dictionary<Type, Type> Types = new Dictionary<Type, Type>();

        private const int MaxLetterCount = 64;

        static WatchUtils()
        {
            Types?.Clear(); // reset static fields in case of domain reload disabled
            Register<float>((float value, ref string[] valuesContainer) =>
            {
                valuesContainer[0] = FormatFloat(value);
            }, 1);
            Register<bool>((bool value, ref string[] valuesContainer) =>
            {
                valuesContainer[0] = value ? "True" : "False";
            }, 1);
            Register<Vector3>((Vector3 value, ref string[] valuesContainer) =>
            {
                valuesContainer[0] = FormatFloat(value.x);
                valuesContainer[1] = FormatFloat(value.y);
                valuesContainer[2] = FormatFloat(value.z);
            }, 3);
            Register<Vector2>((Vector2 value, ref string[] valuesContainer) =>
            {
                valuesContainer[0] = FormatFloat(value.x);
                valuesContainer[1] = FormatFloat(value.y);
            }, 2);
            Register<string>((string value, ref string[] valuesContainer) =>
            {
                valuesContainer[0] = value is { Length: > MaxLetterCount } ? $"{value[..MaxLetterCount]}..." : value;
            }, 1);

            RegisterTexture(typeof(Texture2D));
        }

        public static Watch Create(MemberInfo memberInfo, InstanceHandle instanceHandle, DebugMember attribute)
        {
            var type = memberInfo.GetDataType();
            if (!Types.TryGetValue(type, out var createdType))
            {
#if !UNITY_2022_1_OR_NEWER
                createdType = typeof(WatchShared);
#else
                createdType = Register(type);
#endif
            }

            return Activator.CreateInstance(createdType, memberInfo, instanceHandle, attribute) as Watch;
        }

        internal static string FormatFloat(float value)
        {
            return value is > -10000000 and < 10000000 ? value.ToString("0.00", CultureInfo.InvariantCulture) :
                value.ToString("g3", CultureInfo.InvariantCulture);
        }

        private static Type Register<T>(Watch<T>.ToDisplayStringSignature toDisplayString, int numberOfValues)
        {
            Watch<T>.Setup(toDisplayString, numberOfValues);
            var createdType = typeof(Watch<T>);
            Types.Add(typeof(T), createdType);
            return createdType;
        }

        private static Type Register(Type type)
        {
            var genericType = typeof(Watch<>);
            var createdType = genericType.MakeGenericType(type);
            Types.Add(type, createdType);
            return createdType;
        }

        private static Type RegisterTexture(Type type)
        {
            var createdType = typeof(WatchTexture);
            Types.Add(type, createdType);
            return createdType;
        }
    }

    internal abstract class Watch : Hook
    {
        public abstract string Value { get; }
        public abstract string[] Values { get; }
        public abstract int NumberOfValues { get; }

        protected Watch(MemberInfo memberInfo, InstanceHandle instanceHandle, DebugMember attribute) : base(memberInfo, instanceHandle, attribute) { }
    }

#if !UNITY_2022_1_OR_NEWER
    // Before Unity 2022.1 there's no IL2CPP Full Generic Sharing feature so Watch<> would require "Faster(smaller) Build"
    // in IL2CPP Code Generation Build Settings to avoid IL2CPP Ahead Of Time (AOT) codegen error in runtime.
    // Separate class are created here for other non-registered types so in runtime Watch<> is not accessed by types without codegen.
    internal class WatchShared : Watch
    {
        private static string[] _buffer = new string[1];
        public override int NumberOfValues => 1;
        private string[] ToDisplayStrings()
        {
            var value = _memberInfo.GetValue(_instance);
            _buffer[0] = value != null ? value.ToString() : "";
            return _buffer;
        }

        public override string[] Values => ToDisplayStrings();
        public override string Value => Values[0];

        public WatchShared(MemberInfo memberInfo, InstanceHandle instanceHandle, DebugMember attribute) : base(memberInfo, instanceHandle, attribute) { }
    }
#endif

    internal class Watch<T> : Watch
    {
        public delegate void ToDisplayStringSignature(T value, ref string[] valuesContainer);
        public static ToDisplayStringSignature ToDisplayStringsDelegate { get; private set; } = null;
        public static int NumberOfDisplayStrings { get; private set; } = 1;
        private static string[] _buffer = new string[1];

        public override int NumberOfValues => NumberOfDisplayStrings;

        internal static void ResetBuffer()
        {
            _buffer = new string[NumberOfDisplayStrings];
        }

        public static void Setup(ToDisplayStringSignature del, int numberOfValues)
        {
            ToDisplayStringsDelegate = del;
            NumberOfDisplayStrings = numberOfValues;
            ResetBuffer();
        }

        public static string[] ToDisplayStrings(T value)
        {
            if (ToDisplayStringsDelegate != null)
            {
                ToDisplayStringsDelegate.Invoke(value, ref _buffer);
            }
            else
            {
                _buffer[0] = value != null ? value.ToString() : "";
            }
            return _buffer;
        }
        private readonly Func<T> _getter;

        public override string[] Values => ToDisplayStrings(_getter.Invoke());
        public override string Value => Values[0];

        public Watch(MemberInfo memberInfo, InstanceHandle instanceHandle, DebugMember attribute) : base(memberInfo, instanceHandle, attribute)
        {
            _getter = () => (T)memberInfo.GetValue(_instance);
        }
    }

    internal class WatchTexture : Watch
    {
        public WatchTexture(MemberInfo memberInfo, InstanceHandle instanceHandle, DebugMember attribute) : base(memberInfo, instanceHandle, attribute)
        {
            _getter = () => (Texture2D)memberInfo.GetValue(_instance);
        }

        private readonly Func<Texture2D> _getter;
        public Texture2D Texture => _getter.Invoke();
        public override string Value => string.Empty;
        public override string[] Values => Array.Empty<string>();
        public override int NumberOfValues => 0;
    }
}

