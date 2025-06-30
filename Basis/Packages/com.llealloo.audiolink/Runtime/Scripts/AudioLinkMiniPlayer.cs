#if UDONSHARP
using System;



using UnityEngine;

using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.AVPro;
using VRC.SDK3.Video.Components.Base;

using VRC.Udon.Common;

namespace AudioLink
{
    [AddComponentMenu("AudioLink/AudioLink Mini Player")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class AudioLinkMiniPlayer : MonoBehaviour
    {
        [Header("Options")]
        [Tooltip("Optional default URL to play on world load")]
        public VRCUrl defaultUrl;

        [Tooltip("Whether player controls are locked to master and instance owner by default")]
        public bool defaultLocked = false;

        public bool retryOnError = true;

        [Tooltip("Write out video player events to VRChat log")]
        public bool debugLogging = true;

        [Tooltip("Automatically loop track when finished")]
        public bool loop = false;

        [Header("Internal")]
        [Tooltip("Use this texture as an input to materials and other shader systems like LTCGI.")]
        public CustomRenderTexture videoRenderTexture;
        [Tooltip("AVPro video player component")]
        public VRCAVProVideoPlayer avProVideo;

        float retryTimeout = 6;
        float syncFrequency = 5;
        float syncThreshold = 1;

        [UdonSynced]
        VRCUrl _syncUrl;
        VRCUrl _queuedUrl;

        [UdonSynced]
        int _syncVideoNumber;
        int _loadedVideoNumber;

        /// <summary>
        /// Synced via <see cref="_flags"/>
        /// </summary>
        [NonSerialized]
        public bool _syncOwnerPlaying;

        [UdonSynced]
        float _syncVideoStartNetworkTime;

        /// <summary>
        /// Synced via <see cref="_flags"/>
        /// </summary>
        bool _syncLocked = true;

        /// <summary>
        /// Flags byte for various settings that are synced. Order is <see cref="_syncOwnerPlaying"/>, <see cref="_syncLocked"/>
        /// </summary>
        [UdonSynced]
        byte _flags = 0b10;

        [NonSerialized]
        public int localPlayerState = PLAYER_STATE_STOPPED;
        [NonSerialized]
        public VideoError localLastErrorCode;

        BaseVRCVideoPlayer _currentPlayer;

        float _lastVideoPosition = 0;
        float _videoTargetTime = 0;

        bool _waitForSync;
        float _lastSyncTime;
        float _playStartTime = 0;

        float _pendingLoadTime = 0;
        float _pendingPlayTime = 0;
        VRCUrl _pendingPlayUrl;

        // Realtime state

        [NonSerialized]
        public bool seekableSource;
        [NonSerialized]
        public float trackDuration;
        [NonSerialized]
        public float trackPosition;
        [NonSerialized]
        public bool locked;

        // Constants

        const int PLAYER_STATE_STOPPED = 0;
        const int PLAYER_STATE_LOADING = 1;
        const int PLAYER_STATE_PLAYING = 2;
        const int PLAYER_STATE_ERROR = 3;

        void Start()
        {
            avProVideo.Loop = false;
            avProVideo.Stop();

            _currentPlayer = avProVideo;

            CopyIntoFlags();

            if (Networking.IsOwner(gameObject))
            {
                _syncLocked = defaultLocked;
                locked = _syncLocked;
                RequestSerialization();

                _PlayVideo(defaultUrl);
            }
        }

        /// <summary>
        /// Copys all the associated bools into the flags byte
        /// </summary>
        private void CopyIntoFlags()
        {
            _flags = 0;
            if (_syncOwnerPlaying)
                _flags |= 1;
            if (_syncLocked)
                _flags |= 2;
        }

        /// <summary>
        /// Copys all the associated bools out of the flags byte into the associated bools
        /// </summary>
        private void CopyOutOfFlags()
        {
            _syncOwnerPlaying = (_flags & 1) != 0;
            _syncLocked = (_flags & 2) != 0;
        }

        public void _TriggerPlay()
        {
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Trigger play");
            if (localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_LOADING)
                return;

            _PlayVideo(_syncUrl);
        }

        public void _TriggerStop()
        {
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Trigger stop");
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _StopVideo();
        }

        public void _TriggerLock()
        {
            if (!_IsAdmin())
                return;
            if (localPlayerState != PLAYER_STATE_PLAYING)
                return;

            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncLocked = !_syncLocked;
            locked = _syncLocked;
            RequestSerialization();
        }

        public void _Resync()
        {
            _ForceResync();
        }

        public void _ChangeUrl(VRCUrl url)
        {
            if (_syncLocked && !_CanTakeControl())
                return;

            _PlayVideo(url);

            _queuedUrl = VRCUrl.Empty;
        }

        public void _UpdateQueuedUrl(VRCUrl url)
        {
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _queuedUrl = url;
        }

        public void _SetTargetTime(float time)
        {
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - time;
            SyncVideo();
            RequestSerialization();
        }

        void _PlayVideo(VRCUrl url)
        {
            _pendingPlayTime = 0;
            if (!_IsUrlValid(url))
                return;

            string message = "Play video " + url;
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] " + message);
            bool isOwner = Networking.IsOwner(gameObject);
            if (!isOwner && !_CanTakeControl())
                return;

            if (!isOwner)
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncUrl = url;
            _syncVideoNumber += isOwner ? 1 : 2;
            _loadedVideoNumber = _syncVideoNumber;
            _syncOwnerPlaying = false;

            _syncVideoStartNetworkTime = float.MaxValue;
            RequestSerialization();

            _videoTargetTime = _ParseTimeFromUrl(url.Get());

            _StartVideoLoad();
        }

        public void _LoopVideo()
        {
            _PlayVideo(_syncUrl);
        }

        public void _PlayQueuedUrl()
        {
            _PlayVideo(_queuedUrl);
            _queuedUrl = VRCUrl.Empty;
        }

        bool _IsUrlValid(VRCUrl url)
        {
            if (!VRC.SDKBase.Utilities.IsValid(url))
                return false;

            string urlStr = url.Get();
            if (urlStr == null || urlStr == "")
                return false;

            return true;
        }

        // Time parsing code adapted from USharpVideo project by Merlin
        float _ParseTimeFromUrl(string urlStr)
        {
            // Attempt to parse out a start time from YouTube links with t= or start=
            if (!urlStr.Contains("youtube.com/watch") && !urlStr.Contains("youtu.be/"))
                return 0;

            int tIndex = urlStr.IndexOf("?t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("?start=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&start=");
            if (tIndex == -1)
                return 0;

            char[] urlArr = urlStr.ToCharArray();
            int numIdx = urlStr.IndexOf('=', tIndex) + 1;

            string intStr = "";
            while (numIdx < urlArr.Length)
            {
                char currentChar = urlArr[numIdx];
                if (!char.IsNumber(currentChar))
                    break;

                intStr += currentChar;
                ++numIdx;
            }

            if (intStr.Length == 0)
                return 0;

            int secondsCount = 0;
            if (!int.TryParse(intStr, out secondsCount))
                return 0;

            return secondsCount;
        }

        void _StartVideoLoadDelay(float delay)
        {
            _pendingLoadTime = Time.time + delay;
        }

        void _StartVideoLoad()
        {
            _pendingLoadTime = 0;
            if (_syncUrl == null || _syncUrl.Get() == "")
                return;

            string message = "Start video load " + _syncUrl;
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] " + message);
            _UpdatePlayerState(PLAYER_STATE_LOADING);

#if !UNITY_EDITOR
            _currentPlayer.LoadURL(_syncUrl);
#endif
        }

        public void _StopVideo()
        {
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Stop video");

            if (seekableSource)
                _lastVideoPosition = _currentPlayer.GetTime();

            _UpdatePlayerState(PLAYER_STATE_STOPPED);

            _currentPlayer.Stop();
            _videoTargetTime = 0;
            _pendingPlayTime = 0;
            _pendingLoadTime = 0;
            _playStartTime = 0;

            if (Networking.IsOwner(gameObject))
            {
                _syncVideoStartNetworkTime = 0;
                _syncOwnerPlaying = false;
                _syncUrl = VRCUrl.Empty;
                RequestSerialization();
            }
        }

        public override void OnVideoReady()
        {
            float duration = _currentPlayer.GetDuration();
            string message = "Video ready, duration: " + duration + ", position: " + _currentPlayer.GetTime();
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] " + message);

            // If a seekable video is loaded it should have a positive duration.  Otherwise we assume it's a non-seekable stream
            seekableSource = !float.IsInfinity(duration) && !float.IsNaN(duration) && duration > 1;

            // If player is owner: play video
            // If Player is remote:
            //   - If owner playing state is already synced, play video
            //   - Otherwise, wait until owner playing state is synced and play later in update()
            //   TODO: Streamline by always doing this in update instead?

            if (Networking.IsOwner(gameObject))
                _currentPlayer.Play();
            else
            {
                // TODO: Stream bypass owner
                if (_syncOwnerPlaying)
                    _currentPlayer.Play();
                else
                    _waitForSync = true;
            }
        }

