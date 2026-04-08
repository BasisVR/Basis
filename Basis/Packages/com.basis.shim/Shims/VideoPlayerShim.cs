using UnityEngine;
using Cilbox;
using UnityEngine.Video;
using Basis.BasisUI;
using System.Collections;

namespace Basis.Shims
{
    public class VideoPlayerShim : CilboxShim
    {
        private const float PendingUrlTimeoutSeconds = 20f;

        private VideoPlayer videoPlayer;
        private string pendingConfirmedUrl = string.Empty;
        private bool hasPendingConfirmedUrl;
        private bool playRequestedForPendingUrl;
        private bool prepareRequestedForPendingUrl;
        private int pendingConfirmedUrlRequestId;
        private Coroutine pendingUrlTimeoutCoroutine;

        public void Awake()
        {
            videoPlayer = gameObject.GetComponent<VideoPlayer>();
            if (videoPlayer == null)
                videoPlayer = gameObject.AddComponent<VideoPlayer>();
            videoPlayer.clockResyncOccurred += OnClockResyncOccurred;
            videoPlayer.errorReceived += OnErrorReceived;
            videoPlayer.frameDropped += OnFrameDropped;
            videoPlayer.frameReady += OnFrameReady;
            videoPlayer.loopPointReached += OnLoopPointReached;
            videoPlayer.prepareCompleted += OnPrepareCompleted;
            videoPlayer.seekCompleted += OnSeekCompleted;
            videoPlayer.started += OnStarted;
        }

        public void OnDestroy()
        {
            if (videoPlayer == null) return;
            videoPlayer.clockResyncOccurred -= OnClockResyncOccurred;
            videoPlayer.errorReceived -= OnErrorReceived;
            videoPlayer.frameDropped -= OnFrameDropped;
            videoPlayer.frameReady -= OnFrameReady;
            videoPlayer.loopPointReached -= OnLoopPointReached;
            videoPlayer.prepareCompleted -= OnPrepareCompleted;
            videoPlayer.seekCompleted -= OnSeekCompleted;
            videoPlayer.started -= OnStarted;
        }

