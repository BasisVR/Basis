using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
namespace Basis.Scripts.UI.NamePlate
{
    public abstract class BasisNamePlate : InteractableObject
    {
        public BasisBoneControl HipTarget;
        public BasisBoneControl MouthTarget;
        public TextMeshPro Text;
        public SpriteRenderer Loadingbar;
        public MeshFilter Filter;
        public TextMeshPro Loadingtext;
        public BasisRemotePlayer BasisRemotePlayer;
        public SpriteRenderer namePlateImage;
        public Coroutine colorTransitionCoroutine;
        public Coroutine returnToNormalCoroutine;
        public bool HasRendererCheckWiredUp = false;
        public bool IsVisible = true;
        public static bool ShowHandle = false;
        public bool HasProgressBarVisible = false;
        public Mesh bakedMesh;
        private WaitForSeconds cachedReturnDelay;
        private WaitForEndOfFrame cachedEndOfFrame;
        public Color CurrentColor;
        public static Action<bool> ShowHandleEvent = thebool => { BasisNamePlate.ShowHandle = thebool; };
        public SpriteRenderer namePlateLarge;
        public TextMeshPro Handletext;
        /// <summary>
        /// can only be called once after that the text is nuked and a mesh render is just used with a filter
        /// </summary>
        /// <param name="hipTarget"></param>
        /// <param name="basisRemotePlayer"></param>
        public void Initalize(BasisBoneControl hipTarget, BasisRemotePlayer basisRemotePlayer)
        {
            if (BasisDeviceManagement.IsMobile())
            {
                Color Color = namePlateImage.color;
                Color.a = 1;
                namePlateImage.color = Color;
            }
            cachedReturnDelay = new WaitForSeconds(RemoteNamePlateDriver.returnDelay);
            cachedEndOfFrame = new WaitForEndOfFrame();
            BasisRemotePlayer = basisRemotePlayer;
            HipTarget = hipTarget;
            MouthTarget = BasisRemotePlayer.Mouth;
            Text.text = BasisRemotePlayer.DisplayName;
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
            BasisRemotePlayer.AudioReceived += OnAudioReceived;
            BasisRemotePlayer.OnAvatarSwitched += RebuildRenderCheck;
            BasisRemotePlayer.OnAvatarSwitchedFallBack += RebuildRenderCheck;
            RemoteNamePlateDriver.Instance.AddNamePlate(this);
            Loadingtext.enableVertexGradient = false;
            // Text.enableCulling = true;
            // Text.enableAutoSizing = false;
            GenerateText();
            GameObject.Destroy(Text.gameObject);
            ShowHandleEvent += ShowHandleFunc;
            ShowHandleFunc(ShowHandle);
        }
        public void ShowHandleFunc(bool Show)
        {
            int id = GetInstanceID();
            BasisDebug.Log("ShowHandleFunc " + Show + " " + id);
        }
        public void GenerateText()
        {
            // Force update to ensure the mesh is generated
            Text.ForceMeshUpdate();
            // Store the generated mesh
            bakedMesh = Mesh.Instantiate(Text.mesh);
            Filter.sharedMesh = bakedMesh;
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
                BasisDebug.Log("Wired up Renderer Check For Blinking");
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
                Color targetColor;
                if (BasisRemotePlayer.OutOfRangeFromLocal)
                {
                    targetColor = hasRealAudio ? RemoteNamePlateDriver.StaticOutOfRangeColor : RemoteNamePlateDriver.StaticNormalColor;
                }
                else
                {
                    targetColor = hasRealAudio ? RemoteNamePlateDriver.StaticIsTalkingColor : RemoteNamePlateDriver.StaticNormalColor;
                }
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
            CurrentColor = namePlateImage.color;
            float elapsedTime = 0f;

            while (elapsedTime < RemoteNamePlateDriver.transitionDuration)
            {
                elapsedTime += Time.deltaTime;
                float lerpProgress = Mathf.Clamp01(elapsedTime / RemoteNamePlateDriver.transitionDuration);
                namePlateImage.color = Color.Lerp(CurrentColor, targetColor, lerpProgress);
                yield return cachedEndOfFrame;
            }

            namePlateImage.color = targetColor;
            CurrentColor = targetColor;
            colorTransitionCoroutine = null;

            if (targetColor == RemoteNamePlateDriver.StaticIsTalkingColor)
            {
                if (returnToNormalCoroutine != null)
                {
                    StopCoroutine(returnToNormalCoroutine);
                }
                returnToNormalCoroutine = StartCoroutine(DelayedReturnToNormal());
            }
        }

