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

using UnityEngine;

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// Static container class of utility methods used at runtime and related to <see cref="BuildingBlock"/>s.
    /// See the [Multiplayer Building Blocks Setup Guide](https://developer.oculus.com/documentation/unity/bb-multiplayer-blocks) for more information.
    /// This class features the <see cref="RunTimeUtils.GetInterfaceComponent{T}"/> method,
    /// which you can use to query for <see cref="MonoBehaviour"/>s on a <see cref="GameObject"/>
    /// that implement a particular namespace.
    /// </summary>
    public static class RunTimeUtils
    {
        /// <summary>
        /// Returns the first (order not guaranteed) instance of a <see cref="MonoBehaviour"/> implementing the
        /// interface <see cref="T"/>.
        /// </summary>
        /// <param name="monoBehaviour"><see cref="MonoBehaviour"/> instance, as caller, on whose
        /// <see cref="GameObject"/> the search will occur.</param>
        /// <typeparam name="T">Interface type expected</typeparam>
        /// <returns>The first instance of <see cref="T"/> found.</returns>
        public static T GetInterfaceComponent<T>(this MonoBehaviour monoBehaviour) where T : class
        {
            foreach (var component in monoBehaviour.GetComponents<MonoBehaviour>())
            {
                if (component is T interfaceComponent)
                {
                    return interfaceComponent;
                }
            }

            return null;
        }

        public static string GenerateRandomString(int size, bool includeLowercase = true, bool includeUppercase = true, bool includeNumeric = true, bool includeSpecial = false)
        {
            const string lowercase = "abcdefghijklmnopqrstuvwxyz";
            const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string numeric = "0123456789";
            const string special = "!@#$%^&*()_-+=[{]};:<>|./?";
            var charSet = "";
            if (includeLowercase) charSet += lowercase;
            if (includeUppercase) charSet += uppercase;
            if (includeNumeric) charSet += numeric;
            if (includeSpecial) charSet += special;
            var stringChars = new char[size];
            for (var i = 0; i < size; i++)
            {
                stringChars[i] = charSet[Random.Range(0, charSet.Length)];
            }
            return new string(stringChars);
        }
    }
}
