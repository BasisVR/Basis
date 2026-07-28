using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.Scripts.BasisSdk.Interactions
{
    public class BasisPlayerInteract : MonoBehaviour
    {
        public static LayerMask IgnoreRaycasting;
        public static LayerMask playerLayer;
        public static LayerMask LocalPlayerAvatar;
        public static LayerMask Mask;
        public static LayerMask IgnoredByInteractable;

        public static QueryTriggerInteraction TriggerInteraction = QueryTriggerInteraction.UseGlobal;

        [Tooltip("How far the player can interact with objects. Must hold that raycastDistance > hoverRadius")]
        public static float raycastDistance = 5.0f;

        [Tooltip("How far the player Hover.")]
        public static float hoverRadius = 0.5f;

        // NOTE: this needs to be >= max number of colliders it can potentially hit in a scene, otherwise it will behave oddly
        public static int k_MaxPhysicHitCount = 128;
        public static bool OnlySortClosest = true;

        [Tooltip("Search radius for direct grab detection around hand bones")]
        public static float grabSearchRadius = 0.25f;

        /// <summary>
        /// Interaction ranges (hover, grab search, cosmetic line offsets) are authored in metres for a
        /// default-size avatar. Scale them with the avatar so a shrunken player doesn't hover/grab
        /// across the room — the unscaled 0.5 m hover bubble on a tiny avatar lit the interaction line
        /// toward random props while pointing at UI ("the line points the wrong direction").
        /// </summary>
        public static float AvatarScaledRange(float baseRange)
        {
            float s = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            if (float.IsNaN(s) || float.IsInfinity(s) || s <= 0f)
            {
                s = 1f;
            }
            return baseRange * s;
        }

        public static float RaycastVisualStartForwardOffset = 0.06f;

        public static Vector3 RayVisualStart(Vector3 origin, Vector3 end)
        {
            Vector3 delta = end - origin;
            float distance = delta.magnitude;
            if (distance <= 1e-4f)
            {
                return origin;
            }
            float applied = Mathf.Min(AvatarScaledRange(RaycastVisualStartForwardOffset), distance * 0.5f);
            return origin + (delta / distance) * applied;
        }

        private static readonly Collider[] _grabHitBuffer = new Collider[32];

        [SerializeField]
        public BasisInteractInput[] InteractInputs = new BasisInteractInput[] { };

        public static Material LineMaterial;
        private static AsyncOperationHandle<Material> asyncOperationLineMaterial;

        public static float interactLineWidth = 1f;
        public static bool renderInteractLines = true;
        private static bool interactLinesActive = false;

        public static string LoadMaterialAddress = "Interactable/InteractLineMat.mat";

        private const int k_UpdatePriority = 201;

        public static BasisPlayerInteract Instance;

        private BasisDirectTouch _directTouch;

        private void Start()
        {
            IgnoreRaycasting = LayerMask.NameToLayer("Ignore Raycast");
            playerLayer = LayerMask.NameToLayer("Player");
            LocalPlayerAvatar = LayerMask.NameToLayer("LocalPlayerAvatar");

            IgnoredByInteractable = LayerMask.NameToLayer("IgnoredByInteractable");
            // Create a LayerMask that includes all layers
            LayerMask allLayers = ~0;

            // Exclude the "Ignore Raycast", "Player", and "LocalPlayerAvatar" layers
            Mask = allLayers &
                   ~(1 << (int)IgnoreRaycasting) &
                   ~(1 << (int)playerLayer) &
                   ~(1 << (int)IgnoredByInteractable) &
                   ~(1 << (int)LocalPlayerAvatar);

            Instance = this;

            _directTouch = new BasisDirectTouch();

            BasisLocalPlayer.AfterSimulateOnLate.AddAction(k_UpdatePriority, PollSystem);

            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            devices.OnListAdded += OnInputChanged;
            devices.OnListItemRemoved += OnInputRemoved;

            var array = devices.ToArray();
            for (int i = 0; i < array.Length; i++)
            {
                BasisInput device = array[i];
                OnInputChanged(device);
            }

            AsyncOperationHandle<Material> op = Addressables.LoadAssetAsync<Material>(LoadMaterialAddress);
            LineMaterial = op.WaitForCompletion();
            asyncOperationLineMaterial = op;
        }

        private void OnDestroy()
        {
            if (asyncOperationLineMaterial.IsValid())
            {
                asyncOperationLineMaterial.Release();
            }

            _directTouch?.Shutdown();
            _directTouch = null;

            BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(k_UpdatePriority, PollSystem);

            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            devices.OnListAdded -= OnInputChanged;
            devices.OnListItemRemoved -= OnInputRemoved;
        }

        private void OnInputChanged(BasisInput input)
        {
            if (!input.HasRaycaster)
            {
                return;
            }

            // Guard against duplicate additions
            if (InteractInputs.Any(x => x.input.UniqueDeviceIdentifier == input.UniqueDeviceIdentifier))
            {
                return;
            }

            var interactInput = new BasisInteractInput
            {
                input = input,
                lastTarget = null,
            };

            var list = InteractInputs.ToList();
            list.Add(interactInput);
            InteractInputs = list.ToArray();
        }

        private void OnInputRemoved(BasisInput input)
        {
            // Always attempt removal regardless of current HasRaycaster state.
            // The raycaster state may have changed between when the input was added
            // and when it is being removed (e.g., OpenXR eye tracking detected late).
            RemoveInput(input.UniqueDeviceIdentifier);
        }

        // Simulate after IK update
        [BurstCompile]
        private void PollSystem()
        {
#if UNITY_EDITOR // just remove when you're profiling this
            UnityEngine.Profiling.Profiler.BeginSample("Interactable System");
#endif
            if (InteractInputs == null)
            {
                return;
            }

            int interactInputsCount = InteractInputs.Length;
            if (interactInputsCount == 0)
            {
                return;
            }

            for (int index = 0; index < interactInputsCount; index++)
            {
                BasisInteractInput interactInput = InteractInputs[index];
                if (interactInput.input == null)
                {
                    BasisDebug.LogWarning("Pickup input device unexpectedly null, input devices likely changed");
                    continue;
                }

                bool gripDown = interactInput.input.CurrentInputState.GripButton;
                bool gripPressedAgain = gripDown && !interactInput.wasGripDown;
                interactInput.wasGripDown = gripDown;

                // After a grab-again drop, wait for grip release so the same press can't re-grab the pickup
                if (interactInput.suppressGrabUntilRelease)
                {
                    if (gripDown)
                    {
                        InteractInputs[index] = interactInput;
                        continue;
                    }
                    interactInput.suppressGrabUntilRelease = false;
                }

                // Auto-hold: pressing grab again drops the held pickup (VR only; desktop uses right-click)
                if (gripPressedAgain
                    && interactInput.lastTarget != null
                    && interactInput.lastTarget.IsInteractingWith(interactInput.input)
                    && interactInput.lastTarget.IsAutoHoldActive()
                    && !IsDesktopCenterEye(interactInput.input))
                {
                    interactInput.lastTarget.OnInteractEnd(interactInput.input);
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                    }
                    interactInput.lastTarget = null;
                    interactInput.suppressGrabUntilRelease = true;
                    InteractInputs[index] = interactInput;
                    continue;
                }

                BasisHoverSphere hoverSphere = interactInput.input.hoverSphere;

                // Poll hover — radius rescaled per frame so avatar scale changes apply immediately
                hoverSphere.Radius = AvatarScaledRange(hoverRadius);
                hoverSphere.PollSystem(interactInput.input.RaycastCoord.position);

                BasisInteractableObject hitInteractable = PointRaycasterFindInteractable(interactInput);
                bool isValidRayHit = hitInteractable != null;

                bool isValidHoverHit = false;
                if (hoverSphere.ResultCount != 0)
                {
                    if (ClosestInfluencableHover(hoverSphere, interactInput.input, out BasisHoverResult result, out BasisInteractableObject obj))
                    {
                        isValidHoverHit = true;
                        hitInteractable = obj;
                    }
                }

                if (isValidRayHit || isValidHoverHit)
                {
                    if (hitInteractable != null)
                    {
                        interactInput.HasvalidRay = true;
                        // NOTE: this will skip a frame of hover after stopping interact
                        UpdatePickupState(hitInteractable, ref interactInput);
                    }
                    else
                    {
                        BasisDebug.LogWarning("Player Interact expected a registered hit but found null. This is a bug, please report.");
                    }
                }
                // Direct grab detection: hand-proximity grab for VR hands
                else if (TryDetectDirectGrab(interactInput, gripPressedAgain, out BasisInteractableObject grabTarget))
                {
                    HandleDirectGrab(grabTarget, ref interactInput);
                }
                // Hover missed entirely. Test for drop & clear hover
                else
                {
                    interactInput.HasvalidRay = false;
                    if (interactInput.lastTarget != null)
                    {
                        // Implementation could allow for hovering and holding of the same object, clear independently
                        // Drop logic: drop when not triggered, or when autohold drop is pressed
                        if (!interactInput.lastTarget.IsInteractTriggered(interactInput.input) && interactInput.lastTarget.IsInteractingWith(interactInput.input) && interactInput.lastTarget.ShouldReleaseAutoHold(interactInput.input))
                        {
                            interactInput.lastTarget.OnInteractEnd(interactInput.input);
                        }

                        if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                        {
                            interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                        }
                    }
                }

                // Write changes back
                InteractInputs[index] = interactInput;
            }

            // TODO: replace with UniqueCounterList
            // Iterate over all the inputs
            for (int index = 0; index < interactInputsCount; index++)
            {
                var input = InteractInputs[index];
                if (input.lastTarget != null && input.lastTarget.RequiresUpdateLoop)
                {
                    input.lastTarget.InputUpdate();
                }
            }

            // Poll direct finger touch for VR UI
            _directTouch?.Poll(InteractInputs);

            // Apply line renderer
            if (renderInteractLines)
            {
                interactLinesActive = true;

                for (int index = 0; index < interactInputsCount; index++)
                {
                    var input = InteractInputs[index];

                    // Hide interaction line when direct touch is active for this hand
                    if (_directTouch != null && _directTouch.IsDeviceTouching(input.input))
                    {
                        if (input.input.InteractionLineRenderer)
                            input.input.InteractionLineRenderer.enabled = false;
                        continue;
                    }

                    if (input.lastTarget != null && input.lastTarget.IsHoveredBy(input.input))
                    {
                        Vector3 origin = input.input.RaycastCoord.position;
                        Vector3 startPos, endPos;
                        float dominantHandFactor = BasisDominantHand.IsLeftHanded ? -1f : 1f;

                        // Desktop offset for center eye (a little to the bottom in the direction of the dominant hand)
                        if (IsDesktopCenterEye(input.input))
                        {
                            Vector3 camDir = input.input.RaycastCoord.rotation * Vector3.right * dominantHandFactor;
                            startPos =
                                input.input.RaycastCoord.position +
                                (input.input.RaycastCoord.rotation * Vector3.forward * AvatarScaledRange(0.1f)) +
                                Vector3.down * AvatarScaledRange(0.1f) +
                                (camDir * AvatarScaledRange(0.125f));
                            // Move the line a bit further out of the way for desktop players by finding the closest
                            // point to the line origin biased towards the edge of the object in the direction of
                            // the dominant hand
                            endPos = input.lastTarget.GetClosestPoint(startPos + camDir * 1000f);
                        }
                        else
                        {
                            startPos = origin;
                            endPos = input.lastTarget.GetClosestPoint(startPos);
                            startPos = RayVisualStart(origin, endPos);
                        }

                        if (input.input.InteractionLineRenderer != null)
                        {
                            LineRenderer line = input.input.InteractionLineRenderer;
                            // Same defensive normalization as the UI line: immune to any scaled ancestor.
                            Vector3 lineLossy = line.transform.lossyScale;
                            if (Mathf.Abs(lineLossy.x - 1f) > 1e-3f || Mathf.Abs(lineLossy.y - 1f) > 1e-3f || Mathf.Abs(lineLossy.z - 1f) > 1e-3f)
                            {
                                Vector3 lineLocal = line.transform.localScale;
                                line.transform.localScale = new Vector3(
                                    lineLossy.x > 1e-6f ? lineLocal.x / lineLossy.x : 1f,
                                    lineLossy.y > 1e-6f ? lineLocal.y / lineLossy.y : 1f,
                                    lineLossy.z > 1e-6f ? lineLocal.z / lineLossy.z : 1f);
                            }
                            line.SetPosition(0, startPos);
                            line.SetPosition(1, endPos);
                            line.enabled = true;
                        }
                    }
                    else
                    {
                        if (input.input.InteractionLineRenderer)
                        {
                            input.input.InteractionLineRenderer.enabled = false;
                        }
                    }
                }
            }
            // Turn all the lines off
            else if (interactLinesActive)
            {
                interactLinesActive = false;

                for (int index = 0; index < interactInputsCount; index++)
                {
                    var input = InteractInputs[index];
                    if (input.input.InteractionLineRenderer != null)
                    {
                        input.input.InteractionLineRenderer.enabled = false;
                    }
                }
            }

#if UNITY_EDITOR
            UnityEngine.Profiling.Profiler.EndSample();
#endif
        }

        private BasisInteractableObject PointRaycasterFindInteractable(BasisInteractInput interactInput)
        {
            bool hit = interactInput.input.BasisPointRaycaster.FirstHit(out RaycastHit rayHit, AvatarScaledRange(raycastDistance));
            if (!hit)
            {
                return null;
            }
            if (((1 << rayHit.collider.gameObject.layer) & Mask) == 0)
            {
                return null;
            }
            // Try to get component from hit collider first.
            BasisInteractableObject hitInteractable;
            if (rayHit.collider.TryGetComponent(out hitInteractable))
            {
                return hitInteractable;
            }
            // Try to get component from ancestors, but only consider if the hit collider is one of its colliders.
            hitInteractable = rayHit.collider.GetComponentInParent<BasisInteractableObject>();
            if (hitInteractable == null)
            {
                return null;
            }
            Collider[] colliders = hitInteractable.GetColliders();
            if (colliders != null && colliders.Length > 0 && colliders.Contains(rayHit.collider))
            {
                return hitInteractable;
            }
            return null;
        }
        private void UpdatePickupState(BasisInteractableObject hitInteractable, ref BasisInteractInput interactInput)
        {
            bool isSameTarget =
                interactInput.lastTarget != null &&
                interactInput.lastTarget.GetEntityId() == hitInteractable.GetEntityId();

            // -----------------------------
            // Different target than last time
            // -----------------------------
            if (!isSameTarget && interactInput.lastTarget != null)
            {
                // If we're holding the last target (trigger pressed)
                if (interactInput.lastTarget.IsInteractTriggered(interactInput.input))
                {
                    // Clear hover of last
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                    }

                    bool shouldHold = hitInteractable.IsAutoHoldActive();

                    // Start interaction on new hit if allowed
                    if (hitInteractable.CanInteract(interactInput.input) &&
                        (!interactInput.lastTarget.IsInteractingWith(interactInput.input) || shouldHold))
                    {
                        hitInteractable.OnInteractStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }
                else
                {
                    // Not pressing interact: clear states from last target (if needed), then consider hover on new target.
                    bool removeTarget = false;

                    bool lastTargetIsHeld = interactInput.lastTarget.IsInteractingWith(interactInput.input);
                    bool autoHoldDropped = !lastTargetIsHeld || interactInput.lastTarget.ShouldReleaseAutoHold(interactInput.input);

                    // If last target is interacting and we should drop, end interaction
                    if (interactInput.lastTarget.IsInteractingWith(interactInput.input) && autoHoldDropped)
                    {
                        interactInput.lastTarget.OnInteractEnd(interactInput.input);
                        removeTarget = true;
                    }

                    // End hover on last target (we're on a new target now)
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                        removeTarget = true;
                    }

                    if (removeTarget)
                    {
                        interactInput.lastTarget = null;
                    }

                    // Try hovering new target (only if we’re allowed to start a hover)
                    if (autoHoldDropped && hitInteractable.CanHover(interactInput.input))
                    {
                        hitInteractable.OnHoverStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }

                return;
            }

            // -----------------------------
            // Same target (or lastTarget is null)
            // -----------------------------
            if (hitInteractable.IsInteractTriggered(interactInput.input))
            {
                // If currently hovered, end hover before interaction
                if (hitInteractable.IsHoveredBy(interactInput.input))
                {
                    bool canInteractNow = hitInteractable.CanInteract(interactInput.input);
                    hitInteractable.OnHoverEnd(interactInput.input, canInteractNow);
                }

                bool shouldHold = hitInteractable.IsAutoHoldActive();

                if (hitInteractable.CanInteract(interactInput.input))
                {
                    if (!hitInteractable.IsInteractingWith(interactInput.input) || shouldHold)
                    {
                        hitInteractable.OnInteractStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }

                return;
            }

            // Not interacting this frame
            bool autoHoldDroppedSameTarget = hitInteractable.ShouldReleaseAutoHold(interactInput.input);

            // End interaction if grip is confirmed released (with grace period
            // to prevent false drops during fast VR hand movement)
            if (hitInteractable.IsInteractingWith(interactInput.input) && autoHoldDroppedSameTarget)
            {
                hitInteractable.OnInteractEnd(interactInput.input);
            }

            // ✅ Key part: Hover maintenance vs hover start
            bool isHoveredNow = hitInteractable.IsHoveredBy(interactInput.input);

            if (isHoveredNow)
            {
                // Maintain hover only if still valid (range/UI/enabled/etc)
                if (!HoverStillValid(hitInteractable, interactInput.input))
                {
                    hitInteractable.OnHoverEnd(interactInput.input, false);

                    // If we’re not interacting (or auto-hold allows drop), clear lastTarget to avoid “sticky”
                    if (!hitInteractable.IsInteractingWith(interactInput.input) || autoHoldDroppedSameTarget)
                    {
                        interactInput.lastTarget = null;
                    }
                }
                else
                {
                    interactInput.lastTarget = hitInteractable;
                }
            }
            else
            {
                // Not currently hovered: only then use CanHover() to START hover
                if (hitInteractable.CanHover(interactInput.input))
                {
                    hitInteractable.OnHoverStart(interactInput.input);
                    interactInput.lastTarget = hitInteractable;
                }
                else
                {
                    // If we can’t hover and we’re not interacting, don’t keep lastTarget stuck
                    if (!hitInteractable.IsInteractingWith(interactInput.input) || autoHoldDroppedSameTarget)
                    {
                        interactInput.lastTarget = null;
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether an existing hover should remain active.
        /// IMPORTANT: This is NOT the same as CanHover(), which is “can we start hovering?”
        /// </summary>
        private static bool HoverStillValid(BasisInteractableObject obj, BasisInput input)
        {
            if (!obj.InteractableEnabled) return false;
            if (input.BasisUIRaycast.HadRaycastUITarget) return false;
            if (!obj.Inputs.IsInputAdded(input)) return false;
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return false;
            if (!obj.Inputs.TryGetByRole(role, out BasisInputWrapper wrapper)) return false;

            // We only use this function to validate an EXISTING hover
            if (wrapper.GetState() != BasisInteractInputState.Hovering) return false;

            return obj.IsWithinRange(wrapper.BoneControl.OutgoingWorldData.position, obj.InteractRange);
        }

        private void RemoveInput(string uid)
        {
            // Find the inputs to remove based on the UID
            BasisInteractInput[] inputs = InteractInputs
                .Where(x => x.input.UniqueDeviceIdentifier == uid)
                .ToArray();

            int length = inputs.Length;

            if (length > 1)
            {
                BasisDebug.LogError($"Interact Inputs has multiple inputs of the same UID {uid}. Please report this bug.");
            }

            if (length == 0)
            {
                // Not an error: the device may never have been a raycaster when it was
                // added (e.g., OpenXR headset whose eye tracking was detected after
                // the input was registered). See issue #389.
                return;
            }

            for (int i = 0; i < inputs.Length; i++)
            {
                var input = inputs[i];
                // Handle hover and interaction states
                if (input.lastTarget != null)
                {
                    if (input.lastTarget.IsHoveredBy(input.input))
                    {
                        input.lastTarget.OnHoverEnd(input.input, false);
                    }

                    if (input.lastTarget.IsInteractingWith(input.input))
                    {
                        input.lastTarget.OnInteractEnd(input.input);
                    }
                }
                // Manually resize the array
                InteractInputs = InteractInputs
                    .Where(x => x.input.UniqueDeviceIdentifier != input.input.UniqueDeviceIdentifier)
                    .ToArray();
            }
        }

        /// <summary>
        /// Attempts to find a directly grabbable interactable near the hand bone position.
        /// Only activates for hand inputs when grip is pressed.
        /// </summary>
        private bool TryDetectDirectGrab(BasisInteractInput interactInput, bool gripPressedAgain, out BasisInteractableObject grabTarget)
        {
            grabTarget = null;
            BasisInput input = interactInput.input;

            // Don't try to grab if already interacting with something
            if (interactInput.lastTarget != null && interactInput.lastTarget.IsInteractingWith(input))
                return false;

            // Require a fresh grip press: a grip still held from a drop must not re-grab (intentional grab only)
            if (!gripPressedAgain) return false;

            // Only for hand roles
            if (!input.TryGetRole(out BasisBoneTrackedRole role)) return false;
            if (role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand) return false;

            // Get the hand bone world position
            if (!input.HasControl || input.Control == null) return false;
            Vector3 handPos = input.Control.OutgoingWorldData.position;

            // Overlap sphere at hand position to find nearby colliders
            int hitCount = Physics.OverlapSphereNonAlloc(handPos, AvatarScaledRange(grabSearchRadius), _grabHitBuffer, Mask, TriggerInteraction);

            float closestDist = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = _grabHitBuffer[i];
                if (col == null) continue;

                // Find interactable on collider or parent (matching existing lookup pattern)
                BasisInteractableObject obj = col.GetComponent<BasisInteractableObject>();
                if (obj == null)
                {
                    obj = col.GetComponentInParent<BasisInteractableObject>();
                    if (obj != null)
                    {
                        Collider[] colliders = obj.GetColliders();
                        if (colliders == null || !colliders.Contains(col))
                            continue;
                    }
                }
                if (obj == null) continue;

                // Check distance against object's specific grab radius
                float dist = Vector3.Distance(obj.GetClosestPoint(handPos), handPos);
                if (dist > obj.GrabRadius) continue;

                // Check if object allows direct grab from this input
                if (!obj.CanDirectGrab(input)) continue;

                if (dist < closestDist)
                {
                    closestDist = dist;
                    grabTarget = obj;
                }
            }

            return grabTarget != null;
        }

        /// <summary>
        /// Executes a direct grab: transitions the interactable from Ignored → Hovering → Interacting in a single frame.
        /// </summary>
        private void HandleDirectGrab(BasisInteractableObject target, ref BasisInteractInput interactInput)
        {
            BasisInput input = interactInput.input;

            // Clean up any existing hover/interaction on the previous target
            if (interactInput.lastTarget != null && interactInput.lastTarget != target)
            {
                if (interactInput.lastTarget.IsHoveredBy(input))
                    interactInput.lastTarget.OnHoverEnd(input, false);
                if (interactInput.lastTarget.IsInteractingWith(input))
                    interactInput.lastTarget.OnInteractEnd(input);
            }

            // Only start hover if not already hovering (avoid redundant state transitions)
            if (!target.IsHoveredBy(input))
            {
                target.OnHoverStart(input);
            }
            // End hover with willInteract=true (keeps state at Hovering for OnInteractStart)
            target.OnHoverEnd(input, true);
            // Transition: Hovering → Interacting
            target.OnInteractStart(input);

            interactInput.lastTarget = target;
            interactInput.HasvalidRay = true;
        }

        private const int HoverCircleSeg = 16;
        private const float HoverGizmoWidth = 0.004f;
        private static readonly Color HoverListColor = Color.magenta;
        private static readonly Color HoverTargetColor = Color.blue;
        private static readonly Color HoverSphereColor = Color.gray;

        private struct HoverGizmo
        {
            public int TargetLine;
            public int RingX;
            public int RingY;
            public int RingZ;
            public List<int> ListLines;
        }

        private static readonly Dictionary<BasisInput, HoverGizmo> _hoverGizmos = new Dictionary<BasisInput, HoverGizmo>();
        private static readonly HashSet<BasisInput> _hoverSeen = new HashSet<BasisInput>();
        private static readonly List<BasisInput> _hoverStale = new List<BasisInput>();
        private static readonly Vector3[] _hoverRingBuf = new Vector3[HoverCircleSeg];
        private static bool _hoverHooked;

        public static void UpdateHoverGizmos(bool show)
        {
            EnsureHoverHook();

            if (!show || Instance == null || Instance.InteractInputs == null)
            {
                HoverShutdown();
                return;
            }

            _hoverSeen.Clear();
            BasisInteractInput[] devices = Instance.InteractInputs;
            int count = devices.Length;
            for (int index = 0; index < count; index++)
            {
                BasisInteractInput device = devices[index];
                if (device.input == null || device.input.hoverSphere == null)
                {
                    continue;
                }

                BasisInput input = device.input;
                _hoverSeen.Add(input);
                _hoverGizmos.TryGetValue(input, out HoverGizmo g);
                if (g.ListLines == null)
                {
                    g.ListLines = new List<int>();
                }

                Vector3 origin = input.RaycastCoord.position;

                int drawn = 0;
                int resultCount = input.hoverSphere.ResultCount;
                for (int r = 1; r < resultCount; r++)
                {
                    BasisHoverResult hit = input.hoverSphere.Results[r];
                    if (hit.collider == null || hit.distanceToCenter == float.NegativeInfinity)
                    {
                        continue;
                    }
                    if (!hit.collider.TryGetComponent(out BasisInteractableObject _))
                    {
                        continue;
                    }
                    int lineId = EnsureHoverListLine(g.ListLines, drawn);
                    BasisGizmoManager.UpdateLineGizmo(lineId, origin, hit.closestPointToCenter);
                    BasisGizmoManager.SetGizmoActive(lineId, true);
                    drawn++;
                }
                for (int i = drawn; i < g.ListLines.Count; i++)
                {
                    BasisGizmoManager.SetGizmoActive(g.ListLines[i], false);
                }

                if (Instance.ClosestInfluencableHover(input.hoverSphere, input, out BasisHoverResult result, out _))
                {
                    if (g.TargetLine <= 0)
                    {
                        BasisGizmoManager.CreateLineGizmo("HoverTarget", out g.TargetLine, origin, result.closestPointToCenter, HoverGizmoWidth, HoverTargetColor);
                    }
                    else
                    {
                        BasisGizmoManager.UpdateLineGizmo(g.TargetLine, origin, result.closestPointToCenter);
                        BasisGizmoManager.SetGizmoActive(g.TargetLine, true);
                    }
                }
                else if (g.TargetLine > 0)
                {
                    BasisGizmoManager.SetGizmoActive(g.TargetLine, false);
                }

                if (!IsDesktopCenterEye(input))
                {
                    UpdateHoverSphere(ref g, input.hoverSphere.WorldPosition, AvatarScaledRange(hoverRadius));
                }
                else
                {
                    SetHoverSphereActive(g, false);
                }

                _hoverGizmos[input] = g;
            }

            HoverPrune();
        }

        private static int EnsureHoverListLine(List<int> pool, int index)
        {
            while (pool.Count <= index)
            {
                BasisGizmoManager.CreateLineGizmo("HoverList", out int id, Vector3.zero, Vector3.zero, HoverGizmoWidth, HoverListColor);
                pool.Add(id);
            }
            return pool[index];
        }

        private static void UpdateHoverSphere(ref HoverGizmo g, Vector3 center, float radius)
        {
            bool create = g.RingX <= 0;

            FillRing(center, radius, Vector3.up, Vector3.forward);
            if (create)
            {
                BasisGizmoManager.CreateLineGizmo("HoverRingX", out g.RingX, _hoverRingBuf, HoverGizmoWidth, HoverSphereColor, loop: true);
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(g.RingX, _hoverRingBuf);
                BasisGizmoManager.SetGizmoActive(g.RingX, true);
            }

            FillRing(center, radius, Vector3.right, Vector3.forward);
            if (create)
            {
                BasisGizmoManager.CreateLineGizmo("HoverRingY", out g.RingY, _hoverRingBuf, HoverGizmoWidth, HoverSphereColor, loop: true);
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(g.RingY, _hoverRingBuf);
                BasisGizmoManager.SetGizmoActive(g.RingY, true);
            }

            FillRing(center, radius, Vector3.right, Vector3.up);
            if (create)
            {
                BasisGizmoManager.CreateLineGizmo("HoverRingZ", out g.RingZ, _hoverRingBuf, HoverGizmoWidth, HoverSphereColor, loop: true);
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(g.RingZ, _hoverRingBuf);
                BasisGizmoManager.SetGizmoActive(g.RingZ, true);
            }
        }

        private static void FillRing(Vector3 center, float radius, Vector3 a, Vector3 b)
        {
            float step = Mathf.PI * 2f / HoverCircleSeg;
            for (int i = 0; i < HoverCircleSeg; i++)
            {
                float t = step * i;
                _hoverRingBuf[i] = center + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * radius;
            }
        }

        private static void SetHoverSphereActive(in HoverGizmo g, bool active)
        {
            if (g.RingX > 0) BasisGizmoManager.SetGizmoActive(g.RingX, active);
            if (g.RingY > 0) BasisGizmoManager.SetGizmoActive(g.RingY, active);
            if (g.RingZ > 0) BasisGizmoManager.SetGizmoActive(g.RingZ, active);
        }

        private static void HoverPrune()
        {
            if (_hoverGizmos.Count == _hoverSeen.Count)
            {
                return;
            }
            _hoverStale.Clear();
            foreach (KeyValuePair<BasisInput, HoverGizmo> kvp in _hoverGizmos)
            {
                if (!_hoverSeen.Contains(kvp.Key))
                {
                    _hoverStale.Add(kvp.Key);
                }
            }
            for (int i = 0; i < _hoverStale.Count; i++)
            {
                if (_hoverGizmos.TryGetValue(_hoverStale[i], out HoverGizmo g))
                {
                    DestroyHoverGizmo(g);
                    _hoverGizmos.Remove(_hoverStale[i]);
                }
            }
        }

        private static void HoverShutdown()
        {
            if (_hoverGizmos.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<BasisInput, HoverGizmo> kvp in _hoverGizmos)
            {
                DestroyHoverGizmo(kvp.Value);
            }
            _hoverGizmos.Clear();
        }

        private static void DestroyHoverGizmo(HoverGizmo g)
        {
            if (g.TargetLine > 0) BasisGizmoManager.DestroyGizmo(g.TargetLine);
            if (g.RingX > 0) BasisGizmoManager.DestroyGizmo(g.RingX);
            if (g.RingY > 0) BasisGizmoManager.DestroyGizmo(g.RingY);
            if (g.RingZ > 0) BasisGizmoManager.DestroyGizmo(g.RingZ);
            if (g.ListLines != null)
            {
                for (int i = 0; i < g.ListLines.Count; i++)
                {
                    BasisGizmoManager.DestroyGizmo(g.ListLines[i]);
                }
            }
        }

        private static void EnsureHoverHook()
        {
            if (_hoverHooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnHoverMasterToggleChanged;
            _hoverHooked = true;
        }

        // Master toggle going off destroys BasisGizmoManager's dictionaries, so our cached
        // IDs are stale — forget them so the next tick re-creates cleanly.
        private static void OnHoverMasterToggleChanged(bool state)
        {
            if (!state)
            {
                _hoverGizmos.Clear();
            }
        }

        public bool ForceSetInteracting(BasisInteractableObject interactableObject, BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && interactableObject.Inputs.ChangeStateByRole(role, BasisInteractInputState.Hovering))
            {
                for (int i = 0; i < InteractInputs.Length; i++)
                {
                    if (InteractInputs[i].IsInput(input))
                    {
                        BasisDebug.Log("Stole ownership, starting interact", BasisDebug.LogTag.Networking);
                        interactableObject.OnInteractStart(input);
                        InteractInputs[i].lastTarget = interactableObject;
                    }
                }

                return true;
            }

            return false;
        }

        public static bool IsDesktopCenterEye(BasisInput input)
        {
            return BasisDeviceManagement.IsUserInDesktop() &&
                   input.TryGetRole(out BasisBoneTrackedRole role) &&
                   role == BasisBoneTrackedRole.CenterEye;
        }

        /// <summary>
        /// Gets the closest InteractableObject in the given HoverSphere where IsInfluencable is true for the given input.
        /// </summary>
        /// <param name="hoverSphere">The hover sphere containing hover results.</param>
        /// <param name="input">The input used to check if the object is influencable.</param>
        /// <param name="result">Closest hover result.</param>
        /// <param name="interactable">Closest interactable.</param>
        /// <returns>True if a valid influencable object was found.</returns>
        private bool ClosestInfluencableHover(
            BasisHoverSphere hoverSphere,
            BasisInput input,
            out BasisHoverResult result,
            out BasisInteractableObject interactable)
        {
            for (int index = 0; index < hoverSphere.ResultCount; index++)
            {
                ref var hit = ref hoverSphere.Results[index];

                if (hit.collider == null) continue;

                // Check the collider itself first, then check parent GameObjects.
                // This matches PointRaycasterFindInteractable behavior — interactables
                // often live on a parent with colliders on child objects.
                BasisInteractableObject component = hit.collider.GetComponent<BasisInteractableObject>();
                if (component == null)
                {
                    component = hit.collider.GetComponentInParent<BasisInteractableObject>();
                    // Verify the hit collider belongs to this interactable
                    if (component != null)
                    {
                        Collider[] colliders = component.GetColliders();
                        if (colliders == null || !colliders.Contains(hit.collider))
                        {
                            component = null;
                        }
                    }
                }

                if (component != null && component.IsInfluencable(input))
                {
                    result = hit;
                    interactable = component;
                    return true;
                }
            }

            result = new BasisHoverResult();
            interactable = null;
            return false;
        }
    }
}
