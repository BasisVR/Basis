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


using System.Collections;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="ScriptableObject"/> that's storing the style used by Immersive Debugger.
    /// Serves as the base class for the specific styles, providing common functionalities to manage the asset.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public abstract class Style : ScriptableObject
    {
        protected bool _instantiated;
        /// <summary>
        /// Whether the style object is instantiated or not (meaning the style is cloned as an instance).
        /// </summary>
        public bool Instantiated => _instantiated;

        private static string Path<T>() where T : Style => $"Styles/{typeof(T).Name}s/";
        /// <summary>
        /// Load and returns the default style of the asset.
        /// The default style is specified by the name of the asset (with "Default" suffix).
        /// </summary>
        /// <typeparam name="T">Type of the style</typeparam>
        /// <returns>The default style</returns>
        public static T Default<T>() where T : Style => Resources.Load<T>($"{Path<T>()}Default");
        /// <summary>
        /// Load the style with the specified name of the asset.
        /// </summary>
        /// <param name="name">String representing the name of the asset</param>
        /// <typeparam name="T">Type of the style</typeparam>
        /// <returns>The loaded style of given name</returns>
        public static T Load<T>(string name) where T : Style
        {
            return Resources.Load<T>($"{Path<T>()}{name}") ?? Default<T>();
        }

        /// <summary>
        /// Instantiate the style object (cloning as instance) and set the instantiated state to true.
        /// </summary>
        /// <param name="name">String representing the name of the asset</param>
        /// <typeparam name="T">Type of the style</typeparam>
        /// <returns>The instantiated style of given name</returns>
        public static T Instantiate<T>(string name) where T : Style
        {
            var instance = GameObject.Instantiate(Load<T>(name));
            instance._instantiated = true;
            return instance;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying)
            {
                var controllers = GameObject.FindObjectsByType<Controller>(FindObjectsSortMode.None);
                foreach (var controller in controllers)
                {
                    if (controller.LayoutStyle == this)
                    {
                        controller.StartCoroutine(RefreshLayout(controller));
                    }
                }
            }
        }

        private IEnumerator RefreshLayout(Controller controller)
        {
            yield return null;
            controller.RefreshLayout();
        }
#endif
    }
}