        public override void OnVideoStart()
        {
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Video start");

            if (Networking.IsOwner(gameObject))
            {
                _UpdatePlayerState(PLAYER_STATE_PLAYING);
                _playStartTime = Time.time;

                _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _videoTargetTime;
                _syncOwnerPlaying = true;
                RequestSerialization();

                _currentPlayer.SetTime(_videoTargetTime);
            }
            else
            {
                if (!_syncOwnerPlaying)
                {
                    // TODO: Owner bypass
                    _currentPlayer.Pause();
                    _waitForSync = true;
                }
                else
                {
                    _UpdatePlayerState(PLAYER_STATE_PLAYING);
                    _playStartTime = Time.time;

                    SyncVideo();
                }
            }
        }

        public override void OnVideoEnd()
        {
            if (!seekableSource && Time.time - _playStartTime < 1)
            {
                Debug.Log("[AudioLink] Video end encountered at start of stream, ignoring");
                return;
            }

            _UpdatePlayerState(PLAYER_STATE_STOPPED);
            seekableSource = false;

            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Video end");
            _lastVideoPosition = 0;

            if (Networking.IsOwner(gameObject))
            {
                if (_IsUrlValid(_queuedUrl))
                    SendCustomEventDelayedFrames("_PlayQueuedUrl", 1);
                else if (loop)
                    SendCustomEventDelayedFrames("_LoopVideo", 1);
                else
                {
                    _syncVideoStartNetworkTime = 0;
                    _syncOwnerPlaying = false;
                    RequestSerialization();
                }
            }
        }

