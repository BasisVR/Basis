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
using System.Threading.Tasks;
using UnityEngine;

#if USING_META_XR_VOICE_SDK
using Meta.WitAi.TTS;
#endif

namespace Meta.XR.BuildingBlocks.Editor
{
    public class TextToSpeechBlockData : Meta.XR.BuildingBlocks.Editor.BlockData
    {
        protected override bool UsesPrefab => false;

#pragma warning disable CS1998
        protected override async Task<List<GameObject>> InstallRoutineAsync(GameObject selectedGameObject)
        {
#if USING_META_XR_VOICE_SDK
            await VoiceBlocksUtils.GetWitConfig();
            var instance = TTSEditorUtilities.CreateDefaultSetup().gameObject;
            instance.name = $"{Utils.BlockPublicTag} {BlockName}";
            return new List<GameObject> { instance };
#else
            throw new InstallationCancelledException("It's required to install the Voice SDK package to use this component");
#endif // USING_META_XR_VOICE_SDK
        }
#pragma warning restore CS1998
    }
}
