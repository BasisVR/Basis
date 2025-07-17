using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.TransformBinders.BoneControl;
using BattlePhaze.SettingsManager.Intergrations;
using System.Collections;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.UI.NamePlate
{
    public class BasisRemoteNamePlate : BasisInteractableObject
    {
        public BasisRemoteBoneControl HipTarget;
        public BasisRemoteBoneControl MouthTarget;
        public SpriteRenderer LoadingBar;
        public MeshFilter Filter;
        public TextMeshPro LoadingText;
        public BasisRemotePlayer BasisRemotePlayer;
        public Coroutine colorTransitionCoroutine;
        public Coroutine returnToNormalCoroutine;
        public bool HasRendererCheckWiredUp = false;
        public bool IsVisible = true;
        public bool HasProgressBarVisible = false;
        public Mesh bakedMesh;
        public MeshRenderer Renderer;
        private WaitForSeconds cachedReturnDelay;
        private WaitForEndOfFrame cachedEndOfFrame;
        public Color CurrentColor;
        public Transform Self;
        public float InteractRange = 2f;
        /// <summary>
        /// can only be called once after that the text is nuked and a mesh render is just used with a filter
        /// </summary>
        /// <param name="hipTarget"></param>
        /// <param name="basisRemotePlayer"></param>
        public void Initalize(BasisRemoteBoneControl hipTarget, BasisRemotePlayer basisRemotePlayer)
        {
            cachedReturnDelay = new WaitForSeconds(BasisRemoteNamePlateDriver.returnDelay);
            cachedEndOfFrame = new WaitForEndOfFrame();
            BasisRemotePlayer = basisRemotePlayer;
            HipTarget = hipTarget;
            MouthTarget = BasisRemotePlayer.RemoteBoneDriver.Mouth;
            BasisRemotePlayer.RemoteNamePlate = this;
            BasisRemotePlayer.HasRemoteNamePlate = true;
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
            BasisRemotePlayer.AudioReceived += OnAudioReceived;
            BasisRemotePlayer.OnAvatarSwitched += RebuildRenderCheck;
            BasisRemotePlayer.OnAvatarSwitchedFallBack += RebuildRenderCheck;
            Self = this.transform;
            BasisRemoteNamePlateDriver.Instance.GenerateTextFactory(BasisRemotePlayer, this);
            LoadingText.enableVertexGradient = false;

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
              //  BasisDebug.Log("Wired up Renderer Check For Blinking");
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
            if (IsVisible == false)
            {
                if (returnToNormalCoroutine != null)
                {
                    StopCoroutine(returnToNormalCoroutine);
                }
                if (colorTransitionCoroutine != null)
                {
                    StopCoroutine(colorTransitionCoroutine);
                }
            }
        }
        public void OnAudioReceived(bool hasRealAudio)
        {
            if (IsVisible)
            {
                Color targetColor = BasisRemotePlayer.OutOfRangeFromLocal
                    ? hasRealAudio ? BasisRemoteNamePlateDriver.StaticOutOfRangeColor : BasisRemoteNamePlateDriver.StaticNormalColor
                    : hasRealAudio ? BasisRemoteNamePlateDriver.StaticIsTalkingColor : BasisRemoteNamePlateDriver.StaticNormalColor;
                BasisNetworkManagement.MainThreadContext.Post(_ =>
                {
                    if (this != null)
                    {
                        if (isActiveAndEnabled)
                        {
                            if (colorTransitionCoroutine != null)
                            {
                                StopCoroutine(colorTransitionCoroutine);
                            }
                            if (targetColor != CurrentColor)
                            {
                                colorTransitionCoroutine = StartCoroutine(TransitionColor(targetColor));
                            }
                        }
                    }
                }, null);
            }
        }
        private IEnumerator TransitionColor(Color targetColor)
        {
            CurrentColor = Renderer.sharedMaterials[0].color;
            float elapsedTime = 0f;

            while (elapsedTime < BasisRemoteNamePlateDriver.transitionDuration)
            {
                elapsedTime += Time.deltaTime;
                float lerpProgress = Mathf.Clamp01(elapsedTime / BasisRemoteNamePlateDriver.transitionDuration);
                Renderer.materials[0].color = Color.Lerp(CurrentColor, targetColor, lerpProgress);
                yield return cachedEndOfFrame;
            }

            Renderer.materials[0].color = targetColor;
            CurrentColor = targetColor;
            colorTransitionCoroutine = null;

            if (returnToNormalCoroutine != null)
            {
                StopCoroutine(returnToNormalCoroutine);
            }
            returnToNormalCoroutine = StartCoroutine(DelayedReturnToNormal());
        }

        private IEnumerator DelayedReturnToNormal()
        {
            yield return cachedReturnDelay;
            yield return StartCoroutine(TransitionColor(BasisRemoteNamePlateDriver.StaticNormalColor));
            returnToNormalCoroutine = null;
        }
        public new void OnDestroy()
        {
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
            BasisRemotePlayer.AudioReceived -= OnAudioReceived;
            DeInitalizeCallToRender();
            base.OnDestroy();
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
                      UpdateProgressBar(UniqueID, progress);
                  }
              });
        }
        public void UpdateProgressBar(string UniqueID, float progress)
        {
            Vector2 scale = LoadingBar.size;
            float NewX = progress / 2;
            if (scale.x != NewX)
            {
                scale.x = NewX;
                LoadingBar.size = scale;
            }
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
            input.PlaySoundEffect("hover", SMModuleAudio.ActiveMenusVolume / 80);
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                // same input that was highlighting previously
                if (wrapper.GetState() == BasisInteractInputState.Hovering)
                {
                    WasPressed();
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

                    WasPressed();
                    OnInteractEndEvent?.Invoke(input);
                }
            }
        }
        public void HighlightObject(bool IsHighlighted)
        {

        }
        public void WasPressed()
        {
            if (BasisRemotePlayer != null)
            {
                BasisIndividualPlayerSettings.OpenPlayerSettings(BasisRemotePlayer);
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
            return input.CurrentInputState.Trigger >= 0.9;
        }
        public const float x = 0;
        public const float z = 0;
        public static Vector3 dirToCamera;
        public static Vector3 cachedDirection;
        public static float YHeightMultiplier = 1.25f;
        public void Simulate()
        {
            cachedDirection = HipTarget.OutGoingData.position;
            cachedDirection.y += MouthTarget.TposeLocalScaled.position.y / YHeightMultiplier;
            dirToCamera = BasisLocalCameraDriver.Position - cachedDirection;
            Self.SetPositionAndRotation(cachedDirection, Quaternion.Euler(x, math.atan2(dirToCamera.x, dirToCamera.z) * Mathf.Rad2Deg, z));
        }
    }
}
