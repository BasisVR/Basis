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
using System.Collections;
using Meta.XR.BuildingBlocks;
using UnityEngine;

#if META_AVATAR_SDK_DEFINED
using Oculus.Avatar2;
using Oculus.Avatar2.Experimental;
using CAPI = Oculus.Avatar2.CAPI;
#endif // META_AVATAR_SDK_DEFINED

namespace Meta.XR.MultiplayerBlocks.Shared
{
#if META_AVATAR_SDK_DEFINED
    /// <summary>
    /// The Avatar Entity implementation for Networked Avatar, which loads a remote/local avatar according to <see cref="IAvatarBehaviour"/>.
    /// It also provides a fallback solution to a local zip avatar with a randomized preloaded avatar from sample assets
    /// when the user is not entitled (no Meta Id) or has no avatar setup.
    /// For more information on the Meta Avatars SDK, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    [DefaultExecutionOrder(50)] // after GpuSkinningConfiguration initializes
    public class AvatarEntity : OvrAvatarEntity, IAvatarStreamConfig
    {
        /// <summary>
        /// Event triggered when the <see cref="AvatarEntity"/> is spawned.
        /// </summary>
        public static event Action<AvatarEntity> OnSpawned;

#if META_AVATAR_SDK_28_OR_NEWER
        [Header("Face Pose Input")]
        [SerializeField]
        private OvrAvatarFacePoseBehavior m_facePoseProvider;
        [SerializeField]
        private OvrAvatarEyePoseBehavior m_eyePoseProvider;
#endif

        private byte[] _streamedData;
        private float _cycleStartTime;
        private bool _isSkeletonLoaded;
        private bool _initialAvatarLoaded;
        private IAvatarBehaviour _avatarBehaviour;
        private StreamLOD _streamLevel = StreamLOD.Medium;
        private float _intervalToSendDataInSec = 0.08f;

        /// <summary>
        /// Could be triggered by any changes like oculus id, local avatar index, network connection etc.
        /// </summary>
        public void ReloadAvatarManually()
        {
            if (!_initialAvatarLoaded)
            {
                return;
            }

            _isSkeletonLoaded = false;
            EntityActive = false;
            Teardown();
            CreateEntity();
            LoadAvatar();
        }

        protected override void Awake()
        {
            _avatarBehaviour = this.GetInterfaceComponent<IAvatarBehaviour>();
            if (_avatarBehaviour == null)
            {
                throw new InvalidOperationException("Using AvatarEntity without an IAvatarBehaviour");
            }

            OnSpawned?.Invoke(this);
        }

        private void Start()
        {
            if (_avatarBehaviour == null)
            {
                return;
            }

            ConfigureAvatar();
            base.Awake(); // creating avatar entity here

#if META_AVATAR_SDK_28_OR_NEWER
            SetActiveView(_avatarBehaviour.HasInputAuthority ? CAPI.ovrAvatar2EntityViewFlags.FirstPerson : CAPI.ovrAvatar2EntityViewFlags.ThirdPerson);
#else
            if (!_avatarBehaviour.HasInputAuthority)
            {
                SetActiveView(CAPI.ovrAvatar2EntityViewFlags.ThirdPerson);
            }
#endif

            LoadAvatar();
            _initialAvatarLoaded = true;
        }

#if META_AVATAR_SDK_28_OR_NEWER
        private OvrAvatarInputManagerBehavior m_bodyTracking;
        public OvrAvatarInputManagerBehavior BodyTracking
        {
            get => m_bodyTracking;
            protected set
            {
                m_bodyTracking = value;
                SetInputManager(value);
            }
        }

        private OvrAvatarInputManagerBehavior FindAvatarInputManagerBehavior()
        {
            foreach (var b in FindObjectsByType<OvrAvatarInputManagerBehavior>(FindObjectsSortMode.None))
            {
                if (b.isActiveAndEnabled)
                {
                    return b;
                }
            }

            return null;
        }
#endif

        private void ConfigureAvatar()
        {
            var isLocal = _avatarBehaviour.HasInputAuthority;
            SetIsLocal(isLocal);
            gameObject.name = isLocal ? "LocalAvatar" : "RemoteAvatar";

            if (isLocal)
            {
#if META_AVATAR_SDK_28_OR_NEWER
                // based on Preset_Default we remove hand scaling for local
                _creationInfo.features &= ~CAPI.ovrAvatar2EntityFeatures.HandScaling;

                var body = FindAvatarInputManagerBehavior();
                BodyTracking = body;

                SetFacePoseProvider(m_facePoseProvider);
                SetEyePoseProvider(m_eyePoseProvider);

                AvatarLODManager.Instance.firstPersonAvatarLod = AvatarLOD;
                AdjustAvatarLOD(_streamLevel);
#else
                _creationInfo.features = CAPI.ovrAvatar2EntityFeatures.Preset_Default;
                var entityInputManager = OvrAvatarManager.Instance.gameObject.GetComponent<EntityInputManager>();
                SetBodyTracking(entityInputManager);
#endif

                var lipSyncInput = FindObjectOfType<OvrAvatarLipSyncContext>();
                SetLipSync(lipSyncInput);
            }
            else
            {
#if META_AVATAR_SDK_28_OR_NEWER
                _creationInfo.features = CAPI.ovrAvatar2EntityFeatures.Preset_Remote;
                _creationInfo.features &= ~CAPI.ovrAvatar2EntityFeatures.UseDefaultAnimHierarchy;

                SetInputManager(null);
                SetFacePoseProvider(null);
                SetEyePoseProvider(null);
                SetLipSync(null);

                // ReSharper disable once Unity.PreferGenericMethodOverload
                var animationBehavior = GetComponent("OvrAvatarAnimationBehavior");
                if (animationBehavior != null)
                {
                    Destroy(animationBehavior);
                }
#else
                _creationInfo.features = CAPI.ovrAvatar2EntityFeatures.Preset_Remote;
#endif
            }
        }

