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
#if META_AVATAR_SDK_DEFINED
using Oculus.Avatar2;
#endif // META_AVATAR_SDK_DEFINED
#if PHOTON_VOICE_DEFINED
using Photon.Voice;
using Photon.Voice.Unity;
#endif // PHOTON_VOICE_DEFINED

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// A helper class to ensure the correct behaviour of lip sync with the Meta Avatars when using the Photon Fusion
    /// networking framework.
    /// For the setup of lip sync and networked voice using Photon Fusion, see <see cref="VoiceSetup"/>.
    /// </summary>
#if PHOTON_VOICE_DEFINED
    [RequireComponent(typeof(Recorder))]
#endif // PHOTON_VOICE_DEFINED
    public class LipSyncPhotonFix : MonoBehaviour
    {
#if META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED
        private OvrAvatarLipSyncContext lipsync;
        private Recorder recorder;

        private void Awake()
        {
            lipsync = FindObjectOfType<OvrAvatarLipSyncContext>();

            if (lipsync != null)
            {
                lipsync.SetAudioSourceType(LipSyncAudioSourceType.Manual);
                lipsync.CaptureAudio = false;
                recorder = GetComponent<Recorder>();
                recorder.SamplingRate = POpusCodec.Enums.SamplingRate.Sampling48000;
            }
        }

        private void PhotonVoiceCreated(PhotonVoiceCreatedParams photonVoiceCreatedParams)
        {
            if (lipsync != null)
            {
                VoiceInfo voiceInfo = photonVoiceCreatedParams.Voice.Info;

                if (photonVoiceCreatedParams.Voice is LocalVoiceAudioFloat)
                {
                    LocalVoiceAudioFloat localVoiceAudioFloat = photonVoiceCreatedParams.Voice as LocalVoiceAudioFloat;
                    localVoiceAudioFloat.AddPostProcessor(new ProcessVoiceDataToLipsync(lipsync));
                }
            }
        }
#endif // META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED
    }

#if META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED
    internal class ProcessVoiceDataToLipsync : IProcessor<float>
    {
        private const int bitsPerSample = 32;
        private OvrAvatarLipSyncContext lipsync;

        public ProcessVoiceDataToLipsync(OvrAvatarLipSyncContext lipsync)
        {
            this.lipsync = lipsync;
        }

        public float[] Process(float[] buf)
        {
            lipsync.ProcessAudioSamples(buf, bitsPerSample);
            return buf;
        }

        public void Dispose()
        {
        }
    }
#endif // META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED
}
