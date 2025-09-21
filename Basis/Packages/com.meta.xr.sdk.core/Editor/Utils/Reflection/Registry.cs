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
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Meta.XR.Editor.Reflection
{
    internal static class Registry
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy;

        private static IReadOnlyList<(MemberInfo, ReflectionAttribute)> _list;
        public static IReadOnlyList<(MemberInfo, ReflectionAttribute)> List = _list ??= BuildList();

        public static void Reset()
        {
            _list = null;
        }

        private static List<(MemberInfo, ReflectionAttribute)> BuildList()
        {
            var allowedAssemblyNames = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(attribute => attribute.AssemblyName)
                .ToList();

            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => allowedAssemblyNames.Contains(assembly.GetName().Name))
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.GetCustomAttribute<ReflectionAttribute>() != null)
                .SelectMany(type => type.GetMembers(Flags))
                .Select(member => (member, member.GetCustomAttribute<ReflectionAttribute>(true)))
                .Where(tuple => tuple.Item2 != null)
                .ToList();
        }

        public static Handle GetHandle(this MemberInfo memberInfo)
        {
            var memberType = memberInfo.MemberType;
            if ((memberType & MemberTypes.Field) != 0)
            {
                return ((FieldInfo)memberInfo).GetValue(null) as Handle;
            }
            if ((memberType & MemberTypes.Property) != 0)
            {
                return ((PropertyInfo)memberInfo).GetValue(null) as Handle; ;

            }

            return null;
        }

        public static ReflectionAttribute FindAttribute(Handle handle)
            => List.FirstOrDefault(tuple => tuple.Item1.GetHandle() == handle).Item2;
    }
}