        private void LoadAvatar()
        {
            if (_avatarBehaviour.OculusId == 0)
            {
                LoadLocalAvatar();
            }
            else
            {
                StartCoroutine(TryToLoadUserAvatar());
            }
        }

        private IEnumerator TryToLoadUserAvatar()
        {
            while (!OvrAvatarEntitlement.AccessTokenIsValid())
            {
                yield return null;
            }
            _userId = _avatarBehaviour.OculusId;
            var hasAvatarRequest = OvrAvatarManager.Instance.UserHasAvatarAsync(_userId);
            while (hasAvatarRequest.IsCompleted == false)
            {
                yield return null;
            }
            if (hasAvatarRequest.Result == OvrAvatarManager.HasAvatarRequestResultCode.HasAvatar)
            {
                LoadUser();
            }
            else // fallback to local avatar
            {
                LoadLocalAvatar();
            }
        }

        private void LoadLocalAvatar()
        {
#if META_AVATAR_SAMPLE_ASSETS_DEFINED
            // we only load local avatar from zip after Avatar Sample Assets is installed
            var assetPath = $"{_avatarBehaviour.LocalAvatarIndex}{GetAssetPostfix()}";
            LoadAssets(new[] { assetPath }, AssetSource.Zip);
#else
            Debug.LogWarning("Meta Avatar Sample Assets package not installed, local avatar cannot be loaded from zip");
#endif // META_AVATAR_SAMPLE_ASSETS_DEFINED
        }

        private string GetAssetPostfix(bool isFromZip = true) {
            return "_" + OvrAvatarManager.Instance.GetPlatformGLBPostfix(_creationInfo.renderFilters.quality, isFromZip)
                       + OvrAvatarManager.Instance.GetPlatformGLBVersion(_creationInfo.renderFilters.quality, isFromZip)
                       + OvrAvatarManager.Instance.GetPlatformGLBExtension(isFromZip);
        }

        protected override void OnSkeletonLoaded()
        {
            base.OnSkeletonLoaded();
            _isSkeletonLoaded = true;
        }

        private void Update()
        {
            if (!_isSkeletonLoaded || _streamedData == null || IsLocal)
            {
                return;
            }

            if (_streamedData == null)
            {
                return;
            }

            //Apply the remote avatar state and smooth the animation
            ApplyStreamData(_streamedData);
            SetPlaybackTimeDelay(_intervalToSendDataInSec / 2);
            _streamedData = null;
        }

        private void LateUpdate()
        {
            if (!_isSkeletonLoaded)
            {
                return;
            }

            var elapsedTime = Time.time - _cycleStartTime;
            if (elapsedTime < _intervalToSendDataInSec)
            {
                return;
            }

            RecordAndSendStreamDataIfHasAuthority();
            _cycleStartTime = Time.time;
        }

        private void RecordAndSendStreamDataIfHasAuthority()
        {
            if (!IsLocal || _avatarBehaviour == null)
            {
                return;
            }

            var bytes = RecordStreamData(_streamLevel);
            _avatarBehaviour.ReceiveStreamData(bytes);
        }

        /// <summary>
        /// Syncs the current Avatar with the one passed in <paramref name="bytes"/>.
        /// </summary>
        /// <param name="bytes">Byte stream containing the serialized avatar state.</param>
        public void SetStreamData(byte[] bytes)
        {
            _streamedData = bytes;
        }

#if META_AVATAR_SDK_28_OR_NEWER
        private void AdjustAvatarLOD(StreamLOD lod)
        {
            var avatarLod = GetComponent<AvatarLOD>();

            if (avatarLod != null)
            {
                avatarLod.dynamicStreamLod = lod;
            }
        }
#endif

#region IAvatarStreamLOD

        /// <summary>
        /// Sets the quality level to be used when serializing the Avatar state.
        /// An implementation of the <see cref="IAvatarStreamConfig"/> interface.
        /// </summary>
        public void SetAvatarStreamLOD(AvatarStreamLOD lod)
        {
            _streamLevel = lod switch
            {
                AvatarStreamLOD.Low => StreamLOD.Low,
                AvatarStreamLOD.Medium => StreamLOD.Medium,
                AvatarStreamLOD.High => StreamLOD.High,
                _ => StreamLOD.Medium
            };

#if META_AVATAR_SDK_28_OR_NEWER
            AdjustAvatarLOD(_streamLevel);
#endif
        }

        /// <summary>
        /// Sets the interval at which the Avatar state is synchronized.
        /// An implementation of the <see cref="IAvatarStreamConfig"/> interface.
        /// </summary>
        public void SetAvatarUpdateIntervalInS(float interval)
        {
            _intervalToSendDataInSec = interval;
        }

#endregion
    }
#endif // META_AVATAR_SDK_DEFINED
}
