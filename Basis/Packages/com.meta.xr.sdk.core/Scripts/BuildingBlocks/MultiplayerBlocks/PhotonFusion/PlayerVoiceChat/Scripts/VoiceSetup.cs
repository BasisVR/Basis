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
#if PHOTON_VOICE_DEFINED
using Fusion;
using Photon.Voice.Fusion;
using Photon.Voice.Unity;
using Photon.Voice.Unity.UtilityScripts;
using POpusCodec.Enums;
using System.Collections;
using System.Reflection;
#endif // PHOTON_VOICE_DEFINED

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// The class responsible for setting up the <see cref="Speaker"/> game object, with the requirements for having
    /// networked player voice over Photon Fusion and also lip sync for the Meta Avatars.
    /// For the full documentation of Voice Network over Photon Fusion, see https://doc.photonengine.com/voice/current/getting-started/voice-for-fusion#.
    /// </summary>
    public class VoiceSetup : MonoBehaviour
    {
        /// <summary>
        /// The transform of the center eye anchor of player, usually present in the player's <see cref="OVRCameraRig"/>.
        /// </summary>
        /// <remarks>Must be set before the game object's <c>Awake()</c> method is called.</remarks>
        public Transform centerEyeAnchor;

        /// <summary>
        /// The GameObject that will be set up and hold the speaker behaviours.
        /// </summary>
        /// <remarks>Must be set before the game object's <c>Awake()</c> method is called.</remarks>
        public GameObject Speaker { get; private set; }

#if PHOTON_VOICE_DEFINED
        private const uint CustomSpeakerPrefabID = 100000;

        private void Awake()
        {
            // compose speaker prefab programmatically so we don't have prefab with missing scripts
            // when project doesn't have Photon Voice package installed.
            CustomNetworkObjectProvider.RegisterCustomNetworkObject(CustomSpeakerPrefabID, () =>
            {
                var voiceObject = new GameObject("Voice");
                var audioSource = voiceObject.AddComponent<AudioSource>();
                audioSource.bypassReverbZones = true;
                audioSource.spatialBlend = 1;
                var recorder = voiceObject.AddComponent<Recorder>();
                recorder.StopRecordingWhenPaused = true;
                recorder.SamplingRate = SamplingRate.Sampling48000;
                voiceObject.AddComponent<Speaker>();
                voiceObject.AddComponent<LipSyncPhotonFix>();
                voiceObject.AddComponent<MicAmplifier>().AmplificationFactor = 2;
                voiceObject.AddComponent<VoiceNetworkObject>();
                voiceObject.AddComponent<NetworkTransform>();
                var networkObject = voiceObject.AddComponent<NetworkObject>();
                // unfortunately ObjectInterest field is not public
                var objectInterestField = typeof(NetworkObject).GetField("ObjectInterest",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var areaOfInterestEnum = typeof(NetworkObject).GetNestedType("ObjectInterestModes",
                    BindingFlags.NonPublic).GetField("AreaOfInterest");
                if (objectInterestField != null && areaOfInterestEnum != null)
                {
                    objectInterestField.SetValue(networkObject, (int)areaOfInterestEnum.GetValue(null));
                }
                return voiceObject;
            });
        }

        private void OnEnable()
        {
            FusionBBEvents.OnSceneLoadDone += OnLoaded;
        }

        private void OnDisable()
        {
            FusionBBEvents.OnSceneLoadDone -= OnLoaded;
        }

        private void OnLoaded(NetworkRunner networkRunner)
        {
            StartCoroutine(SpawnSpeaker(networkRunner));
        }

        private IEnumerator SpawnSpeaker(NetworkRunner networkRunner)
        {
            while (networkRunner == null)
            {
                yield return null;
            }

            // Spawn speaker and parent it to centerEyeAnchor
            var speaker = networkRunner.Spawn(
                NetworkPrefabId.FromRaw(CustomSpeakerPrefabID),
                centerEyeAnchor.position,
                centerEyeAnchor.rotation,
                networkRunner.LocalPlayer);
            speaker.transform.SetParent(centerEyeAnchor.transform);
            Speaker = speaker.gameObject;
        }
#endif // PHOTON_VOICE_DEFINED
    }
}
