using Basis.BasisUI;
using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public class BasisRemoteNamePlate : BasisInteractableObject
    {
        public SpriteRenderer LoadingBar;
        public MeshFilter Filter;
        public TextMeshPro LoadingText;
        [System.NonSerialized] public BasisRemotePlayer BasisRemotePlayer;
        public bool HasRendererCheckWiredUp = false;

        private int _isVisible = 1; // 1 = true, 0 = false
        public bool IsVisible
        {
            get => Volatile.Read(ref _isVisible) == 1;
            private set => Volatile.Write(ref _isVisible, value ? 1 : 0);
        }
        /// <summary>Raw int for job gather — avoids bool→ushort conversion.</summary>
        internal int IsVisibleRaw => Volatile.Read(ref _isVisible);

        /// <summary>Slot in BasisRemoteNamePlateDriver's plates/jobStates arrays; -1 until registered.
        /// Maintained by ApplyPendingStructuralChanges, including swap-back moves.</summary>
        internal int RegistryIndex = -1;

        /// <summary>Cached gameObject-active state for the global merge gather, so it never calls the
        /// (managed→native) isActiveAndEnabled per plate per frame. Maintained by
        /// <see cref="Initialize"/> and <see cref="RefreshActiveState"/>.</summary>
        internal bool RenderActive;

        public bool HasProgressBarVisible = false;
        public MeshRenderer Renderer;
        public Color CurrentColor;
        public Transform Self;

        // ---- Global single-draw nameplate parts (consumed by BasisGlobalNamePlateRenderer) ----
        // When the global path is active these hold this plate's baked geometry in plate-local
        // space; the per-plate MeshRenderer above is disabled and the merge draws them instead.
        internal Mesh GlobalPanelMesh;
        internal Mesh[] GlobalTextMeshes;
        internal Material[] GlobalTextMaterials;
        internal bool HasGlobalParts;

        /// <summary>True when this plate is baked and active, so the global merge should include it.</summary>
        internal bool IsGloballyRenderable => HasGlobalParts && Self != null && isActiveAndEnabled;

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
        private string currentChatMessage;
        private readonly string[] currentChatMessageWithTyping = new string[TypingIndicatorFrames.Length];
        private bool wantsTypingIndicator;
        private int typingAnimationFrame = -1;
        private double typingAnimationStartTime;
        private string typingIndicatorText = "...";
        private string visibleChatText;

        // ---- Overlay limiter state (see BasisNamePlateOverlayLimiter) ----
        // Culled overlays keep their message/progress state but deactivate their objects and
        // skip every text/mesh write until the nearest-K ranking readmits them.
        private bool chatOverlayCulled;
        private bool loadingOverlayCulled;
        private double chatDisplayLastActiveTime;
        private int loadingTextBucket = int.MinValue;

        /// <summary>True while a chat message or typing indicator wants to display.</summary>
        internal bool HasActiveChatOverlay => ChatText != null && HasBubbleText();

        /// <summary>True while the avatar-loading text + bar want to display.</summary>
        internal bool HasActiveLoadingOverlay => HasProgressBarVisible;

        internal void SetChatOverlayCulled(bool culled)
        {
            if (chatOverlayCulled == culled)
            {
                return;
            }
            chatOverlayCulled = culled;
            RefreshChatLayout();
        }

        internal void SetLoadingOverlayCulled(bool culled)
        {
            if (loadingOverlayCulled == culled)
            {
                return;
            }
            loadingOverlayCulled = culled;
            if (!culled)
            {
                // Force the next progress report to rewrite the label — its text went stale
                // while writes were skipped.
                loadingTextBucket = int.MinValue;
            }
            ApplyLoadingOverlayActive();
        }

        private void ApplyLoadingOverlayActive()
        {
            bool show = HasProgressBarVisible && !loadingOverlayCulled;
            if (LoadingText != null && LoadingText.gameObject.activeSelf != show)
            {
                LoadingText.gameObject.SetActive(show);
            }
            if (LoadingBar != null && LoadingBar.gameObject.activeSelf != show)
            {
                LoadingBar.gameObject.SetActive(show);
            }
        }

        private static readonly string[] TypingIndicatorFrames =
        {
            ".",
            "..",
            "..."
        };

        // --------- Update-driven "talk pulse" state (replaces coroutine) ---------
        private bool isPulsingTalk;
        private double talkStartTime;
        private Color talkColorCached;
        private float4 talkColorFloat4;
        private float4 restingColorFloat4;
        /// <summary>
        /// can only be called once after that the text is nuked and a mesh render is just used with a filter
        /// </summary>
        public void Initialize(BasisRemotePlayer RemotePlayer)
        {
            BasisRemotePlayer = RemotePlayer;
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
            BasisRemotePlayer.AudioReceived += OnAudioReceived;
            BasisRemotePlayer.OnAvatarSwitched += RebuildRenderCheck;

            BasisRemotePlayer.OnAvatarFailedStateChanged += RefreshFailedStateColor;
            BasisRemotePlayer.OnChatMessageReceived += SetChatText;
            BasisRemotePlayer.OnChatTypingStateChanged += SetTypingIndicatorVisible;
            BasisRemotePlayer.OnNamePlateActiveStateShouldRefresh += RefreshActiveState;
            BasisRemotePlayer.OnRemotePlayerDestroying += HandlePlayerDestroying;
            BasisRemotePlayer.OnTalkModeChanged += HandleTalkModeChanged;
            BasisRemotePlayer.NamePlateTransformProvider = GetSelfTransform;

            Self = this.transform;
            Self.localScale = Vector3.one * BasisRemoteNamePlateDriver.PlateWorldScale();

            // Global path renders the name through the shared merged mesh; the per-plate
            // renderer stays off (the BoxCollider still drives interaction independently).
            if (BasisRemoteNamePlateDriver.UseGlobalNamePlateMesh && Renderer != null)
            {
                Renderer.enabled = false;
            }

            BasisRemoteNamePlateDriver.QueueTextBake(BasisRemotePlayer, this);
            LoadingText.enableVertexGradient = false;
            mpb = new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(mpb, 0);
            ApplyTalkModeColors();
            BasisRemoteNamePlateDriver.Register(this);

            SetTypingIndicatorVisible(BasisRemotePlayer.IsChatTyping);

            RenderActive = BasisRemoteNamePlateDriver.ShouldPlateBeActive(this);
            if (!RenderActive)
            {
                gameObject.SetActive(false);
            }
            PushPoseGate(RenderActive);

            _ = LoadBlockStateAsync();
        }

        private Transform GetSelfTransform() => Self;

        /// <summary>
        /// Stores freshly baked global-render parts, releasing any previous ones. Called by
        /// <see cref="BasisRemoteNamePlateDriver.BakeNameMeshGlobal"/>.
        /// </summary>
        internal void SetGlobalParts(Mesh panelMesh, Mesh[] textMeshes, Material[] textMaterials)
        {
            DestroyGlobalParts();
            GlobalPanelMesh = panelMesh;
            GlobalTextMeshes = textMeshes;
            GlobalTextMaterials = textMaterials;
            HasGlobalParts = panelMesh != null;
        }

        private void DestroyGlobalParts()
        {
            if (GlobalPanelMesh != null) Destroy(GlobalPanelMesh);
            if (GlobalTextMeshes != null)
            {
                for (int i = 0; i < GlobalTextMeshes.Length; i++)
                {
                    if (GlobalTextMeshes[i] != null) Destroy(GlobalTextMeshes[i]);
                }
            }
            GlobalPanelMesh = null;
            GlobalTextMeshes = null;
            GlobalTextMaterials = null;
            HasGlobalParts = false;
        }

        private void HandlePlayerDestroying()
        {
            if (this == null)
            {
                return;
            }
            DeInitialize();
            AddressableResourceProcess.ReleaseGameobject(gameObject);
        }

        public override void OnDestroy()
        {
            UnsubscribeFromPlayer();
            base.OnDestroy();
        }

        /// <summary>
        /// Re-evaluates and applies this nameplate's active state via
        /// <see cref="BasisRemoteNamePlateDriver.ShouldPlateBeActive"/>.
        /// </summary>
        public void RefreshActiveState()
        {
            // The avatar's renderer-visibility callback can fire mid-teardown; bail if this
            // plate has already been destroyed rather than touching its gameObject.
            if (this == null) return;
            RenderActive = BasisRemoteNamePlateDriver.ShouldPlateBeActive(this);
            gameObject.SetActive(RenderActive);
            PushPoseGate(RenderActive);
        }

        /// <summary>
        /// Tells the bone system whether this plate still needs its transform posed each frame.
        /// While it is off, <c>MappedNameplateApplyJob</c> skips the plate's slot entirely — the
        /// merged name mesh is built from the network pose rather than this transform, so the only
        /// things it drives are the child chat/loading overlays and the interaction collider, all
        /// of which are deactivated along with the plate.
        /// </summary>
        private void PushPoseGate(bool active)
        {
            BasisNetworkReceiver receiver = BasisRemotePlayer != null ? BasisRemotePlayer.NetworkReceiver : null;
            if (receiver == null) return;
            RemoteBoneJobSystem.SetNamePlateActive(receiver.playerId, active);
        }

        /// <summary>
        /// Reads the persisted block state for this player and refreshes the
        /// nameplate's active state. Fire-and-forget from <see cref="Initialize"/>.
        /// </summary>
        private async Task LoadBlockStateAsync()
        {
            if (BasisRemotePlayer == null || string.IsNullOrEmpty(BasisRemotePlayer.UUID)) return;

            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(BasisRemotePlayer.UUID);
            if (this == null || BasisRemotePlayer == null) return;

            BasisRemotePlayer.IsBlocked = settings.IsBlocked;
            BasisRemotePlayer.AlwaysShowAvatar = settings.AlwaysShowAvatar;
            RefreshActiveState();
        }
        private void SetPlateColor(Color c)
        {
            // Failed-load state pins the plate to red regardless of what the caller asked for.
            if (BasisRemotePlayer != null && BasisRemotePlayer.HasFailedAvatarLoadGlobally)
            {
                c = BasisRemoteNamePlateDriver.StaticFailedLoadColor;
            }

            // Single source of truth; the global merge reads CurrentColor each frame.
            CurrentColor = c;

            if (BasisRemoteNamePlateDriver.UseGlobalNamePlateMesh) return;

            mpb.SetColor(ColorId, c);
            Renderer.SetPropertyBlock(mpb, 0);
        }

        /// <summary>
        /// Immediately re-applies the plate color based on the current failed-load state.
        /// Call when the player's <see cref="BasisRemotePlayer.HasFailedAvatarLoadGlobally"/>
        /// flag changes so the visual updates without waiting for the next pulse tick.
        /// </summary>
        public void RefreshFailedStateColor()
        {
            if (mpb == null) return;
            if (BasisRemotePlayer == null) return;

            if (BasisRemotePlayer.HasFailedAvatarLoadGlobally)
            {
                // Kill any in-flight talking pulse so the job doesn't keep writing over red.
                isPulsingTalk = false;
                Color red = BasisRemoteNamePlateDriver.StaticFailedLoadColor;
                SetPlateColor(red);
            }
            else
            {
                Color normal = BasisRemoteNamePlateDriver.GetModeRestingColor(BasisRemotePlayer != null ? BasisRemotePlayer.TalkMode : BasisTalkMode.Normal);
                SetPlateColor(normal);
            }
            BasisRemoteNamePlateDriver.SyncPlateJobState(this);
        }
        private void EnsureChatDisplayCreated()
        {
            if (ChatText == null)
            {
                CreateChatTextDisplay();
            }
        }

        private void CreateChatTextDisplay()
        {
            // Create the chat bubble background object
            GameObject chatBubbleObj = new GameObject("ChatBubble");
            chatBubbleObj.transform.SetParent(Self, false);
            chatBubbleObj.transform.SetLocalPositionAndRotation(new Vector3(0, 12f, 0), Quaternion.identity);
            chatBubbleObj.transform.localScale = Vector3.one;
            chatBubbleObj.layer = gameObject.layer;

            ChatBubbleFilter = chatBubbleObj.AddComponent<MeshFilter>();
            ChatBubbleRenderer = chatBubbleObj.AddComponent<MeshRenderer>();

            if (BasisRemoteNamePlateDriver.SelectedNamePlateMaterial != null)
            {
                ChatBubbleRenderer.sharedMaterial = BasisRemoteNamePlateDriver.SelectedNamePlateMaterial;
                ChatBubbleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ChatBubbleRenderer.receiveShadows = false;
                ChatBubbleRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            }
            chatBubbleObj.SetActive(false);

            // Create the chat text TMP object
            GameObject chatTextObj = new GameObject("ChatText");
            chatTextObj.transform.SetParent(Self, false);
            // Position above the nameplate (nameplate is at y=0, half height ~4.5 units)
            chatTextObj.transform.SetLocalPositionAndRotation(new Vector3(0, 12f, 0.04f), Quaternion.Euler(0, 180, 0));
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
            // Sanitized on send only, so the received text is whatever the sender transmitted.
            ChatText.richText = false;

            // Use same font as the loading text if available
            if (LoadingText != null && LoadingText.font != null)
            {
                ChatText.font = LoadingText.font;
            }

            // Size the rect to fit above nameplate
            if (ChatText.TryGetComponent(out RectTransform chatRect))
            {
                chatRect.sizeDelta = new Vector2(58, 10);
            }

            // Overlay text — never part of shadows, probes or per-object motion vectors.
            if (chatTextObj.TryGetComponent(out MeshRenderer chatTextRenderer))
            {
                chatTextRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                chatTextRenderer.receiveShadows = false;
                chatTextRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                chatTextRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                chatTextRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }

            chatTextObj.SetActive(false);
            chatDisplayLastActiveTime = Time.timeAsDouble;
            chatOverlayCulled = false;
        }

        private void UnsubscribeFromPlayer()
        {
            if (BasisRemotePlayer == null)
            {
                return;
            }
            // Unsubscribe all events we hooked up
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
            BasisRemotePlayer.AudioReceived -= OnAudioReceived;
            BasisRemotePlayer.OnAvatarSwitched -= RebuildRenderCheck;

            BasisRemotePlayer.OnAvatarFailedStateChanged -= RefreshFailedStateColor;
            BasisRemotePlayer.OnChatMessageReceived -= SetChatText;
            BasisRemotePlayer.OnChatTypingStateChanged -= SetTypingIndicatorVisible;
            BasisRemotePlayer.OnNamePlateActiveStateShouldRefresh -= RefreshActiveState;
            BasisRemotePlayer.OnRemotePlayerDestroying -= HandlePlayerDestroying;
            BasisRemotePlayer.OnTalkModeChanged -= HandleTalkModeChanged;
            if (BasisRemotePlayer.NamePlateTransformProvider == GetSelfTransform)
            {
                BasisRemotePlayer.NamePlateTransformProvider = null;
            }
        }

        public void DeInitialize()
        {
            BasisRemoteNamePlateDriver.Unregister(this);
            // Drop out of the global merge gather immediately: snapArr can still hold this plate until
            // its deferred topology rebuild, and a recycled playerId must not resurface our name on the
            // new player. Replaces the per-plate Unity null check in GatherFromBoneSystem.
            RenderActive = false;
            PushPoseGate(false);
            UnsubscribeFromPlayer();

            // Clean up chat display
            if (ChatBubbleFilter != null && ChatBubbleFilter.sharedMesh != null) Destroy(ChatBubbleFilter.sharedMesh);
            if (ChatText != null) Destroy(ChatText.gameObject);
            if (ChatBubbleFilter != null) Destroy(ChatBubbleFilter.gameObject);
            if (Filter != null && Filter.sharedMesh != null && Filter.sharedMesh.name == BasisRemoteNamePlateDriver.CombinedNameplateMeshName) Destroy(Filter.sharedMesh);
            DestroyGlobalParts();
            hasChatMessage = false;
            wantsTypingIndicator = false;

            // Clean up rendering resources
            DeInitializeCallToRender();

            // Stop any active pulse
            isPulsingTalk = false;
            BasisRemoteNamePlateDriver.SyncPlateJobState(this);
        }

        public void RebuildRenderCheck()
        {
            if (HasRendererCheckWiredUp)
            {
                DeInitializeCallToRender();
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
            RefreshActiveState();

            // If we get hidden, just stop the pulse (avoids Update doing work on hidden plate)
            if (!State)
            {
                isPulsingTalk = false;
            }
            BasisRemoteNamePlateDriver.SyncPlateJobState(this);
        }

        /// <summary>
        /// Returns true when audio from this player is currently audible to the local
        /// user. Main-thread only — touches Unity components (audio source volume).
        /// </summary>
        /// <remarks>
        /// Covers every state that should prevent a talking pulse:
        /// face-visibility, failed-load pin, block state (local or remote temp),
        /// audio receiver presence, out-of-range (signalled by <c>HasAudioSource==false</c>,
        /// since <see cref="Basis.Scripts.Networking.Receivers.BasisAudioReceiver.StopAudio"/>
        /// fires on the out-of-range transition), and individual-player mute
        /// (<c>audioSource.volume==0</c>, set by <c>ChangeRemotePlayersVolumeSettings</c>).
        /// Continuous audio streams from speakers the user can't hear will repeatedly
        /// fail this check and so never latch the pulse.
        /// </remarks>
        public bool CanCurrentlyBeHeard()
        {
            if (!IsVisible) return false;

            var player = BasisRemotePlayer;
            if (player == null) return false;
            if (player.HasFailedAvatarLoadGlobally) return false;
            if (player.IsEffectivelyBlocked) return false;

            var receiver = player.NetworkReceiver;
            if (receiver == null) return false;

            var audio = receiver.AudioReceiverModule;
            if (audio == null || !audio.HasAudioSource) return false;

            var src = audio.audioSource;
            if (src == null || src.volume <= 0f) return false;

            return true;
        }

        public void OnAudioReceived()
        {
            // ── Network-thread fast path ──
            // Fires at audio packet rate (~50Hz per speaker). Bail using only
            // thread-safe reads — Unity component access (audioSource.volume) is
            // deferred to the enqueued main-thread lambda below.
            if (!IsVisible) return;

            var player = BasisRemotePlayer;
            if (player == null) return;
            if (player.HasFailedAvatarLoadGlobally) return;
            if (player.IsEffectivelyBlocked) return;

            var receiver = player.NetworkReceiver;
            if (receiver == null) return;
            var audio = receiver.AudioReceiverModule;
            // HasAudioSource is volatile — false while out of range, not yet loaded, or unloaded.
            if (audio == null || !audio.HasAudioSource) return;

            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (this == null || !isActiveAndEnabled) return;

                // Re-check on the main thread: state may have changed during the
                // enqueue + drain window, and this covers the volume check that
                // can't be done safely off the main thread.
                if (!CanCurrentlyBeHeard()) return;

                talkColorCached = BasisRemoteNamePlateDriver.GetModeTalkColor(BasisRemotePlayer != null ? BasisRemotePlayer.TalkMode : BasisTalkMode.Normal);
                talkColorFloat4 = new float4(talkColorCached.r, talkColorCached.g, talkColorCached.b, talkColorCached.a);

                // Start pulse timeline
                talkStartTime = Time.timeAsDouble;
                isPulsingTalk = true;

                // Stage 1: snap to talk color
                SetPlateColor(talkColorCached);
                BasisRemoteNamePlateDriver.SyncPlateJobState(this);
            });
        }
        internal bool GetIsPulsingForJob() => isPulsingTalk;
        internal float4 GetRestingColorFloat4ForJob() => restingColorFloat4;
        internal void StopPulseFromJob()
        {
            isPulsingTalk = false;
            BasisRemoteNamePlateDriver.SyncPlateJobState(this);
        }

        internal BasisRemoteNamePlateDriver.PlateJobState BuildJobState()
        {
            return new BasisRemoteNamePlateDriver.PlateJobState
            {
                talkStartTime = talkStartTime,
                talkColor = talkColorFloat4,
                restingColor = restingColorFloat4,
                isPulsing = isPulsingTalk ? (byte)1 : (byte)0,
                isVisible = (byte)IsVisibleRaw,
            };
        }

        private void HandleTalkModeChanged()
        {
            // A mode/mute change must recolor the plate immediately, even mid-pulse,
            // so it doesn't wait for the player to talk before showing the new color.
            isPulsingTalk = false;
            ApplyTalkModeColors();
        }

        /// <summary>
        /// Recomputes this plate's resting + talking colors from the player's current
        /// talk mode and snaps to the resting color when not mid-pulse.
        /// </summary>
        public void ApplyTalkModeColors()
        {
            BasisTalkMode mode = BasisRemotePlayer != null ? BasisRemotePlayer.TalkMode : BasisTalkMode.Normal;

            Color resting = BasisRemoteNamePlateDriver.GetModeRestingColor(mode);
            restingColorFloat4 = new float4(resting.r, resting.g, resting.b, resting.a);

            Color talk = BasisRemoteNamePlateDriver.GetModeTalkColor(mode);
            talkColorCached = talk;
            talkColorFloat4 = new float4(talk.r, talk.g, talk.b, talk.a);

            if (!isPulsingTalk)
            {
                SetPlateColor(resting);
            }
            BasisRemoteNamePlateDriver.SyncPlateJobState(this);
        }

        internal void ApplyColorFromJob(Color c)
        {
            if (BasisRemotePlayer != null && BasisRemotePlayer.HasFailedAvatarLoadGlobally)
            {
                c = BasisRemoteNamePlateDriver.StaticFailedLoadColor;
            }
            SetPlateColor(c);
        }

        /// <summary>
        /// Sets the chat text to display above the nameplate.
        /// Empty or null clears the chat text.
        /// </summary>
        public void SetChatText(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                if (ChatText == null) return;

                hasChatMessage = false;
                currentChatMessage = null;
                RefreshCachedChatTypingText();
                if (wantsTypingIndicator)
                {
                    typingAnimationFrame = -1;
                }
                UpdateChatTextVisual();
                UpdateBubbleVisual();
                return;
            }

            EnsureChatDisplayCreated();

            currentChatMessage = message;
            chatMessageSetTime = Time.timeAsDouble;
            chatDisplayLastActiveTime = chatMessageSetTime;
            hasChatMessage = true;
            RefreshCachedChatTypingText();
            UpdateChatTextVisual();
            UpdateBubbleVisual();
        }

        /// <summary>
        /// Called each frame to auto-clear an expired chat message, and to give an idle
        /// display's TMP + bubble objects back once nothing has shown for a while (they
        /// lazily re-create on the next message). Takes the frame time so the driver's
        /// per-plate loop reads Time.timeAsDouble once, not once per plate.
        /// </summary>
        public void UpdateChatTimeout(double now)
        {
            if (hasChatMessage && now - chatMessageSetTime >= BasisNetworkHandleChat.MessageDisplayDuration)
            {
                SetChatText(null);
            }

            if (BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(ChatText != null, HasBubbleText(), now, chatDisplayLastActiveTime, BasisNamePlateOverlayLimiter.ChatDisplayIdleReleaseSeconds))
            {
                ReleaseChatDisplay();
            }
        }

        /// <summary>
        /// Destroys the lazily-created chat objects (mesh, TMP, bubble). Safe to call any
        /// time — the next message re-creates them through EnsureChatDisplayCreated.
        /// </summary>
        private void ReleaseChatDisplay()
        {
            if (ChatBubbleFilter != null && ChatBubbleFilter.sharedMesh != null) Destroy(ChatBubbleFilter.sharedMesh);
            if (ChatText != null) Destroy(ChatText.gameObject);
            if (ChatBubbleFilter != null) Destroy(ChatBubbleFilter.gameObject);
            ChatText = null;
            ChatBubbleFilter = null;
            ChatBubbleRenderer = null;
            visibleChatText = null;
            chatOverlayCulled = false;
        }

        public void SetTypingIndicatorVisible(bool visible)
        {
            if (visible)
            {
                EnsureChatDisplayCreated();
            }
            if (ChatText != null)
            {
                chatDisplayLastActiveTime = Time.timeAsDouble;
            }

            wantsTypingIndicator = visible;
            if (visible)
            {
                typingAnimationStartTime = Time.timeAsDouble;
                typingAnimationFrame = -1;
                typingIndicatorText = TypingIndicatorFrames[TypingIndicatorFrames.Length - 1];
            }

            RefreshCachedChatTypingText();
            UpdateTypingIndicatorVisual();
            UpdateBubbleVisual();
        }

        public bool UpdateTypingIndicatorAnimation() => UpdateTypingIndicatorAnimation(Time.timeAsDouble);

        public bool UpdateTypingIndicatorAnimation(double now)
        {
            if (!wantsTypingIndicator)
            {
                return false;
            }

            int frame = (int)((now - typingAnimationStartTime) / 0.4d) % TypingIndicatorFrames.Length;
            if (frame == typingAnimationFrame)
            {
                return false;
            }

            typingAnimationFrame = frame;
            typingIndicatorText = TypingIndicatorFrames[frame];
            return true;
        }

        public void RefreshTypingIndicatorAnimation(double now)
        {
            if (UpdateTypingIndicatorAnimation(now))
            {
                UpdateChatTextVisual();
            }
        }

        public TextMeshPro GetBubbleSourceText()
        {
            if (ChatText != null && ChatText.gameObject.activeSelf)
            {
                return ChatText;
            }

            return null;
        }

        private void UpdateTypingIndicatorVisual()
        {
            UpdateChatTextVisual();
        }

        private void UpdateChatTextVisual()
        {
            if (ChatText == null)
            {
                return;
            }

            if (chatOverlayCulled)
            {
                // Beyond the nearest-K cap: hide and skip the text write. The cache resets so
                // readmission rewrites the label from current state.
                visibleChatText = null;
                ChatText.gameObject.SetActive(false);
                return;
            }

            if (hasChatMessage)
            {
                string text = wantsTypingIndicator
                    ? currentChatMessageWithTyping[typingAnimationFrame < 0 ? TypingIndicatorFrames.Length - 1 : typingAnimationFrame]
                    : currentChatMessage;
                if (!ReferenceEquals(visibleChatText, text))
                {
                    visibleChatText = text;
                    ChatText.text = text;
                }
                ChatText.gameObject.SetActive(true);
                return;
            }

            if (wantsTypingIndicator)
            {
                UpdateTypingIndicatorAnimation();
                if (!ReferenceEquals(visibleChatText, typingIndicatorText))
                {
                    visibleChatText = typingIndicatorText;
                    ChatText.text = typingIndicatorText;
                }
                ChatText.gameObject.SetActive(true);
                return;
            }

            visibleChatText = null;
            ChatText.gameObject.SetActive(false);
        }

        private void RefreshCachedChatTypingText()
        {
            if (!wantsTypingIndicator || !hasChatMessage || string.IsNullOrEmpty(currentChatMessage))
            {
                for (int Index = 0; Index < currentChatMessageWithTyping.Length; Index++)
                {
                    currentChatMessageWithTyping[Index] = null;
                }
                return;
            }

            for (int Index = 0; Index < TypingIndicatorFrames.Length; Index++)
            {
                currentChatMessageWithTyping[Index] = currentChatMessage + "\n" + TypingIndicatorFrames[Index];
            }
        }

        private bool HasBubbleText()
        {
            if (hasChatMessage)
            {
                return true;
            }

            return wantsTypingIndicator;
        }

        private void UpdateBubbleVisual()
        {
            if (ChatBubbleFilter == null)
            {
                return;
            }

            if (!HasBubbleText() || chatOverlayCulled)
            {
                ChatBubbleFilter.gameObject.SetActive(false);
                return;
            }

            BasisRemoteNamePlateDriver.GenerateChatBubble(this);
            ChatBubbleFilter.gameObject.SetActive(true);
        }

        internal void RefreshChatLayout()
        {
            UpdateChatTextVisual();
            UpdateBubbleVisual();
        }

        public void DeInitializeCallToRender()
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
                if (this == null || !isActiveAndEnabled) return;
                if (BasisNamePlateOverlayCore.IsLoadingComplete(progress))
                {
                    HasProgressBarVisible = false;
                    loadingTextBucket = int.MinValue;
                    ApplyLoadingOverlayActive();
                }
                else
                {
                    if (HasProgressBarVisible == false)
                    {
                        HasProgressBarVisible = true;
                        ApplyLoadingOverlayActive();
                    }

                    // Culled by the nearest-K cap: state is tracked but every text/bar write is
                    // skipped — TMP re-tessellation for far-away loading labels is the whole cost.
                    if (loadingOverlayCulled)
                    {
                        return;
                    }

                    // The label only rewrites when progress crosses a quantization bucket; the
                    // bar (a cheap sprite resize) still tracks every report.
                    int bucket = BasisNamePlateOverlayCore.ProgressBucket(progress, BasisNamePlateOverlayLimiter.LoadingTextStepPercent);
                    if (bucket != loadingTextBucket)
                    {
                        loadingTextBucket = bucket;
                        if (LoadingText.text != info)
                        {
                            LoadingText.text = info;
                        }
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
            if (BasisRemoteNamePlateDriver.NamePlateHoverMenuOnly && BasisMainMenu.Instance == null)
            {
                return false;
            }

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
                    BasisDebug.LogWarning(nameof(BasisRemoteNamePlate) + " input source interacted without highlighting first.", BasisDebug.LogTag.Input);
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
