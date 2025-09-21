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
using Meta.XR.MultiplayerBlocks.Colocation;
using UnityEngine;
using UnityEngine.Events;
using Logger = Meta.XR.MultiplayerBlocks.Colocation.Logger;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Meta.XR.MultiplayerBlocks.Shared
{
    [Serializable]
    internal class ColocationDebuggingOptions
    {
        [SerializeField]
        [Tooltip("Show the alignment anchor with debug visual, colocated players " +
                                 "should be seeing the anchor at the same physical location.")]
#if UNITY_EDITOR
        [BooleanLeftToggle] // move toggle to the left to show long text
#endif
        internal bool visualizeAlignmentAnchor = true;
        [SerializeField]
        [Tooltip("Enable verbose logging to debug colocation process")]
#if UNITY_EDITOR
        [BooleanLeftToggle]
#endif
        internal bool enableVerboseLogging = false;
    }

    /// <summary>
    /// The class responsible for storing and applying the debugging options and callbacks for the Colocation
    /// functionality of the Meta Quest SDK. For more information on Colocation, see [Learn Mixed Reality Development through Discover Showcase](https://developer.oculus.com/documentation/unity/unity-learn-mixed-reality-through-discover/).
    /// </summary>
    /// <remarks>Currently there are implementations for Photon Fusion and Unity Netcode for Gameobjects
    /// for networking the Colocation state, but more can be added using this controller.</remarks>
    public class ColocationController : MonoBehaviour
    {
        /// <summary>
        /// An event triggered when the Colocation is ready to be used by the game.
        /// </summary>
        [SerializeField]
        public UnityEvent ColocationReadyCallbacks;
        [SerializeField]
        internal ColocationDebuggingOptions DebuggingOptions;

        public void Awake()
        {
            if (DebuggingOptions.enableVerboseLogging)
            {
                Logger.SetAllLogsVisibility(true);
            }
            else
            {
                // by default only show Errors
                Logger.SetAllLogsVisibility(false);
                Logger.SetLogLevelVisibility(LogLevel.Error, true);
                Logger.SetLogLevelVisibility(LogLevel.SharedSpatialAnchorsError, true);
            }
        }
    }

#if UNITY_EDITOR
    internal class BooleanLeftToggleAttribute : PropertyAttribute { }
    [CustomPropertyDrawer(typeof(BooleanLeftToggleAttribute))]
    internal class LeftAlignedToggleDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            property.boolValue = EditorGUI.ToggleLeft(position, label, property.boolValue);
            EditorGUI.EndProperty();
        }
    }
#endif
}