        private IEnumerator DelayedReturnToNormal()
        {
            yield return cachedReturnDelay;
            yield return StartCoroutine(TransitionColor(RemoteNamePlateDriver.StaticNormalColor));
            returnToNormalCoroutine = null;
        }
        public new void OnDestroy()
        {
            BasisRemotePlayer.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
            BasisRemotePlayer.AudioReceived -= OnAudioReceived;
            DeInitalizeCallToRender();
            RemoteNamePlateDriver.Instance.RemoveNamePlate(this);
            ShowHandleEvent -= ShowHandleFunc;
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
                      Loadingtext.gameObject.SetActive(false);
                      Loadingbar.gameObject.SetActive(false);
                      HasProgressBarVisible = false;
                  }
                  else
                  {
                      if (HasProgressBarVisible == false)
                      {
                          Loadingbar.gameObject.SetActive(true);
                          Loadingtext.gameObject.SetActive(true);
                          HasProgressBarVisible = true;
                      }

                      if (Loadingtext.text != info)
                      {
                          Loadingtext.text = info;
                      }
                      UpdateProgressBar(UniqueID, progress);
                  }
              });
        }
        public void UpdateProgressBar(string UniqueID, float progress)
        {
            Vector2 scale = Loadingbar.size;
            float NewX = progress / 2;
            if (scale.x != NewX)
            {
                scale.x = NewX;
                Loadingbar.size = scale;
            }
        }
        public override bool CanHover(BasisInput input)
        {
            return !DisableInteract &&
                !IsPuppeted &&
                Inputs.IsInputAdded(input) &&
                input.TryGetRole(out BasisBoneTrackedRole role) &&
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
                found.GetState() == InteractInputState.Ignored &&
                IsWithinRange(input.transform.position);
        }
        public override bool CanInteract(BasisInput input)
        {
            return !DisableInteract &&
                !IsPuppeted &&
                Inputs.IsInputAdded(input) &&
                input.TryGetRole(out BasisBoneTrackedRole role) &&
                Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
                found.GetState() == InteractInputState.Hovering &&
                IsWithinRange(input.transform.position);
        }

        public override void OnHoverStart(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            if (found != null && found.Value.GetState() != InteractInputState.Ignored)
                BasisDebug.LogWarning(nameof(PickupInteractable) + " input state is not ignored OnHoverStart, this shouldn't happen");
            var added = Inputs.ChangeStateByRole(found.Value.Role, InteractInputState.Hovering);
            if (!added)
                BasisDebug.LogWarning(nameof(PickupInteractable) + " did not find role for input on hover");

            OnHoverStartEvent?.Invoke(input);
            HighlightObject(true);
        }

        public override void OnHoverEnd(BasisInput input, bool willInteract)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out _))
            {
                if (!willInteract)
                {
                    if (!Inputs.ChangeStateByRole(role, InteractInputState.Ignored))
                    {
                        BasisDebug.LogWarning(nameof(PickupInteractable) + " found input by role but could not remove by it, this is a bug.");
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
                if (wrapper.GetState() == InteractInputState.Hovering)
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
                BasisDebug.LogWarning(nameof(PickupInteractable) + " did not find role for input on Interact start");
            }
        }

        public override void OnInteractEnd(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                if (wrapper.GetState() == InteractInputState.Interacting)
                {
                    Inputs.ChangeStateByRole(wrapper.Role, InteractInputState.Ignored);

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
            return found.HasValue && found.Value.GetState() == InteractInputState.Interacting;
        }

        public override bool IsHoveredBy(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == InteractInputState.Hovering;
        }

        public override void InputUpdate()
        {
        }
    }
}
