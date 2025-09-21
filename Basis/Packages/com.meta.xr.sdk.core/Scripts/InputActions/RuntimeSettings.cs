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

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

#if UNITY_EDITOR
using UnityEditor;
using Meta.XR.Editor.Callbacks;
#endif // UNITY_EDITOR

using UnityEngine;
using UnityEngine.Serialization;

namespace Meta.XR.InputActions
{
    public class RuntimeSettings : OVRRuntimeAssetsBase
    {
        [Tooltip("A list of input action definitions, which define how certain input values can be obtained from third party devices.")]
        public List<UserInputActionSet> InputActionDefinitions = new List<UserInputActionSet>();
        [Tooltip("Allows for the inclusion of Input Actions defined in an InputActionSet Serializable Object, such as those provided in third party device samples.")]
        public List<InputActionSet> InputActionSets = new List<InputActionSet>();

        internal static string InstanceAssetName = "InputActions";
        private static RuntimeSettings _instance;
        public static RuntimeSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    LoadAsset(out RuntimeSettings debugTypes, InstanceAssetName);
                    _instance = debugTypes;
                }

                return _instance;
            }
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        static void RegisterPlaymodeListener()
        {
            static void RegisterListener()
            {
                UpdateBindingsOnDisk(clean: true);

                EditorApplication.playModeStateChanged += state =>
                {
                    switch (state)
                    {
                        case PlayModeStateChange.ExitingEditMode:
                            UpdateBindingsOnDisk(supportPlaymode: true);
                            break;
                        case PlayModeStateChange.EnteredEditMode:
                            UpdateBindingsOnDisk(clean: true);
                            break;
                    }
                };
            }

            // Registering this after the safe InitializeOnLoad to avoid
            // singleton access of InputActions.asset before it's imported.
            InitializeOnLoad.Register(RegisterListener);
        }

        public static string GetRuntimeActionBindings()
        {
            var instance = Instance;
            if( instance == null )
            {
                return "";
            }
            // I'd prefer to use a json serializer for this whole block, but the ones we currently use don't
            // play nicely with SerializedObjects so I'm manually building this.
            StringBuilder builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"InputActionDefinitions\": [");
            if (instance.InputActionDefinitions != null)
            {
                for (int i = 0; i < instance.InputActionDefinitions.Count; i++)
                {
                    // These ToString methods use a json serializer, which sanitizes the strings.
                    builder.Append($"{instance.InputActionDefinitions[i]}");
                    if (i < instance.InputActionDefinitions.Count - 1)
                    {
                        builder.Append(",");
                    }
                }
            }
            builder.Append("],");
            builder.Append("\"InputActionSets\": [");
            if (instance.InputActionSets != null)
            {
                for (int i = 0; i < instance.InputActionSets.Count; i++)
                {
                    // These ToString methods use a json serializer, which sanitizes the strings.
                    builder.Append($"{instance.InputActionSets[i]}");
                    if (i < instance.InputActionSets.Count - 1)
                    {
                        builder.Append(",");
                    }
                }
            }
            builder.Append("]");
            builder.Append("}");
            string str = builder.ToString();
            str = Regex.Replace(str, "[\r\n]+", "\n");
            return str;
        }

        public static void UpdateBindingsOnDisk(bool supportPlaymode = false, bool clean = false, string buildPath = null)
        {
            const string kStreamedPath = "Assets/StreamingAssets"; // avoiding Application.streamingAssetsPath since we're 100% certain we want this exact path, always
            const string kFileName = "RuntimeActionBindings.json";
            const string kNewline = "\n";

            var src = Instance;

            var streamedFile = new FileInfo($"{kStreamedPath}/{kFileName}"); // Application.streamingAssetsPath
            var streamedMeta = new FileInfo($"{streamedFile}.meta");         // always delete the meta file too
            var playmodeFile = new FileInfo($"./{kFileName}");               // Editor runtime CWD

            if (!string.IsNullOrEmpty(buildPath) && streamedFile.Exists)
                streamedFile.CopyTo($"{buildPath}/../{kFileName}");

            foreach (var file in new [] { streamedFile, streamedMeta, playmodeFile })
            {
                try
                {
                    if (file.Exists)
                        file.Delete();
                }
                catch (IOException dismissible)
                {
                    // these exceptions shouldn't halt procedure, but we should log the details anyway.
                    Debug.LogFormat(
                        LogType.Warning, // don't log as LogType.Exception ~ it might inadvertently halt in certain ctxs
                        LogOption.None,
                        context: src,
                        format: "{0}",
                        dismissible
                    );
                    // (this likely foretells a later failure tho.)
                }
                // SecurityException and UnauthorizedAccessException are exotic cases at best and should bubble up.
            }

            if (clean)
                return;

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var bindings = GetRuntimeActionBindings();

            streamedFile.Directory?.Create();
            using (var file = new StreamWriter(streamedFile.Open(FileMode.Create, FileAccess.Write, FileShare.None), encoding))
            {
                file.NewLine = kNewline;
                file.Write(bindings);
            }

            if (supportPlaymode)
                streamedFile.CopyTo($"{playmodeFile}", overwrite: true);

            // (Allow any exceptions above to bubble up so builds fail and buildmasters get a clear signal.)
            // ((Reason: If an app depends on these input bindings, then it's a broken build without them.))
        }
#endif // UNITY_EDITOR
    }

    [System.Serializable]
    public class UserInputActionSet
    {
        /// <summary>
        /// The Interaction Profile these actions should be used with.
        /// See the OpenXR spec for info on Interaction Profiles and the Action system in general.
        /// </summary>
        [InlineLink("https://registry.khronos.org/OpenXR/specs/1.0/html/xrspec.html#semantic-path-interaction-profiles")]
        [Tooltip("The interaction profile of the device these actions should be applied to.")]
        public string InteractionProfile;

        /// <summary>
        /// A list of Input Actions.
        /// </summary>
        [Tooltip("A list of the different Input Actions that this device supports.")]
        public List<InputActionDefinition> InputActionDefinitions = new List<InputActionDefinition>();

        public override string ToString()
        {
            return JsonUtility.ToJson(this);
        }
    }

    [System.Serializable]
    public class InputActionDefinition
    {
        /// <summary>
        /// The name of the action, to be used in conjuction with OVRPlugin.GetActionStateBoolean and other such functions.
        /// </summary>
        [Tooltip("The name of this action. This is used in functions like OVRPlugin.GetActionStateBoolean to identify this specific action.")]
        public string ActionName;

        /// <summary>
        /// The action type.
        /// </summary>
        [Tooltip("The type of this action. Does it return a bool, pose, vector2, float or trigger a vibration?")]
        public OVRPlugin.ActionTypes Type;

        /// <summary>
        /// Paths determine how this data is retrieved within the target controller.
        /// For most devices, this should include a left & right hand path.
        /// </summary>
        [Tooltip("Paths: the path from where this action will get its data. This is based on the OpenXR specification for the device.")]
        [FormerlySerializedAs("Path")]
        public string[] Paths;
    }
}
