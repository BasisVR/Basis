using System.Collections;
using UnityEngine;
using Cilbox;
using Basis.BasisUI;

namespace Basis
{
    public class VideoPlayerShim : CilboxShim
    {
        public delegate void ShimEventHandler(VideoPlayerShim source);
        public delegate void ShimErrorEventHandler(VideoPlayerShim source, string message);
        public delegate void ShimFrameReadyEventHandler(VideoPlayerShim source, long frameIndex);
        public delegate void ShimTimeEventHandler(VideoPlayerShim source, double seconds);

        private const float PendingUrlTimeoutSeconds = 20f;

        private UnityEngine.Video.VideoPlayer videoPlayer;
        private string pendingConfirmedUrl = string.Empty;
        private bool hasPendingConfirmedUrl;
        private bool prepareRequestedForPendingUrl;
        private int pendingConfirmedUrlRequestId;
        private Coroutine pendingUrlTimeoutCoroutine;
        private ShimEventHandler _prepareCompleted;
        private ShimEventHandler _loopPointReached;
        private ShimEventHandler _started;
        private ShimEventHandler _frameDropped;
        private ShimErrorEventHandler _errorReceived;
        private ShimEventHandler _seekCompleted;
        private ShimTimeEventHandler _clockResyncOccurred;
        private ShimFrameReadyEventHandler _frameReady;

        public void Awake()
        {
            videoPlayer = gameObject.GetComponent<UnityEngine.Video.VideoPlayer>();
            if (videoPlayer == null)
            {
                videoPlayer = gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
            }

            videoPlayer.prepareCompleted -= OnPrepareCompleted;
            videoPlayer.prepareCompleted += OnPrepareCompleted;
            videoPlayer.loopPointReached -= OnLoopPointReached;
            videoPlayer.loopPointReached += OnLoopPointReached;
            videoPlayer.started -= OnStarted;
            videoPlayer.started += OnStarted;
            videoPlayer.frameDropped -= OnFrameDropped;
            videoPlayer.frameDropped += OnFrameDropped;
            videoPlayer.errorReceived -= OnErrorReceived;
            videoPlayer.errorReceived += OnErrorReceived;
            videoPlayer.seekCompleted -= OnSeekCompleted;
            videoPlayer.seekCompleted += OnSeekCompleted;
            videoPlayer.clockResyncOccurred -= OnClockResyncOccurred;
            videoPlayer.clockResyncOccurred += OnClockResyncOccurred;
            videoPlayer.frameReady -= OnFrameReady;
            videoPlayer.frameReady += OnFrameReady;
        }

        public void OnDestroy()
        {
            if (videoPlayer == null)
            {
                return;
            }

            videoPlayer.prepareCompleted -= OnPrepareCompleted;
            videoPlayer.loopPointReached -= OnLoopPointReached;
            videoPlayer.started -= OnStarted;
            videoPlayer.frameDropped -= OnFrameDropped;
            videoPlayer.errorReceived -= OnErrorReceived;
            videoPlayer.seekCompleted -= OnSeekCompleted;
            videoPlayer.clockResyncOccurred -= OnClockResyncOccurred;
            videoPlayer.frameReady -= OnFrameReady;
        }

        public string url { 
            get { return videoPlayer.url; } 
            set {
                string url = value;

                if(url == videoPlayer.url) return;
                if(hasPendingConfirmedUrl && url == pendingConfirmedUrl) return;
                if(!url.StartsWith("https://")) return;

                Debug.Log($"[VideoPlayerShim] Setting URL to {url}");
                pendingConfirmedUrl = url;
                hasPendingConfirmedUrl = true;
                prepareRequestedForPendingUrl = false;
                pendingConfirmedUrlRequestId++;
                int requestId = pendingConfirmedUrlRequestId;

                if (pendingUrlTimeoutCoroutine != null)
                {
                    StopCoroutine(pendingUrlTimeoutCoroutine);
                }

                pendingUrlTimeoutCoroutine = StartCoroutine(ExpirePendingUrlRequest(requestId));

                BasisMainMenu.Open();
                BasisMainMenu.Instance.OpenDialogue(
                    "Video Player URL",
                    $"Do you want to load this video?\n{url}",
                    "Accept",
                    "Decline",
                    accepted => {
                        if (!hasPendingConfirmedUrl || pendingConfirmedUrl != url || pendingConfirmedUrlRequestId != requestId)
                        {
                            return;
                        }

                        if (!accepted)
                        {
                            ClearPendingUrlRequest();
                            return;
                        }

                        videoPlayer.url = url;
                        hasPendingConfirmedUrl = false;
                        pendingConfirmedUrl = string.Empty;
                        if (pendingUrlTimeoutCoroutine != null)
                        {
                            StopCoroutine(pendingUrlTimeoutCoroutine);
                            pendingUrlTimeoutCoroutine = null;
                        }

                        if (prepareRequestedForPendingUrl)
                        {
                            prepareRequestedForPendingUrl = false;
                            videoPlayer.Prepare();
                        }
                    }
                );
            } 
        }
        public RenderTexture targetTexture { 
            get { return videoPlayer.targetTexture; } 
            set { videoPlayer.targetTexture = value; } 
        }
        public bool isPlaying { get { return videoPlayer.isPlaying; } }
        public bool isPrepared { get { return videoPlayer.isPrepared; } }
        public bool waitForFirstFrame { get { return videoPlayer.waitForFirstFrame; } set { videoPlayer.waitForFirstFrame = value; } }
        public bool skipOnDrop { get { return videoPlayer.skipOnDrop; } set { videoPlayer.skipOnDrop = value; } }
        public bool playOnAwake { get { return videoPlayer.playOnAwake; } set { videoPlayer.playOnAwake = value; } }
        public bool isLooping { get { return videoPlayer.isLooping; } set { videoPlayer.isLooping = value; } }
        public bool sendFrameReadyEvents { get { return videoPlayer.sendFrameReadyEvents; } set { videoPlayer.sendFrameReadyEvents = value; } }
        public long frame { get { return (long)videoPlayer.frame; } set { videoPlayer.frame = value; } }
        public double time { 
            get { return videoPlayer.time; } 
            set { videoPlayer.time = value; } 
        }
        public float playbackSpeed { get { return videoPlayer.playbackSpeed; } set { videoPlayer.playbackSpeed = value; } }
        public ulong frameCount { get { return videoPlayer.frameCount; } }
        public double length { get { return videoPlayer.length; } }