        //
        // Properties
        //
        public static ushort controlledAudioTrackMaxCount = 32;
        public VideoAspectRatio aspectRatio
        {
            get { return videoPlayer.aspectRatio; }
            set { videoPlayer.aspectRatio = value; }
        }
        public VideoAudioOutputMode audioOutputMode
        {
            get { return videoPlayer.audioOutputMode; }
            set { videoPlayer.audioOutputMode = value; }
        }
        public ushort audioTrackCount { get { return videoPlayer.audioTrackCount; } }
        public bool canSetDirectAudioVolume { get { return videoPlayer.canSetDirectAudioVolume; } }
        public bool canSetPlaybackSpeed { get { return videoPlayer.canSetPlaybackSpeed; } }
        public bool canSetSkipOnDrop { get { return videoPlayer.canSetSkipOnDrop; } }
        public bool canSetTime { get { return videoPlayer.canSetTime; } }
        public bool canSetTimeUpdateMode { get { return videoPlayer.canSetTimeUpdateMode; } }
        public bool canStep { get { return videoPlayer.canStep; } }
        public VideoClip clip
        {
            get { return videoPlayer.clip; }
            set { videoPlayer.clip = value; }
        }
        public double clockTime { get { return videoPlayer.clockTime; } }
        public ushort controlledAudioTrackCount { get { return videoPlayer.controlledAudioTrackCount; } }
        public double externalReferenceTime
        {
            get { return videoPlayer.externalReferenceTime; }
            set { videoPlayer.externalReferenceTime = value; }
        }
        public long frame { get { return videoPlayer.frame; } }
        public ulong frameCount { get { return videoPlayer.frameCount; } }
        public float frameRate { get { return videoPlayer.frameRate; } }
        public uint height { get { return videoPlayer.height; } }
        public bool isLooping
        {
            get { return videoPlayer.isLooping; }
            set { videoPlayer.isLooping = value; }
        }
        public bool isPaused { get { return videoPlayer.isPaused; } }
        public bool isPlaying { get { return videoPlayer.isPlaying; } }
        public bool isPrepared { get { return videoPlayer.isPrepared; } }
        public double length { get { return videoPlayer.length; } }
        public uint pixelAspectRatioDenominator
        {
            get { return videoPlayer.pixelAspectRatioDenominator; }
        }
        public uint pixelAspectRatioNumerator
        {
            get { return videoPlayer.pixelAspectRatioNumerator; }
        }
        public float playbackSpeed
        {
            get { return videoPlayer.playbackSpeed; }
            set { videoPlayer.playbackSpeed = value; }
        }
        public bool playOnAwake
        {
            get { return videoPlayer.playOnAwake; }
            set { videoPlayer.playOnAwake = value; }
        }
        public VideoRenderMode renderMode
        {
            get { return videoPlayer.renderMode; }
            set { videoPlayer.renderMode = value; }
        }
        public bool sendFrameReadyEvents
        {
            get { return videoPlayer.sendFrameReadyEvents; }
            set { videoPlayer.sendFrameReadyEvents = value; }
        }
        public bool skipOnDrop
        {
            get { return videoPlayer.skipOnDrop; }
            set { videoPlayer.skipOnDrop = value; }
        }
        public VideoSource source
        {
            get { return videoPlayer.source; }
            set { videoPlayer.source = value; }
        }
        public Camera targetCamera
        {
            get { return videoPlayer.targetCamera; }
            set { videoPlayer.targetCamera = value; }
        }
        public Video3DLayout targetCamera3DLayout
        {
            get { return videoPlayer.targetCamera3DLayout; }
            set { videoPlayer.targetCamera3DLayout = value; }
        }
        public float targetCameraAlpha
        {
            get { return videoPlayer.targetCameraAlpha; }
            set { videoPlayer.targetCameraAlpha = value; }
        }
        public string targetMaterialProperty
        {
            get { return videoPlayer.targetMaterialProperty; }
            set { videoPlayer.targetMaterialProperty = value; }
        }
        public Renderer targetMaterialRenderer
        {
            get { return videoPlayer.targetMaterialRenderer; }
            set { videoPlayer.targetMaterialRenderer = value; }
        }
        public RenderTexture targetTexture
        {
            get { return videoPlayer.targetTexture; }
            set { videoPlayer.targetTexture = value; }
        }
        public Texture texture { get { return videoPlayer.texture; } }
        public double time
        {
            get { return videoPlayer.time; }
            set { videoPlayer.time = value; }
        }
        public VideoTimeReference timeReference
        {
            get { return videoPlayer.timeReference; }
            set { videoPlayer.timeReference = value; }
        }
        public VideoTimeUpdateMode timeUpdateMode
        {
            get { return videoPlayer.timeUpdateMode; }
            set { videoPlayer.timeUpdateMode = value; }
        }
        public string url
        {
            get { return videoPlayer.url; }
            set
            {
                string url = value;

                if (url == videoPlayer.url) return;
                if(hasPendingConfirmedUrl && url == pendingConfirmedUrl) return;
                if (!url.StartsWith("https://")) return;

                if (BasisTrustedVideoUrls.IsTrusted(url))
                {
                    BasisDebug.Log($"[VideoPlayerShim] Auto-accepting trusted URL \"{url}\"", BasisDebug.LogTag.Shims);
                    AutoDenyPendingUrlRequest();
                    videoPlayer.url = url;
                    if (playRequestedForPendingUrl)
                    {
                        playRequestedForPendingUrl = false;
                        videoPlayer.Play();
                    }
                    if (prepareRequestedForPendingUrl)
                    {
                        prepareRequestedForPendingUrl = false;
                        videoPlayer.Prepare();
                    }
                    return;
                }

                BasisDebug.Log($"[VideoPlayerShim] Requesting URL \"{url}\"", BasisDebug.LogTag.Shims);

                AutoDenyPendingUrlRequest();
                pendingConfirmedUrl = url;
                hasPendingConfirmedUrl = true;
                pendingConfirmedUrlRequestId++;
                int requestId = pendingConfirmedUrlRequestId;
                pendingUrlTimeoutCoroutine = StartCoroutine(ExpirePendingUrlRequest(requestId));

                bool handled = false;

                BasisMainMenu.Open();
                BasisMainMenu.Instance.OpenDialogue(
                    "Video Player URL",
                    $"Do you want to load this video?\n{url}",
                    "Accept",
                    "Decline",
                    accepted =>
                    {
                        if (handled) return;
                        handled = true;
                        if (!hasPendingConfirmedUrl || pendingConfirmedUrl != url || pendingConfirmedUrlRequestId != requestId)
                        {
                            return;
                        }
                        if (!accepted)
                        {
                            ClearPendingUrlRequest();
                            return;
                        }
                        ApplyPendingUrl(url);
                    }
                );

                BasisMenuDialoguePanel dialogue = BasisMainMenu.Instance.Dialogue;
                if (dialogue != null)
                {
                    PanelButton acceptAndSaveButton = PanelButton.CreateNew(dialogue.AcceptButton.transform.parent);
                    acceptAndSaveButton.Descriptor.SetTitle("Accept & Save");
                    acceptAndSaveButton.transform.SetSiblingIndex(dialogue.AcceptButton.transform.GetSiblingIndex() + 1);
                    acceptAndSaveButton.OnClicked += () =>
                    {
                        if (handled) return;
                        handled = true;
                        if (hasPendingConfirmedUrl && pendingConfirmedUrl == url && pendingConfirmedUrlRequestId == requestId)
                        {
                            BasisTrustedVideoUrls.Add(url);
                            ApplyPendingUrl(url);
                        }
                        BasisMainMenu.Instance.Dialogue = null;
                        dialogue.ReleaseInstance();
                    };
                }
            }
        }
        public bool waitForFirstFrame
        {
            get { return videoPlayer.waitForFirstFrame; }
            set { videoPlayer.waitForFirstFrame = value; }
        }
        public uint width { get { return videoPlayer.width; } }
        //
        // Methods
        //
        public void EnableAudioTrack(ushort trackIndex, bool enabled) { videoPlayer.EnableAudioTrack(trackIndex, enabled); }
        public ushort GetAudioChannelCount(ushort trackIndex) { return videoPlayer.GetAudioChannelCount(trackIndex); }
        public string GetAudioLanguageCode(ushort trackIndex) { return videoPlayer.GetAudioLanguageCode(trackIndex); }
        public uint GetAudioSampleRate(ushort trackIndex) { return videoPlayer.GetAudioSampleRate(trackIndex); }
        public bool GetDirectAudioMute(ushort trackIndex) { return videoPlayer.GetDirectAudioMute(trackIndex); }
        public float GetDirectAudioVolume(ushort trackIndex) { return videoPlayer.GetDirectAudioVolume(trackIndex); }
        public AudioSource GetTargetAudioSource(ushort trackIndex) { return videoPlayer.GetTargetAudioSource(trackIndex); }
        public bool IsAudioTrackEnabled(ushort trackIndex) { return videoPlayer.IsAudioTrackEnabled(trackIndex); }
        public void Pause() { videoPlayer.Pause(); }
        public void Play()
        {
            if (hasPendingConfirmedUrl)
            {
                playRequestedForPendingUrl = true;
                return;
            }

            playRequestedForPendingUrl = false;
            videoPlayer.Play();
        }
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
        public void SetDirectAudioMute(ushort trackIndex, bool mute) { videoPlayer.SetDirectAudioMute(trackIndex, mute); }
        public void SetDirectAudioVolume(ushort trackIndex, float volume) { videoPlayer.SetDirectAudioVolume(trackIndex, volume); }
        public void SetTargetAudioSource(ushort trackIndex, AudioSource source) { videoPlayer.SetTargetAudioSource(trackIndex, source); }
        public void StepForward() { videoPlayer.StepForward(); }
        public void Stop() { videoPlayer.Stop(); }
        //
        // Events
        //
        public event TimeEventHandlerShim clockResyncOccurred;
        public event ErrorEventHandlerShim errorReceived;
        public event EventHandlerShim frameDropped;
        public event FrameReadyEventHandlerShim frameReady;
        public event EventHandlerShim loopPointReached;
        public event EventHandlerShim prepareCompleted;
        public event EventHandlerShim seekCompleted;
        public event EventHandlerShim started;
        //
        // Delegates
        //
        public delegate void ErrorEventHandlerShim(VideoPlayerShim source, string message);
        public delegate void EventHandlerShim(VideoPlayerShim source);
        public delegate void FrameReadyEventHandlerShim(VideoPlayerShim source, long frameIndex);
        public delegate void TimeEventHandlerShim(VideoPlayerShim source, double seconds);
        //
        // Event Invokers
        //
        private void OnClockResyncOccurred(VideoPlayer source, double seconds)
        {
            clockResyncOccurred?.Invoke(this, seconds);
        }
        private void OnErrorReceived(VideoPlayer source, string message)
        {
            errorReceived?.Invoke(this, message);
        }
        private void OnFrameDropped(VideoPlayer source)
        {
            frameDropped?.Invoke(this);
        }
        private void OnFrameReady(VideoPlayer source, long frameIndex)
        {
            frameReady?.Invoke(this, frameIndex);
        }
        private void OnLoopPointReached(VideoPlayer source)
        {
            loopPointReached?.Invoke(this);
        }
        private void OnPrepareCompleted(VideoPlayer source)
        {
            prepareCompleted?.Invoke(this);
        }
        private void OnSeekCompleted(VideoPlayer source)
        {
            seekCompleted?.Invoke(this);
        }
        private void OnStarted(VideoPlayer source)
        {
            started?.Invoke(this);
        }
        //
        // URL Confirmation Logic
        //
        private void ApplyPendingUrl(string url)
        {
            BasisDebug.Log($"[VideoPlayerShim] Setting URL to \"{url}\"", BasisDebug.LogTag.Shims);
            videoPlayer.url = url;
            hasPendingConfirmedUrl = false;
            pendingConfirmedUrl = string.Empty;
            if (pendingUrlTimeoutCoroutine != null)
            {
                StopCoroutine(pendingUrlTimeoutCoroutine);
                pendingUrlTimeoutCoroutine = null;
            }
            if (playRequestedForPendingUrl)
            {
                playRequestedForPendingUrl = false;
                videoPlayer.Play();
            }
            if (prepareRequestedForPendingUrl)
            {
                prepareRequestedForPendingUrl = false;
                videoPlayer.Prepare();
            }
        }

        private void ClearPendingUrlRequest()
        {
            hasPendingConfirmedUrl = false;
            pendingConfirmedUrl = string.Empty;
            prepareRequestedForPendingUrl = false;
            playRequestedForPendingUrl = false;
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

            BasisDebug.Log($"[VideoPlayerShim] Timed out waiting for approval: {pendingConfirmedUrl}", BasisDebug.LogTag.Shims);
            AutoDenyPendingUrlRequest();
        }

        private void AutoDenyPendingUrlRequest()
        {
            string url = pendingConfirmedUrl;
            BasisMenuDialoguePanel dialogue = BasisMainMenu.Instance != null ? BasisMainMenu.Instance.Dialogue : null;
            if (dialogue != null &&
                dialogue.Title == "Video Player URL" &&
                !string.IsNullOrEmpty(dialogue.Description) &&
                dialogue.Description.Contains(url))
            {
                dialogue.Callback?.Invoke(false);
                BasisMainMenu.Instance.Dialogue = null;
                dialogue.ReleaseInstance();
            }
            ClearPendingUrlRequest();
        }
    }
}