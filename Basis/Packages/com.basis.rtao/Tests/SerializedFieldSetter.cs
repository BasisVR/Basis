using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public static class SerializedFieldSetter
    {
        public static void Set(Object target, string fieldName, object value)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(field, $"{target.GetType().Name} has no field named {fieldName}.");
            field.SetValue(target, value);
            EditorUtility.SetDirty(target);
        }

        public static T Get<T>(Object target, string fieldName)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(field, $"{target.GetType().Name} has no field named {fieldName}.");
            return (T)field.GetValue(target);
        }
    }
}