        public ShimEventHandler prepareCompleted { get { return _prepareCompleted; } set { _prepareCompleted = value; } }
        public ShimEventHandler loopPointReached { get { return _loopPointReached; } set { _loopPointReached = value; } }
        public ShimEventHandler started { get { return _started; } set { _started = value; } }
        public ShimEventHandler frameDropped { get { return _frameDropped; } set { _frameDropped = value; } }
        public ShimErrorEventHandler errorReceived { get { return _errorReceived; } set { _errorReceived = value; } }
        public ShimEventHandler seekCompleted { get { return _seekCompleted; } set { _seekCompleted = value; } }
        public ShimTimeEventHandler clockResyncOccurred { get { return _clockResyncOccurred; } set { _clockResyncOccurred = value; } }
        public ShimFrameReadyEventHandler frameReady { get { return _frameReady; } set { _frameReady = value; } }

        public void Play() { videoPlayer.Play(); }
        public void Pause() { videoPlayer.Pause(); }
        public void Stop() { videoPlayer.Stop(); }
        public void Prepare()
        {
            if (hasPendingConfirmedUrl)
            {
                prepareRequestedForPendingUrl = true;
                return;
            }

            prepareRequestedForPendingUrl = false;
            videoPlayer.Prepare();
        }

        private void ClearPendingUrlRequest()
        {
            hasPendingConfirmedUrl = false;
            pendingConfirmedUrl = string.Empty;
            prepareRequestedForPendingUrl = false;
            if (pendingUrlTimeoutCoroutine != null)
            {
                StopCoroutine(pendingUrlTimeoutCoroutine);
                pendingUrlTimeoutCoroutine = null;
            }
        }

        private IEnumerator ExpirePendingUrlRequest(int requestId)
        {
            yield return new WaitForSecondsRealtime(PendingUrlTimeoutSeconds);

            pendingUrlTimeoutCoroutine = null;

            if (!hasPendingConfirmedUrl || pendingConfirmedUrlRequestId != requestId)
            {
                yield break;
            }

            Debug.Log($"[VideoPlayerShim] Timed out waiting for approval: {pendingConfirmedUrl}");
            AutoDenyPendingUrlRequest();
        }

        private void AutoDenyPendingUrlRequest()
        {
            string url = pendingConfirmedUrl;
            BasisMenuDialoguePanel dialogue = BasisMainMenu.Instance != null ? BasisMainMenu.Instance.Dialogue : null;
            bool closedDialogue = false;

            if (dialogue != null &&
                dialogue.Title == "Video Player URL" &&
                !string.IsNullOrEmpty(dialogue.Description) &&
                dialogue.Description.Contains(url))
            {
                dialogue.Callback?.Invoke(false);
                if (BasisMainMenu.Instance != null && BasisMainMenu.Instance.Dialogue == dialogue)
                {
                    BasisMainMenu.Instance.Dialogue = null;
                }
                dialogue.ReleaseInstance();
                closedDialogue = true;
            }

            if (!closedDialogue)
            {
                ClearPendingUrlRequest();
            }
        }

        private void OnPrepareCompleted(UnityEngine.Video.VideoPlayer source)
        {
            _prepareCompleted?.Invoke(this);
        }

        private void OnLoopPointReached(UnityEngine.Video.VideoPlayer source)
        {
            _loopPointReached?.Invoke(this);
        }

        private void OnStarted(UnityEngine.Video.VideoPlayer source)
        {
            _started?.Invoke(this);
        }

        private void OnFrameDropped(UnityEngine.Video.VideoPlayer source)
        {
            _frameDropped?.Invoke(this);
        }

        private void OnErrorReceived(UnityEngine.Video.VideoPlayer source, string message)
        {
            _errorReceived?.Invoke(this, message);
        }

        private void OnSeekCompleted(UnityEngine.Video.VideoPlayer source)
        {
            _seekCompleted?.Invoke(this);
        }

        private void OnClockResyncOccurred(UnityEngine.Video.VideoPlayer source, double seconds)
        {
            _clockResyncOccurred?.Invoke(this, seconds);
        }

        private void OnFrameReady(UnityEngine.Video.VideoPlayer source, long frameIndex)
        {
            _frameReady?.Invoke(this, frameIndex);
        }
    }
}
