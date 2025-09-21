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

namespace Meta.XR.Guides.Editor
{
    /// <summary>
    /// Create a Meta XR Guide editor window
    /// </summary>
    internal class Guide
    {
        internal static Dictionary<int, GuideWindow> GuideWindowIntances = new();

        /// <summary>
        /// This will create a <see cref="GuideWindow"/> type of <see cref="EditorWindow"/>.
        /// </summary>
        /// <param name="title">Title of the window.</param>
        /// <param name="description">A brief description of this guide.</param>
        /// <param name="populator">A reference to the IIDentified class that owns the GetItems and Init methods.</param>
        /// <remarks>
        /// The <paramref name="populator"/> referenced class needs to have the <see cref="GuideItemsAttribute"/> attribute
        /// in order to correctly repaint the Guide window on Unity's domain reload.
        /// <example>
        /// <code>
        /// <![CDATA[
        /// [GuideItems]
        /// public class Foo
        /// {
        ///     public static Foo()
        ///     {
        ///         Guide.Create("Foo title", "Bar desc.", this).Show();
        ///     }
        ///
        ///     [Init]
        ///     public static void Init(GuideWindow guideWindow)
        ///     {
        ///         // Initialize the window, register callbacks
        ///     }
        ///
        ///     [GuideItems]
        ///     public static List<IUserInterfaceItem> GetItems()
        ///     {
        ///         return new List<IUserInterfaceItem>();
        ///     }
        /// }
        /// ]]>
        /// </code>
        /// </example>
        /// </remarks>
        /// <returns>Returns a <see cref="GuideWindow"/>.</returns>
        public static GuideWindow Create(string title, string description, IIdentified populator) =>
            Create(title, description, populator, GuideWindow.DefaultOptions);

        /// <summary>
        /// This will create a <see cref="GuideWindow"/> type of <see cref="EditorWindow"/>.
        /// </summary>
        /// <param name="title">Title of the window.</param>
        /// <param name="description">A brief description of this guide.</param>
        /// <param name="populator">A reference to the IIDentified class that owns the GetItems and Init methods.</param>
        /// <param name="guideOptions">Takes a <see cref="GuideWindow.GuideOptions"/>.</param>
        /// <remarks>
        /// The <paramref name="populator"/> referenced class needs to have the <see cref="GuideItemsAttribute"/> attribute
        /// in order to correctly repaint the Guide window on Unity's domain reload.
        /// <example>
        /// <code>
        /// <![CDATA[
        /// [GuideItems]
        /// public class Foo
        /// {
        ///     public static Foo()
        ///     {
        ///         Guide.Create("Foo title", "Bar desc.", this).Show();
        ///     }
        ///
        ///     [Init]
        ///     public static void Init(GuideWindow guideWindow)
        ///     {
        ///         // Initialize the window, register callbacks
        ///     }
        ///
        ///     [GuideItems]
        ///     public static List<IUserInterfaceItem> GetItems()
        ///     {
        ///         return new List<IUserInterfaceItem>();
        ///     }
        /// }
        /// ]]>
        /// </code>
        /// </example>
        /// </remarks>
        /// <returns>Returns a <see cref="GuideWindow"/>.</returns>
        public static GuideWindow Create(
            string title,
            string description,
            IIdentified populator,
            GuideWindow.GuideOptions guideOptions)
        {
            if (GuideWindowIntances.TryGetValue(GetGuideHash(title, description), out var window))
                return window;

            window = ScriptableObject.CreateInstance<GuideWindow>();
            window.Setup(title, description, populator, guideOptions);
            return window;
        }

        internal static int GetGuideHash(string title, string description) => (title + description).GetHashCode();
    }
}
