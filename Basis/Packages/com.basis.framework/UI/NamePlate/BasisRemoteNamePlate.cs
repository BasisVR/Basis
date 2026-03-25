using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading;
using TMPro;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public class BasisRemoteNamePlate : BasisInteractableObject
    {
        public SpriteRenderer LoadingBar;
        public MeshFilter Filter;
        public TextMeshPro LoadingText;
        public BasisRemotePlayer BasisRemotePlayer;
        public bool HasRendererCheckWiredUp = false;

        private int _isVisible = 1; // 1 = true, 0 = false
        public bool IsVisible
        {
            get => Interlocked.CompareExchange(ref _isVisible, 1, 1) == 1;
            private set => Interlocked.Exchange(ref _isVisible, value ? 1 : 0);
        }

        public bool HasProgressBarVisible = false;
        public Mesh bakedMesh;
        public MeshRenderer Renderer;
        public Color CurrentColor;
        public Transform Self;

        private static readonly int ColorId = Shader.PropertyToID("_BaseColor"); // or "_Color" for Built-in RP
        private MaterialPropertyBlock mpb;

        // --------- Chat text display above nameplate ---------
        /// <summary>
        /// TextMeshPro component for displaying chat messages above the nameplate.
        /// Created dynamically at runtime positioned above the name mesh.
        /// </summary>
        public TextMeshPro ChatText;

        /// <summary>
        /// The MeshFilter for the chat text bubble background.
        /// </summary>
        public MeshFilter ChatBubbleFilter;

        /// <summary>
        /// The MeshRenderer for the chat text bubble.
        /// </summary>
        public MeshRenderer ChatBubbleRenderer;

        /// <summary>
        /// Time when the current chat message was set, for auto-clear.
        /// </summary>
        private double chatMessageSetTime;

        /// <summary>
        /// Whether there is an active chat message being displayed.
        /// </summary>
        private bool hasChatMessage;

        // --------- Update-driven "talk pulse" state (replaces coroutine) ---------
        private bool isPulsingTalk;
        private double talkStartTime;
        private Color talkColorCached;
        /// <summary>
        /// can only be called once after that the text is nuked and a mesh render is just used with a filter
        /// </summary>
        public void Initalize(BasisRemotePlayer RemotePlayer)
        {
            BasisRemotePlayer = RemotePlayer;
            BasisRemotePlayer.RemoteNamePlate = this;
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
            BasisRemotePlayer.AudioReceived += OnAudioReceived;
            BasisRemotePlayer.OnAvatarSwitched += RebuildRenderCheck;

            Self = this.transform;
            Self.localScale = new Vector3(0.02f, 0.02f, 0.02f) * BasisRemoteNamePlateDriver.NamePlateSize;
            BasisRemoteNamePlateDriver.Instance.GenerateTextFactory(BasisRemotePlayer, this);
            LoadingText.enableVertexGradient = false;
            mpb = new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(mpb, 0);
            BasisRemoteNamePlateDriver.Register(this);

            // Create chat text display above nameplate
            CreateChatTextDisplay();

            if (!BasisRemoteNamePlateDriver.ShouldPlateBeActive(this))
            {
                gameObject.SetActive(false);
            }
        }
        private void SetPlateColor(Color c)
        {
            mpb.SetColor(ColorId, c);
            Renderer.SetPropertyBlock(mpb, 0);
        }
        private void CreateChatTextDisplay()
        {
            // Create the chat bubble background object
            GameObject chatBubbleObj = new GameObject("ChatBubble");
            chatBubbleObj.transform.SetParent(Self, false);
            chatBubbleObj.transform.localPosition = new Vector3(0, 12f, 0);
            chatBubbleObj.transform.localRotation = Quaternion.identity;
            chatBubbleObj.transform.localScale = Vector3.one;
            chatBubbleObj.layer = gameObject.layer;

            ChatBubbleFilter = chatBubbleObj.AddComponent<MeshFilter>();
            ChatBubbleRenderer = chatBubbleObj.AddComponent<MeshRenderer>();

            if (BasisRemoteNamePlateDriver.Instance != null)
            {
                ChatBubbleRenderer.material = BasisRemoteNamePlateDriver.Instance.SelectedNamePlateMaterial;
                ChatBubbleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ChatBubbleRenderer.receiveShadows = false;
                ChatBubbleRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            }
            chatBubbleObj.SetActive(false);

            // Create the chat text TMP object
            GameObject chatTextObj = new GameObject("ChatText");
            chatTextObj.transform.SetParent(Self, false);
            // Position above the nameplate (nameplate is at y=0, half height ~4.5 units)
            chatTextObj.transform.localPosition = new Vector3(0, 12f, 0.04f);
            chatTextObj.transform.localRotation = Quaternion.Euler(0, 180, 0);
            chatTextObj.transform.localScale = Vector3.one;
            chatTextObj.layer = gameObject.layer;

            ChatText = chatTextObj.AddComponent<TextMeshPro>();
            ChatText.alignment = TextAlignmentOptions.Center;
            ChatText.fontSize = 28;
            ChatText.enableAutoSizing = true;
            ChatText.fontSizeMin = 14;
            ChatText.fontSizeMax = 28;
            ChatText.color = Color.white;
            ChatText.textWrappingMode =  TextWrappingModes.Normal;
            ChatText.overflowMode = TextOverflowModes.Truncate;

            // Use same font as the loading text if available
            if (LoadingText != null && LoadingText.font != null)
            {
                ChatText.font = LoadingText.font;
            }

            // Size the rect to fit above nameplate
            RectTransform chatRect = ChatText.GetComponent<RectTransform>();
            chatRect.sizeDelta = new Vector2(58, 10);

            chatTextObj.SetActive(false);
        }

        public void DeInitalize()
        {
            BasisRemoteNamePlateDriver.Unregister(this);
            if (BasisRemotePlayer != null)
            {
                // Unsubscribe all events we hooked up
                BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
                BasisRemotePlayer.AudioReceived -= OnAudioReceived;
                BasisRemotePlayer.OnAvatarSwitched -= RebuildRenderCheck;
            }

            // Clean up chat display
            if (ChatText != null) Destroy(ChatText.gameObject);
            if (ChatBubbleFilter != null) Destroy(ChatBubbleFilter.gameObject);
            hasChatMessage = false;

            // Clean up rendering resources
            DeInitalizeCallToRender();

            // Stop any active pulse
            isPulsingTalk = false;
        }

        public void RebuildRenderCheck()
        {
            if (HasRendererCheckWiredUp)
            {
                DeInitalizeCallToRender();
            }

            HasRendererCheckWiredUp = false;

            if (BasisRemotePlayer != null && BasisRemotePlayer.FaceRenderer != null)
            {
                BasisRemotePlayer.FaceRenderer.Check += UpdateFaceVisibility;
                BasisRemotePlayer.FaceRenderer.DestroyCalled += AvatarUnloaded;

                UpdateFaceVisibility(BasisRemotePlayer.FaceIsVisible);
                HasRendererCheckWiredUp = true;
            }
        }

        private void AvatarUnloaded()
        {
            UpdateFaceVisibility(true);
        }

        private void UpdateFaceVisibility(bool State)
        {
            IsVisible = State;
            gameObject.SetActive(BasisRemoteNamePlateDriver.ShouldPlateBeActive(this));

            // If we get hidden, just stop the pulse (avoids Update doing work on hidden plate)
            if (!State)
            {
                isPulsingTalk = false;
            }
        }

        public void OnAudioReceived()
        {
            if (!IsVisible) return;

            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (this == null || !isActiveAndEnabled) return;

                // pick the "talking" pulse color
                talkColorCached = BasisRemotePlayer.OutOfRangeFromLocal
                    ? BasisRemoteNamePlateDriver.StaticOutOfRangeColor
                    : BasisRemoteNamePlateDriver.StaticIsTalkingColor;

                // Start pulse timeline
                talkStartTime = Time.timeAsDouble;
                isPulsingTalk = true;

                // Stage 1: snap to talk color
                SetPlateColor(talkColorCached);
            });
        }
        internal bool GetIsPulsingForJob() => isPulsingTalk;
        internal double GetTalkStartTimeForJob() => talkStartTime;
        internal Color GetTalkColorForJob() => talkColorCached;
        internal void StopPulseFromJob()
        {
            isPulsingTalk = false;
        }

        internal void ApplyColorFromJob(Color c)
        {
            SetPlateColor(c);
            CurrentColor = c;
        }

        /// <summary>
        /// Sets the chat text to display above the nameplate.
        /// Empty or null clears the chat text.
        /// </summary>
        public void SetChatText(string message)
        {
            if (ChatText == null) return;

            if (string.IsNullOrEmpty(message))
            {
                ChatText.gameObject.SetActive(false);
                if (ChatBubbleFilter != null)
                    ChatBubbleFilter.gameObject.SetActive(false);
                hasChatMessage = false;
                return;
            }

            ChatText.text = message;
            ChatText.gameObject.SetActive(true);

            // Rebuild chat bubble background to fit text
            if (ChatBubbleFilter != null && BasisRemoteNamePlateDriver.Instance != null)
            {
                BasisRemoteNamePlateDriver.Instance.GenerateChatBubble(this);
                ChatBubbleFilter.gameObject.SetActive(true);
            }

            chatMessageSetTime = Time.timeAsDouble;
            hasChatMessage = true;
        }

        /// <summary>
        /// Called each frame to check if chat message should auto-clear.
        /// </summary>
        public void UpdateChatTimeout()
        {
            if (!hasChatMessage) return;

            if (Time.timeAsDouble - chatMessageSetTime >= BasisNetworkHandleChat.MessageDisplayDuration)
            {
                SetChatText(null);
            }
        }

        public void DeInitalizeCallToRender()
        {
            if (HasRendererCheckWiredUp && BasisRemotePlayer != null && BasisRemotePlayer.FaceRenderer != null)
            {
                BasisRemotePlayer.FaceRenderer.Check -= UpdateFaceVisibility;
                BasisRemotePlayer.FaceRenderer.DestroyCalled -= AvatarUnloaded;
            }
        }
        public void ProgressReport(string UniqueID, float progress, string info)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (progress == 100)
                {
                    LoadingText.gameObject.SetActive(false);
                    LoadingBar.gameObject.SetActive(false);
                    HasProgressBarVisible = false;
                }
                else
                {
                    if (HasProgressBarVisible == false)
                    {
                        LoadingBar.gameObject.SetActive(true);
                        LoadingText.gameObject.SetActive(true);
                        HasProgressBarVisible = true;
                    }

                    if (LoadingText.text != info)
                    {
                        LoadingText.text = info;
                    }

                    Vector2 scale = LoadingBar.size;
                    float NewX = progress / 2;
                    if (scale.x != NewX)
                    {
                        scale.x = NewX;
                        LoadingBar.size = scale;
                    }
                }
            });
        }
        public override bool CanHover(BasisInput input)
        {
            return InteractableEnabled &&
                Inputs.IsInputAdded(input) &&
                input.TryGetRole(out BasisBoneTrackedRole role) &&
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
                found.GetState() == BasisInteractInputState.Ignored &&
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
        }
        public override bool CanInteract(BasisInput input)
        {
            return InteractableEnabled &&
                Inputs.IsInputAdded(input) &&
                input.TryGetRole(out BasisBoneTrackedRole role) &&
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
                found.GetState() == BasisInteractInputState.Hovering &&
                IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
        }
        public override void OnHoverStart(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            if (found != null && found.Value.GetState() != BasisInteractInputState.Ignored)
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " input state is not ignored OnHoverStart, this shouldn't happen");

            var added = Inputs.ChangeStateByRole(found.Value.Role, BasisInteractInputState.Hovering);
            if (!added)
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " did not find role for input on hover");

            OnHoverStartEvent?.Invoke(input);
            HighlightObject(true);
        }
        public override void OnHoverEnd(BasisInput input, bool willInteract)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out _))
            {
                if (!willInteract)
                {
                    if (!Inputs.ChangeStateByRole(role, BasisInteractInputState.Ignored))
                    {
                        BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " found input by role but could not remove by it, this is a bug.");
                    }
                }
                OnHoverEndEvent?.Invoke(input, willInteract);
                HighlightObject(false);
            }
        }
        public override void OnInteractStart(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                // same input that was highlighting previously
                if (wrapper.GetState() == BasisInteractInputState.Hovering)
                {
                    WasPressed(input);
                    OnInteractStartEvent?.Invoke(input);
                }
                else
                {
                    Debug.LogWarning("Input source interacted with ReparentInteractable without highlighting first.");
                }
            }
            else
            {
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " did not find role for input on Interact start");
            }
        }
        public override void OnInteractEnd(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                if (wrapper.GetState() == BasisInteractInputState.Interacting)
                {
                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);
                    OnInteractEndEvent?.Invoke(input);
                }
            }
        }
        public void HighlightObject(bool IsHighlighted)
        {
        }
        public void WasPressed(BasisInput input)
        {
            if (BasisRemotePlayer != null && BasisMainMenu.ActiveMenuTitle != IndividualPlayerProvider.StaticTitle)
            {
                BasisMainMenu.Close();
                input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume);
                IndividualPlayerProvider.remotePlayer = BasisRemotePlayer;
                BasisMainMenu.OpenWithProvider(IndividualPlayerProvider.StaticTitle);
            }
        }
        public override bool IsInteractingWith(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
        }
        public override bool IsHoveredBy(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
        }
        public override void InputUpdate()
        {
        }
        public override bool IsInteractTriggered(BasisInput input)
        {
            // click or mostly triggered
            return HasState(input.CurrentInputState, InputKey);
        }
    }
}
