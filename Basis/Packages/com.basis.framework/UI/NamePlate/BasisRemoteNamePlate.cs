using System.Linq;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
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
            BasisRemoteNamePlateDriver.Instance.GenerateTextFactory(BasisRemotePlayer, this);
            LoadingText.enableVertexGradient = false;
            mpb = new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(mpb, 0);
            BasisRemoteNamePlateDriver.Register(this);
        }
        private void SetPlateColor(Color c)
        {
            mpb.SetColor(ColorId, c);
            Renderer.SetPropertyBlock(mpb, 0);
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
            gameObject.SetActive(State);

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
            /*if (BasisRemotePlayer != null && BasisIndividualPlayerSettings.Instance == null)
            {
                input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume);
                BasisIndividualPlayerSettings.OpenPlayerSettings(BasisRemotePlayer);
            }*/
            BasisNetworkPlayer networkPlayer = BasisNetworkPlayers.Players.Values.FirstOrDefault(p => p.Player.UUID == BasisRemotePlayer.UUID);
            if (networkPlayer == null) return;

            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            PlayersProvider provider = (PlayersProvider)BasisMenuBase<BasisMainMenu>.Providers.Find(
                prov => prov is PlayersProvider);
            if (provider == null) return;
            provider.OpenMenu(networkPlayer);

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
            return HasState(input.CurrentInputState);
        }
    }
}
