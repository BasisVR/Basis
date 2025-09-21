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
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace Meta.XR.Editor.Reflection
{
    internal abstract class Handle
    {
        public TypeHandle Type;
        protected bool Processed;
        protected ReflectionAttribute Attribute;

        protected string TargetName => Attribute?.Name;

        public const BindingFlags TargetFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy;

        public abstract bool Valid { get; }
    }

    internal abstract class Handle<T> : Handle
    {
        protected T _target;
        protected Type TargetType => Type.Target;

        public override bool Valid => Target != null;

        public T Target
        {
            get
            {
                if (!Processed) Process();

                return _target;
            }
        }

        protected virtual void Process()
        {
            Processed = true;

            Attribute ??= Registry.FindAttribute(this);


            if (Attribute == null) return;

            Type = new TypeHandle(Attribute);


            if (!HasValidAttribute) return;

            _target = Fetch();

        }

        protected abstract T Fetch();

        protected virtual bool HasValidAttribute => !string.IsNullOrEmpty(TargetName) && TargetType != null;

        public string BuildError(string errorMessage)
         => $"This reflection usage {{{GetType().Name} {Attribute?.ToString()}}} is invalid : {errorMessage}.";

    }

    internal class PropertyInfoHandle : Handle<PropertyInfo>
    {
        protected override PropertyInfo Fetch() => TargetType.GetProperty(TargetName, TargetFlags);
    }

    internal class PropertyInfoHandle<TPropertyType> : PropertyInfoHandle
    {
        public TPropertyType Get(object owner) => (TPropertyType)(Target?.GetValue(owner));
        public void Set(object owner, TPropertyType value) => Target?.SetValue(owner, value);
    }

    internal class FieldInfoHandle : Handle<FieldInfo>
    {
        protected override FieldInfo Fetch() => TargetType.GetField(TargetName, TargetFlags);
    }

    internal class FieldInfoHandle<TFieldType> : FieldInfoHandle
    {
        public TFieldType Get(object owner) => (TFieldType)(Target?.GetValue(owner));
        public void Set(object owner, TFieldType value) => Target?.SetValue(owner, value);
    }

    internal class MethodInfoHandle : Handle<MethodInfo>
    {
        protected override MethodInfo Fetch() => TargetType.GetMethod(TargetName, TargetFlags);
    }

    internal class StaticMethodInfoHandle<TDelegateType> : MethodInfoHandle
        where TDelegateType : System.Delegate
    {
        private TDelegateType _staticDelegate;

        public TDelegateType Invoke
        {
            get
            {
                if (!Processed) Process();

                return _staticDelegate;
            }
        }
        public override bool Valid => base.Valid && Invoke != null;

        protected virtual TDelegateType CreateDelegate() => (TDelegateType)Target?.CreateDelegate(typeof(TDelegateType));

        protected override void Process()
        {
            base.Process();

            if (Target == null) return;

            try
            {
                _staticDelegate = CreateDelegate();
            }
            catch (Exception)
            {
            }
        }
    }

    internal class StaticMethodInfoHandleWithWrapper<TOut> : StaticMethodInfoHandle<Func<TOut>>
    {
        protected override Func<TOut> CreateDelegate() => () => (TOut)Target?.Invoke(null, new object[] { });

    }

    internal class StaticMethodInfoHandleWithWrapper<T1, TOut> : StaticMethodInfoHandle<Func<T1, TOut>>
    {
        protected override Func<T1, TOut> CreateDelegate() => (_in) => (TOut)Target?.Invoke(null, new object[] { _in });
    }

    internal class StaticMethodInfoHandleWithWrapperAction<T1> : StaticMethodInfoHandle<Action<T1>>
    {
        protected override Action<T1> CreateDelegate() => (_in) => Target?.Invoke(null, new object[] { _in });
    }
}
