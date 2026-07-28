using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.VoiceRecording
{
    /// <summary>
    /// A standalone spatialized (3D) audio source that re-emits a consented remote
    /// player's voice from a caller-supplied anchor transform — e.g. a radio object
    /// (issue #911). It mirrors <see cref="BasisShoutAudioDriver"/>'s per-shouter source
    /// but is 3D and parented to the anchor, and is fed encoded frames tapped from the
    /// player's receiver instead of read from the network directly. It never exposes PCM
    /// to its creator; callers only position it via the anchor.
    /// </summary>
    public sealed class BasisVoiceObjectSource
    {
        public ushort PlayerId { get; }
        public Transform Anchor { get; private set; }
        public bool AnchorLost { get; private set; }

        internal BasisAudioReceiver Receiver { get; private set; }

        private AudioSource _audioSource;
        private BasisRemoteAudioDriver _driver;
        private GameObject _root;
        private volatile bool _active;

        internal BasisVoiceObjectSource(ushort playerId)
        {
            PlayerId = playerId;
        }

        internal bool Initialize(Transform anchor, float minDistance, float maxDistance)
        {
#if UNITY_SERVER
            return false;
#else
            if (anchor == null || BasisDeviceManagement.Instance == null)
            {
                return false;
            }
            Anchor = anchor;

            Receiver = new BasisAudioReceiver();
            BasisAudioReceiver.outputSampleRate = AudioSettings.outputSampleRate;
            BasisAudioReceiver.silentData ??= new float[RemoteOpusSettings.MaxFrameSize];

#if UNITY_IOS && !UNITY_EDITOR
            Receiver.decoder = new OpusSharp.Core.Static.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#else
            Receiver.decoder = new OpusSharp.Core.Dynamic.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#endif

            _root = new GameObject($"Voice Object Source {PlayerId}");
            _root.transform.SetParent(BasisDeviceManagement.Instance.transform, false);
            _root.transform.position = anchor.position;

            _audioSource = _root.AddComponent<AudioSource>();
            _audioSource.clip = BasisAudioClipPool.Get(PlayerId);
            _audioSource.loop = true;
            _audioSource.spatialBlend = 1f;
            _audioSource.spatialize = false;
            _audioSource.spatializePostEffects = false;
            _audioSource.dopplerLevel = 0f;
            _audioSource.spread = 0f;
            _audioSource.minDistance = minDistance;
            _audioSource.maxDistance = maxDistance;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.volume = 1f;

            _driver = _root.AddComponent<BasisRemoteAudioDriver>();
            _driver.BasisAudioReceiver = Receiver;

            Receiver.audioSource = _audioSource;
            Receiver.AudioSourceTransform = _root.transform;
            Receiver.DirectionalDampeningMultiplier = 1f;
            Receiver.InitializeForPlayback();
            Receiver.HasAudioSource = true;
            _driver.Initialized = true;

            _audioSource.Play();
            _active = true;
            return true;
#endif
        }

        internal void FeedEncoded(AudioSegmentDataMessage msg)
        {
            if (_active)
            {
                Receiver?.Insert(msg);
            }
        }

        internal void Tick()
        {
            if (!_active || Receiver == null)
            {
                return;
            }
            if (Anchor == null)
            {
                AnchorLost = true;
                return;
            }
            if (_root != null)
            {
                _root.transform.position = Anchor.position;
            }
            if (!Receiver.IsAudioActive || Receiver.VoiceBuffer.DecodedFrameCount == 0)
            {
                Receiver.DrainAndDecodeThreadSafe();
            }
            Receiver.ApplyAudioState();
        }

        internal void Dispose()
        {
            _active = false;
            if (Receiver != null)
            {
                Receiver.HasAudioSource = false;
                Receiver.OnEncodedFrame = null;
                Receiver.OnDecodedFrame = null;
            }
#if !UNITY_SERVER
            if (Receiver != null && Receiver.decoder != null)
            {
                Receiver.decoder.Dispose();
                Receiver.decoder = null;
            }
#endif
            if (_audioSource != null)
            {
                _audioSource.Stop();
                if (_audioSource.clip != null)
                {
                    BasisAudioClipPool.Return(_audioSource.clip);
                }
            }
            if (_driver != null)
            {
                _driver.Initialized = false;
                Object.Destroy(_driver);
            }
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            Receiver = null;
            _audioSource = null;
            _driver = null;
            _root = null;
        }
    }
}
