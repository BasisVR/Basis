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
using System.Runtime.CompilerServices;
using UnityEngine;
namespace Meta.XR.ImmersiveDebugger.Utils
{
    /// <summary>
    /// MemberInfo extension for unifying code between FieldInfo and PropertyInfo
    /// </summary>
    internal static class MemberInfoExtensions
    {
        public static object GetValue(this MemberInfo memberInfo, object instance)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                return ((FieldInfo)memberInfo).GetValue(instance);
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                var property = (PropertyInfo)memberInfo;
                if (property.CanRead)
                {
                    return property.GetValue(instance);
                }
                Debug.LogWarning("Calling GetValue() from property cannot be read");
                return null;
            }
            Debug.LogWarning("Calling GetValue() from wrong member type, expect field/property");
            return null;
        }

        public static void SetValue(this MemberInfo memberInfo, object instance, object value)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                ((FieldInfo)memberInfo).SetValue(instance, value);
                return;
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                var property = (PropertyInfo)memberInfo;
                if (property.CanWrite)
                {
                    property.SetValue(instance, value);
                    return;
                }
                Debug.LogWarning("Calling SetValue() from property cannot be written");
                return;
            }
            Debug.LogWarning("Calling SetValue() from wrong member type, expect field/property");
        }

        public static Type GetDataType(this MemberInfo memberInfo)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                return ((FieldInfo)memberInfo).FieldType;
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                return ((PropertyInfo)memberInfo).PropertyType;
            }
            Debug.LogWarning("Calling GetDataType() from wrong member type, expect field/property");
            return null;
        }

        public static bool IsStatic(this MemberInfo memberInfo)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                return ((FieldInfo)memberInfo).IsStatic;
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                var property = (PropertyInfo)memberInfo;
                return property.CanRead && property.GetMethod.IsStatic ||
                       property.CanWrite && property.SetMethod.IsStatic;
            }
            if ((memberType & MemberTypes.Method) != 0)
            {
                var method = (MethodInfo)memberInfo;
                return method.IsStatic;
            }
            Debug.LogWarning("Calling IsStatic() from wrong member type, expect field/property");
            return false;
        }

        public static bool IsPublic(this MemberInfo memberInfo)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                return ((FieldInfo)memberInfo).IsPublic;
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                var property = (PropertyInfo)memberInfo;
                return property.CanRead && property.GetMethod.IsPublic ||
                       property.CanWrite && property.SetMethod.IsPublic;
            }
            if ((memberType & MemberTypes.Method) != 0)
            {
                var method = (MethodInfo)memberInfo;
                return method.IsPublic;
            }
            return false;
        }

        public static string BuildSignatureForDebugInspector(this MemberInfo memberInfo)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                var fieldInfo = (FieldInfo)memberInfo;
                var prefix = fieldInfo.IsPublic ? "public" :
                    fieldInfo.IsPrivate ? "private" :
                    fieldInfo.IsFamily ? "protected" : "internal";
                return $"<i>{prefix} {fieldInfo.FieldType.Name}</i> <b>{fieldInfo.Name}</b>";
            }
            if ((memberType & MemberTypes.Method) != 0)
            {
                var methodInfo = (MethodInfo)memberInfo;
                var prefix = methodInfo.IsPublic ? "public" :
                    methodInfo.IsPrivate ? "private" :
                    methodInfo.IsFamily ? "protected" : "internal";
                return $"<i>{prefix} {methodInfo.ReturnType.Name}</i> <b>{methodInfo.Name}</b>()";
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                var propertyInfo = (PropertyInfo)memberInfo;
                var getMethodInfo = propertyInfo.GetMethod;
                var prefix = getMethodInfo.IsPublic ? "public" :
                    getMethodInfo.IsPrivate ? "private" :
                    getMethodInfo.IsFamily ? "protected" : "internal";
                return $"<i>{prefix} {propertyInfo.PropertyType.Name}</i> <b>{propertyInfo.Name}</b>";
            }

            return memberInfo.Name;
        }

        public static bool IsCompatibleWithDebugInspector(this MemberInfo memberInfo)
        {
            if ((memberInfo as ConstructorInfo) != null)
            {
                return false;
            }

            var memberType = memberInfo.MemberType;
            if ((memberType & (MemberTypes.Method | MemberTypes.Property | MemberTypes.Field)) == 0)
            {
                return false;
            }

            if (memberInfo.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
            {
                return false;
            }

            if ((memberType & MemberTypes.Method) != 0)
            {
                var method = (MethodInfo)memberInfo;
                if (method.GetParameters().Length > 0 || method.ReturnType != typeof(void))
                {
                    return false;

                }
            }

            if (memberInfo is PropertyInfo { CanRead: false })
            {
                // There is no way to use any of our managers (Actions, Gizmos, Watch, Tweak)
                // if we cannot even read the property
                return false;
            }

            return true;
        }

        public static bool IsTypeEqual(this MemberInfo member, Type type) => (member as FieldInfo)?.FieldType == type ||
                                                                  (member as PropertyInfo)?.PropertyType == type;

        public static bool IsBaseTypeEqual(this MemberInfo member, Type type) => (member as FieldInfo)?.FieldType.BaseType == type ||
                                                                             (member as PropertyInfo)?.PropertyType.BaseType == type;

        public static bool CanBeChanged(this MemberInfo memberInfo) => (memberInfo.MemberType & (MemberTypes.Property | MemberTypes.Field)) != 0;
    }
}

