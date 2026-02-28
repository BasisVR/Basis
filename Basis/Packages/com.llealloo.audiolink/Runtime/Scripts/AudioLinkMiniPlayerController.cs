#if UDONSHARP
using UdonSharp;

using UnityEngine;
using UnityEngine.UI;

using VRC.SDK3.Components;
using VRC.SDK3.Components.Video;
using VRC.SDKBase;
using VRC.Udon;

// #if UNITY_EDITOR && !COMPILER_UDONSHARP
// using UnityEditor;
// using UnityEditorInternal;
// using UdonSharpEditor;
// #endif

namespace AudioLink
{
    [AddComponentMenu("AudioLink/UI/AudioLink Mini Player Controller")]
    public class AudioLinkMiniPlayerController : UdonSharpBehaviour
    {
        public AudioLinkMiniPlayer videoPlayer;

        public VRCUrlInputField urlInput;
        public GameObject urlInputControl;
        public GameObject progressSliderControl;

        public Image stopIcon;
        public Image lockedIcon;
        public Image unlockedIcon;
        public Image loadIcon;
        public Image syncIcon;

        public Slider progressSlider;
        public Text statusText;
        public Text urlText;
        public Text placeholderText;

        Color normalColor = new Color(1f, 1f, 1f, .8f);
        Color disabledColor = new Color(.5f, .5f, .5f, .4f);
        Color activeColor = new Color(1f, .8f, 0f, .7f);
        Color attentionColor = new Color(.9f, 0f, 0f, .5f);

        const int PLAYER_STATE_STOPPED = 0;
        const int PLAYER_STATE_LOADING = 1;
        const int PLAYER_STATE_PLAYING = 2;
        const int PLAYER_STATE_ERROR = 3;

        string statusOverride = null;
        string instanceMaster = "";
        string instanceOwner = "";

        bool loadActive = false;
        VRCUrl pendingSubmit;
        bool pendingFromLoadOverride = false;

        private void Start()
        {
#if !UNITY_EDITOR
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (VRC.SDKBase.Utilities.IsValid(owner) && owner.IsValid())
                instanceMaster = owner.displayName;
#endif

            stopIcon.color = normalColor;
            lockedIcon.color = normalColor;
            unlockedIcon.color = normalColor;
            loadIcon.color = normalColor;
            syncIcon.color = normalColor;
        }

        public void _HandleUrlInput()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            pendingFromLoadOverride = loadActive;
            pendingSubmit = urlInput.GetUrl();

            SendCustomEventDelayedSeconds("_HandleUrlInputDelay", 0.5f);
        }

        public void _HandleUrlInputDelay()
        {
            VRCUrl url = urlInput.GetUrl();
            urlInput.SetUrl(VRCUrl.Empty);

            // Hack to get around Unity always firing OnEndEdit event for submit and lost focus
            // If loading override was on, but it's off immediately after submit, assume user closed override
            // instead of submitting.  Half second delay is a crude defense against a UI race.
            if (pendingFromLoadOverride && !loadActive)
                return;

            videoPlayer._ChangeUrl(url);
            loadActive = false;
        }

        public void _HandleUrlInputClick()
        {
            if (!videoPlayer._CanTakeControl())
                _SetStatusOverride(MakeOwnerMessage(), 3);
        }

