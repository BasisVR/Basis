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
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    public class VoiceChatInterfaceBlockData : InterfaceBlockData
    {
        internal override async Task<List<GameObject>> InstallWithDependencies(GameObject selectedGameObject = null)
        {
            try
            {
                return await base.InstallWithDependencies(selectedGameObject);
            }
            catch (Exception e)
            {
                if (e.GetType() == typeof(MissingInstallationRoutineException))
                {
                    EditorUtility.DisplayDialog("Unable to install Player Voice Chat block",
                        "This block cannot be installed.\nIt could be because there's Unity Netcode for Game Objects " +
                        "based blocks in the scene.\nThis block only supports Photon Fusion and is incompatible.", "Ok");
                }
                else
                {
                    throw;
                }
            }
            return new List<GameObject>();
        }
    }
}