        public override void OnVideoError(VideoError videoError)
        {
            _currentPlayer.Stop();

            string message = "Video stream failed: " + _syncUrl;
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] " + message);
            string message1 = "Error code: " + videoError;
            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] " + message1);

            _UpdatePlayerState(PLAYER_STATE_ERROR);
            localLastErrorCode = videoError;

            if (Networking.IsOwner(gameObject))
            {
                if (retryOnError)
                {
                    _StartVideoLoadDelay(retryTimeout);
                }
                else
                {
                    _syncVideoStartNetworkTime = 0;
                    _videoTargetTime = 0;
                    _syncOwnerPlaying = false;
                    RequestSerialization();
                }
            }
            else
            {
                _StartVideoLoadDelay(retryTimeout);
            }
        }

        public bool _IsAdmin()
        {
            BasisPlayer player = Networking.LocalPlayer;
            if (!VRC.SDKBase.Utilities.IsValid(player))
                return false;

            return player.isMaster || player.isInstanceOwner;
        }

        public bool _CanTakeControl()
        {
            BasisPlayer player = Networking.LocalPlayer;
            if (!VRC.SDKBase.Utilities.IsValid(player))
                return false;

            return player.isMaster || player.isInstanceOwner || !_syncLocked;
        }

        public override void OnPreSerialization()
        {
            CopyIntoFlags();
        }

        public override void OnDeserialization()
        {
            if (Networking.IsOwner(gameObject))
                return;
            
            CopyOutOfFlags();

            if (debugLogging)
            {
                Debug.Log($"[AudioLink:MiniPlayer] Deserialize: video #{_syncVideoNumber}");
            }

            locked = _syncLocked;

            if (_syncVideoNumber == _loadedVideoNumber)
            {
                if (localPlayerState == PLAYER_STATE_PLAYING && !_syncOwnerPlaying)
                {
                    SendCustomEventDelayedFrames("_StopVideo", 1);
                }
                return;
            }

            // There was some code here to bypass load owner sync bla bla

            _loadedVideoNumber = _syncVideoNumber;

            if (debugLogging)
                Debug.Log("[AudioLink:MiniPlayer] Starting video load from sync");

            _StartVideoLoad();
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            if (!result.success)
            {
                if (debugLogging)
                    Debug.Log("[AudioLink:MiniPlayer] Failed to sync");
            }
        }

        void Update()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            float time = Time.time;

            if (_pendingPlayTime > 0 && time > _pendingPlayTime)
                _PlayVideo(_pendingPlayUrl);
            if (_pendingLoadTime > 0 && Time.time > _pendingLoadTime)
                _StartVideoLoad();

            if (seekableSource && localPlayerState == PLAYER_STATE_PLAYING)
            {
                trackDuration = _currentPlayer.GetDuration();
                trackPosition = _currentPlayer.GetTime();
            }

            // Video is playing: periodically sync with owner
            if (isOwner || !_waitForSync)
            {
                SyncVideoIfTime();
                return;
            }

            // Video is not playing, but still waiting for go-ahead from owner
            if (!_syncOwnerPlaying)
                return;

            // Got go-ahead from owner, start playing video
            _UpdatePlayerState(PLAYER_STATE_PLAYING);

            _waitForSync = false;
            _currentPlayer.Play();

            SyncVideo();
        }

        void SyncVideoIfTime()
        {
            if (Time.realtimeSinceStartup - _lastSyncTime > syncFrequency)
            {
                _lastSyncTime = Time.realtimeSinceStartup;
                SyncVideo();
            }
        }

        void SyncVideo()
        {
            if (seekableSource)
            {
                float offsetTime = Mathf.Clamp((float)Networking.GetServerTimeInSeconds() - _syncVideoStartNetworkTime, 0f, _currentPlayer.GetDuration());
                if (Mathf.Abs(_currentPlayer.GetTime() - offsetTime) > syncThreshold)
                    _currentPlayer.SetTime(offsetTime);
            }
        }

        public void _ForceResync()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            if (isOwner)
            {
                if (seekableSource)
                {
                    float startTime = _videoTargetTime;
                    if (_currentPlayer.IsPlaying)
                        startTime = _currentPlayer.GetTime();

                    _StartVideoLoad();
                    _videoTargetTime = startTime;
                }
                return;
            }

            _currentPlayer.Stop();
            if (_syncOwnerPlaying)
                _StartVideoLoad();
        }

        void _UpdatePlayerState(int state)
        {
            localPlayerState = state;

            if (VRC.SDKBase.Utilities.IsValid(videoRenderTexture))
            {
                if (state == PLAYER_STATE_PLAYING)
                    videoRenderTexture.updateMode = CustomRenderTextureUpdateMode.Realtime;
                else
                    videoRenderTexture.updateMode = CustomRenderTextureUpdateMode.OnDemand;
            }
        }
    }
}
#endif