        public void _HandleUrlInputChange()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            VRCUrl url = urlInput.GetUrl();
            if (url.Get().Length > 0)
                videoPlayer._UpdateQueuedUrl(urlInput.GetUrl());
        }

        public void _HandleSync()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            videoPlayer._Resync();
        }

        public void _HandleStop()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            if (videoPlayer._CanTakeControl())
                videoPlayer._TriggerStop();
            else
                _SetStatusOverride(MakeOwnerMessage(), 3);
        }

        public void _HandleLock()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            if (videoPlayer._CanTakeControl())
                videoPlayer._TriggerLock();
            else
                _SetStatusOverride(MakeOwnerMessage(), 3);
        }

        public void _HandleLoad()
        {
            if (!VRC.SDKBase.Utilities.IsValid(videoPlayer))
                return;

            if (!videoPlayer._CanTakeControl())
            {
                _SetStatusOverride(MakeOwnerMessage(), 3);
                return;
            }

            if (videoPlayer.localPlayerState == PLAYER_STATE_ERROR)
                loadActive = false;
            else
                loadActive = !loadActive;
        }

        bool _draggingProgressSlider = false;

        public void _HandleProgressBeginDrag()
        {
            _draggingProgressSlider = true;
        }

        public void _HandleProgressEndDrag()
        {
            _draggingProgressSlider = false;
        }

        public void _HandleProgressSliderChanged()
        {
            if (!_draggingProgressSlider)
                return;

            if (float.IsInfinity(videoPlayer.trackDuration) || videoPlayer.trackDuration <= 0)
                return;

            float targetTime = videoPlayer.trackDuration * progressSlider.value;
            videoPlayer._SetTargetTime(targetTime);
        }

        void _SetStatusOverride(string msg, float timeout)
        {
            statusOverride = msg;
            SendCustomEventDelayedSeconds("_ClearStatusOverride", timeout);
        }

        public void _ClearStatusOverride()
        {
            statusOverride = null;
        }

        // TODO: This is branchy and repetetive.  Try to pull as much out of constant update loop as possible once player can signal
        // a suitable update event.

        private void Update()
        {
            bool canControl = videoPlayer._CanTakeControl();
            bool enableControl = !videoPlayer.locked || canControl;

            if (videoPlayer.localPlayerState == PLAYER_STATE_PLAYING && !loadActive)
            {
                urlInput.readOnly = true;
                urlInputControl.SetActive(false);

                stopIcon.color = enableControl ? normalColor : disabledColor;
                loadIcon.color = enableControl ? normalColor : disabledColor;
                syncIcon.color = normalColor;

                if (!videoPlayer.seekableSource)
                {
                    SetStatusText("Streaming...");
                    progressSliderControl.SetActive(false);
                }
                else if (_draggingProgressSlider)
                {
                    string durationStr = System.TimeSpan.FromSeconds(videoPlayer.trackDuration).ToString(@"hh\:mm\:ss");
                    string positionStr = System.TimeSpan.FromSeconds(videoPlayer.trackDuration * progressSlider.value).ToString(@"hh\:mm\:ss");
                    SetStatusText(positionStr + "/" + durationStr);
                    progressSliderControl.SetActive(true);
                }
                else
                {
                    string durationStr = System.TimeSpan.FromSeconds(videoPlayer.trackDuration).ToString(@"hh\:mm\:ss");
                    string positionStr = System.TimeSpan.FromSeconds(videoPlayer.trackPosition).ToString(@"hh\:mm\:ss");
                    SetStatusText(positionStr + "/" + durationStr);
                    progressSliderControl.SetActive(true);
                    progressSlider.value = (videoPlayer.trackDuration <= 0) ? 0f : Mathf.Clamp01(videoPlayer.trackPosition / videoPlayer.trackDuration);
                }
                progressSlider.interactable = enableControl;
            }
            else
            {
                _draggingProgressSlider = false;

                stopIcon.color = disabledColor;
                loadIcon.color = disabledColor;
                progressSliderControl.SetActive(false);
                urlInputControl.SetActive(true);

                if (videoPlayer.localPlayerState == PLAYER_STATE_LOADING)
                {
                    stopIcon.color = enableControl ? normalColor : disabledColor;
                    loadIcon.color = enableControl ? normalColor : disabledColor;
                    syncIcon.color = normalColor;

                    SetPlaceholderText("Loading...");
                    urlInput.readOnly = true;
                    SetStatusText("");
                }
                else if (videoPlayer.localPlayerState == PLAYER_STATE_ERROR)
                {
                    stopIcon.color = disabledColor;
                    loadIcon.color = normalColor;
                    syncIcon.color = normalColor;
                    loadActive = false;

                    switch (videoPlayer.localLastErrorCode)
                    {
                        case VideoError.RateLimited:
                            SetPlaceholderText("Rate limited, wait and try again");
                            break;
                        case VideoError.PlayerError:
                            SetPlaceholderText("Video player error");
                            break;
                        case VideoError.InvalidURL:
                            SetPlaceholderText("Invalid URL or source offline");
                            break;
                        case VideoError.AccessDenied:
                            SetPlaceholderText("Video blocked, enable untrusted URLs");
                            break;
                        case VideoError.Unknown:
                        default:
                            SetPlaceholderText("Failed to load video");
                            break;
                    }

                    urlInput.readOnly = !canControl;
                    SetStatusText("");
                }
                else if (videoPlayer.localPlayerState == PLAYER_STATE_STOPPED || videoPlayer.localPlayerState == PLAYER_STATE_PLAYING)
                {
                    if (videoPlayer.localPlayerState == PLAYER_STATE_STOPPED)
                    {
                        loadActive = false;
                        pendingFromLoadOverride = false;
                        stopIcon.color = disabledColor;
                        loadIcon.color = disabledColor;
                        syncIcon.color = disabledColor;
                    }
                    else
                    {
                        stopIcon.color = normalColor;
                        loadIcon.color = activeColor;
                        syncIcon.color = normalColor;
                    }

                    urlInput.readOnly = !canControl;
                    if (canControl)
                    {
                        SetPlaceholderText("Enter Video URL...");
                        SetStatusText("");
                    }
                    else
                    {
                        SetPlaceholderText("");
                        SetStatusText(MakeOwnerMessage());
                    }

                }
            }

            lockedIcon.enabled = videoPlayer.locked;
            unlockedIcon.enabled = !videoPlayer.locked;
            if (videoPlayer.locked)
                lockedIcon.color = canControl ? normalColor : attentionColor;
        }

        void SetStatusText(string msg)
        {
            if (statusOverride != null)
                statusText.text = statusOverride;
            else
                statusText.text = msg;
        }

        void SetPlaceholderText(string msg)
        {
            if (statusOverride != null)
                placeholderText.text = "";
            else
                placeholderText.text = msg;
        }

        void FindOwners()
        {
            int playerCount = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi[] playerList = new VRCPlayerApi[playerCount];
            playerList = VRCPlayerApi.GetPlayers(playerList);

            foreach (VRCPlayerApi player in playerList)
            {
                if (!VRC.SDKBase.Utilities.IsValid(player) || !player.IsValid())
                    continue;
                if (player.isInstanceOwner)
                    instanceOwner = player.displayName;
                if (player.isMaster)
                    instanceMaster = player.displayName;
            }
        }

        string MakeOwnerMessage()
        {
            if (instanceMaster == instanceOwner || instanceOwner == "")
                return $"Controls locked to master {instanceMaster}";
            else
                return $"Controls locked to master {instanceMaster} and owner {instanceOwner}";
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (VRC.SDKBase.Utilities.IsValid(owner) && owner.IsValid())
                instanceMaster = owner.displayName;
        }
    }
}
#endif
