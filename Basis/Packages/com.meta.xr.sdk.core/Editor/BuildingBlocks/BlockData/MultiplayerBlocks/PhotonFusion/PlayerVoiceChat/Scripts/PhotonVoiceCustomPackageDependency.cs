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

using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    /// <summary>
    /// Currently Photon Voice is not provided as a UPM package but a .unitypackage.
    /// We need to use macro to detect whether it's already installed and give developers
    /// custom instructions to get this package.
    /// </summary>
    [InitializeOnLoad]
    internal static class PhotonVoiceCustomPackageDependency
    {
        private const string PHOTON_VOICE_PACKAGE_DEP_ID = "com.exitgames.photonvoice";
        static PhotonVoiceCustomPackageDependency()
        {
            CustomPackageDependencyRegistry.RegisterCustomPackageDependency(PHOTON_VOICE_PACKAGE_DEP_ID, new CustomPackageDependencyInfo()
            {
                PackageDisplayName = "Photon Voice",
                IsPackageInstalled = () =>
                {
#if PHOTON_VOICE_DEFINED
                    return true;
#else
                    return false;
#endif // PHOTON_VOICE_DEFINED
                },
                InstallationInstructions = "You can find Photon Voice2 package from https://assetstore.unity.com/packages/tools/audio/photon-voice-2-130518"
            });
        }
    }
}
