using System;
using System.Reflection;
using UnityEngine.UIElements;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Assigns tracked pointer identity to UI Toolkit pointer events.
    ///
    /// UI Toolkit models XR pointers (<see cref="PointerId.trackedPointerIdBase"/>,
    /// <see cref="PointerType.tracked"/>) but exposes no public way to construct one — every
    /// public GetPooled overload derives its id from a mouse, touch or pen. The setters exist
    /// and are stable, so they are bound ONCE per event type into a cached delegate: no
    /// per-frame reflection, no allocation on the dispatch path.
    ///
    /// Binding failure is not fatal. If a future Unity renames these, <see cref="Supported"/>
    /// goes false and callers fall back to a single pen pointer — hover and click keep working,
    /// only per-device independence is lost.
    /// </summary>
    public static class BasisUIToolkitPointerIdentity
    {
        public static bool Supported => Probe<PointerDownEvent>.SetPointerId != null;

        public static void Apply<T>(T pointerEvent, int pointerId) where T : PointerEventBase<T>, new()
        {
            Probe<T>.SetPointerId?.Invoke(pointerEvent, pointerId);
            Probe<T>.SetPointerType?.Invoke(pointerEvent, PointerType.tracked);
        }

        private static class Probe<T> where T : PointerEventBase<T>, new()
        {
            internal static readonly Action<T, int> SetPointerId = CreateSetter<T, int>("pointerId");
            internal static readonly Action<T, string> SetPointerType = CreateSetter<T, string>("pointerType");
        }

        private static Action<TTarget, TValue> CreateSetter<TTarget, TValue>(string propertyName)
        {
            Type type = typeof(TTarget);
            while (type != null)
            {
                PropertyInfo property = type.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                MethodInfo setter = property?.GetSetMethod(true);
                if (setter != null && setter.GetParameters().Length == 1 && setter.GetParameters()[0].ParameterType == typeof(TValue))
                {
                    return (Action<TTarget, TValue>)Delegate.CreateDelegate(typeof(Action<TTarget, TValue>), setter, false);
                }

                type = type.BaseType;
            }

            return null;
        }
    }
}
