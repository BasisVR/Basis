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

#if FUSION_WEAVER && FUSION2
using System.Linq;
using Fusion;
using Fusion.Editor;
using UnityEditor;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    [InitializeOnLoad]
    internal static class FusionBlockSetupRules
    {
        private const string FusionBbAssemblyName = "Meta.XR.MultiplayerBlocks.Fusion";
        static FusionBlockSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ => NetworkProjectConfig.Global.AssembliesToWeave.Contains(FusionBbAssemblyName),
            message:
                "When using Fusion in your project it's required to add the blocks assembly to Fusion AssembliesToWeave",
                fix: _ =>
                {
                    var current = NetworkProjectConfig.Global.AssembliesToWeave;
                    NetworkProjectConfig.Global.AssembliesToWeave = new string[current.Length + 1];
                    for (var i = 0; i < current.Length; i++)
                    {
                        NetworkProjectConfig.Global.AssembliesToWeave[i] = current[i];
                    }
                    NetworkProjectConfig.Global.AssembliesToWeave[current.Length] = FusionBbAssemblyName;
                    NetworkProjectConfigUtilities.SaveGlobalConfig();
                },
                fixMessage: $"Add the blocks assembly {FusionBbAssemblyName} to Fusion project config's AssembliesToWeave"
            );
        }
    }
}
#endif // FUSION_WEAVER && FUSION2
