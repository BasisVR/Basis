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
using static Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject;

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
            if (input.HasRaycaster)
            {
                RemoveInput(input.UniqueDeviceIdentifier);
            }
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

                BasisHoverSphere hoverSphere = interactInput.input.hoverSphere;

                // Poll hover
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
                // Hover missed entirely. Test for drop & clear hover
                else
                {
                    interactInput.HasvalidRay = false;
                    if (interactInput.lastTarget != null)
                    {
                        // Implementation could allow for hovering and holding of the same object, clear independently
                        bool autoHold = BasisDeviceManagement.IsUserInDesktop() && interactInput.lastTarget.AutoHold == BasisAutoHold.Yes;

                        // Drop logic: only drop when not triggered
                        if (!interactInput.lastTarget.IsInteractTriggered(interactInput.input) && interactInput.lastTarget.IsInteractingWith(interactInput.input) && !autoHold)
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

            // Apply line renderer
            if (renderInteractLines)
            {
                interactLinesActive = true;

                for (int index = 0; index < interactInputsCount; index++)
                {
                    var input = InteractInputs[index];
                    if (input.lastTarget != null && input.lastTarget.IsHoveredBy(input.input))
                    {
                        Vector3 origin = input.input.RaycastCoord.position;
                        Vector3 start;

                        // Desktop offset for center eye (a little to the bottom right)
                        if (IsDesktopCenterEye(input.input))
                        {
                            start =
                                input.input.RaycastCoord.position +
                                (input.input.RaycastCoord.rotation * Vector3.forward * 0.1f) +
                                Vector3.down * 0.1f +
                                (input.input.RaycastCoord.rotation * Vector3.right * 0.1f);
                        }
                        else
                        {
                            start = origin;
                        }

                        if (input.input.InteractionLineRenderer != null)
                        {
                            Vector3 endPos = input.lastTarget.GetClosestPoint(origin);
                            input.input.InteractionLineRenderer.SetPosition(0, start);
                            input.input.InteractionLineRenderer.SetPosition(1, endPos);
                            input.input.InteractionLineRenderer.enabled = true;
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
            bool hit = interactInput.input.BasisPointRaycaster.FirstHit(out RaycastHit rayHit, raycastDistance);
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
            // Handy context for logs
            string inputId = interactInput.input != null ? interactInput.input.ToString() : "null";
            int hitId = hitInteractable != null ? hitInteractable.GetInstanceID() : -1;

            // Hit a different target than last time
            if (interactInput.lastTarget != null && interactInput.lastTarget.GetInstanceID() != hitInteractable.GetInstanceID())
            {
                //  Debug.Log($"[Pickup] Branch: Different target. LastTarget={interactInput.lastTarget.name}({interactInput.lastTarget.GetInstanceID()}), NewHit={hitInteractable.name}({hitId}), Input={inputId}");

                bool holdDropTriggered = interactInput.lastTarget.IsHoldDropTriggered(interactInput.input);
                //  Debug.Log($"[Pickup] holdDropTriggered for lastTarget={holdDropTriggered}");

                // Holding Logic:
                // last target had input trigger
                if (interactInput.lastTarget.IsInteractTriggered(interactInput.input))
                {
                    //  Debug.Log("[Pickup] Different target: lastTarget had interact trigger.");

                    // Clear hover of last
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        //  Debug.Log($"[Pickup] Ending hover on lastTarget '{interactInput.lastTarget.name}' due to new interact.");
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                    }

                    bool shouldHold = hitInteractable.AutoHold == BasisAutoHold.Yes;
                    //  Debug.Log($"[Pickup] shouldHold on new hit: {shouldHold}");

                    // Interacted with new hit since last frame & we aren't holding (in which case do nothing)
                    if (hitInteractable.CanInteract(interactInput.input) &&
                        (!interactInput.lastTarget.IsInteractingWith(interactInput.input) || shouldHold))
                    {
                        //  Debug.Log($"[Pickup] Starting interaction on NEW hit '{hitInteractable.name}' from lastTarget branch. shouldHold={shouldHold}");
                        hitInteractable.OnInteractStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                    else
                    {
                        //   Debug.Log("[Pickup] Did NOT start interaction on new hit (CanInteract or hold conditions failed).");
                    }
                }
                // No primary trigger
                // auto hold & remove
                else
                {
                    //   Debug.Log("[Pickup] Different target: lastTarget does NOT have interact trigger (no primary trigger).");

                    bool removeTarget = false;

                    bool autoHoldDropped = true;
                    if (IsDesktopCenterEye(interactInput.input))
                    {
                        autoHoldDropped =
                            interactInput.lastTarget.AutoHold != BasisAutoHold.Yes ||
                            (interactInput.lastTarget.AutoHold == BasisAutoHold.Yes && holdDropTriggered);

                        //     Debug.Log($"[Pickup] DesktopCenterEye: autoHoldDropped={autoHoldDropped}, lastTarget.AutoHold={interactInput.lastTarget.AutoHold}, holdDropTriggered={holdDropTriggered}");
                    }

                    // End interact of hit (unlikely since we just hit it this update)
                    if (hitInteractable.IsInteractingWith(interactInput.input))
                    {
                        //   Debug.Log($"[Pickup] Ending interaction on NEW hit '{hitInteractable.name}' (unexpected ongoing interact).");
                        hitInteractable.OnInteractEnd(interactInput.input);
                    }

                    // End interact of previous object
                    if (interactInput.lastTarget.IsInteractingWith(interactInput.input) && autoHoldDropped)
                    {
                        //   Debug.Log($"[Pickup] Ending interaction on LAST target '{interactInput.lastTarget.name}' due to autoHoldDropped.");
                        interactInput.lastTarget.OnInteractEnd(interactInput.input);
                        removeTarget = true;
                    }

                    // Hover missed previous object
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        // Debug.Log($"[Pickup] Ending hover on LAST target '{interactInput.lastTarget.name}' (hover missed).");
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                        removeTarget = true;
                    }

                    if (removeTarget)
                    {
                        //    Debug.Log("[Pickup] Clearing lastTarget reference.");
                        interactInput.lastTarget = null;
                    }

                    // Try hovering new interactable
                    if (hitInteractable.CanHover(interactInput.input) && autoHoldDropped)
                    {
                        //  Debug.Log($"Was able to hover");
                        hitInteractable.OnHoverStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                    else
                    {
                        //     Debug.Log($"Was not able to hover {hitInteractable.CanHover(interactInput.input)} && {autoHoldDropped}");
                    }
                }
            }
            // Hitting same interactable
            else
            {
                // Debug.Log($"[Pickup] Branch: Same target OR no lastTarget. Hit={hitInteractable.name}({hitId}), LastTarget={(interactInput.lastTarget ? interactInput.lastTarget.name : "null")}, Input={inputId}");

                if (hitInteractable.IsInteractTriggered(interactInput.input))
                {
                    //  Debug.Log("[Pickup] Same target: interact TRIGGERED.");

                    // First clear hover
                    if (hitInteractable.IsHoveredBy(interactInput.input))
                    {
                        bool canInteractNow = hitInteractable.CanInteract(interactInput.input);
                        //   Debug.Log($"[Pickup] Ending hover on '{hitInteractable.name}' before interact. canInteractNow={canInteractNow}");
                        hitInteractable.OnHoverEnd(interactInput.input, canInteractNow);
                    }

                    // Then try to interact
                    bool shouldHold = hitInteractable.AutoHold == BasisAutoHold.Yes; // || interactInput.input.isHeld
                                                                                     //   Debug.Log($"[Pickup] shouldHold (same target)={shouldHold}");

                    if (hitInteractable.CanInteract(interactInput.input))
                    {
                        if (!hitInteractable.IsInteractingWith(interactInput.input) || shouldHold)
                        {
                            //    Debug.Log($"[Pickup] Starting interaction on SAME target '{hitInteractable.name}'. shouldHold={shouldHold}");
                            hitInteractable.OnInteractStart(interactInput.input);
                            interactInput.lastTarget = hitInteractable;
                        }
                        else
                        {
                            //  Debug.Log("[Pickup] Interact trigger ignored: already interacting and !shouldHold.");
                        }
                    }
                    else
                    {
                        //    Debug.Log("[Pickup] Interact trigger but CanInteract returned false.");
                    }
                }
                else
                {
                    //   Debug.Log("[Pickup] Same target: interact NOT triggered (no primary trigger).");

                    bool autoHoldDropped = true;
                    if (IsDesktopCenterEye(interactInput.input))
                    {
                        autoHoldDropped =
                            hitInteractable.AutoHold != BasisAutoHold.Yes ||
                            (hitInteractable.AutoHold == BasisAutoHold.Yes &&
                             hitInteractable.IsHoldDropTriggered(interactInput.input));

                        //   Debug.Log($"[Pickup] DesktopCenterEye (same target): autoHoldDropped={autoHoldDropped}, AutoHold={hitInteractable.AutoHold}");
                    }

                    // End interact if not holding and we're still interacting
                    if (hitInteractable.IsInteractingWith(interactInput.input) && autoHoldDropped)
                    {
                        //  Debug.Log($"[Pickup] Ending interaction on SAME target '{hitInteractable.name}' due to autoHoldDropped.");
                        hitInteractable.OnInteractEnd(interactInput.input);
                    }

                    // Hover logic
                    if (hitInteractable.CanHover(interactInput.input))
                    {
                        //   Debug.Log($"[Pickup] Starting/maintaining hover on SAME target '{hitInteractable.name}'.");
                        hitInteractable.OnHoverStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                    else
                    {
                        //  Debug.Log($"[Pickup] Cannot hover SAME target '{hitInteractable.name}'. CanHover returned false.");
                    }
                }
            }
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
                BasisDebug.LogError($"Interact Inputs did not include {uid}. Please report this bug.");
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

        public static void DrawAll()
        {
            if (Instance.InteractInputs == null)
            {
                return;
            }

            int count = Instance.InteractInputs.Length;
            for (int index = 0; index < count; index++)
            {
                var device = Instance.InteractInputs[index];

                if (device.input == null || device.input.hoverSphere == null)
                {
                    continue;
                }

                Gizmos.color = Color.magenta;

                if (device.input.hoverSphere.ResultCount > 1)
                {
                    var hits = device.input.hoverSphere
                        .Results[1..device.input.hoverSphere.ResultCount] // skip first, is colored later
                        .Select(hit => hit.collider.TryGetComponent(out BasisInteractableObject component)
                            ? (hit, component)
                            : (default, null))
                        .Where(hit => hit.component != null && hit.hit.distanceToCenter != float.NegativeInfinity);

                    // Hover list
                    foreach (var hit in hits)
                    {
                        Gizmos.DrawLine(device.input.RaycastCoord.position, hit.Item1.closestPointToCenter);
                    }
                }

                // Hover target
                Gizmos.color = Color.blue;
                if (Instance.ClosestInfluencableHover(device.input.hoverSphere, device.input, out var result, out _))
                {
                    Gizmos.DrawLine(device.input.RaycastCoord.position, result.closestPointToCenter);
                }

                Gizmos.color = Color.gray;

                // Hover sphere
                if (!IsDesktopCenterEye(device.input))
                {
                    Gizmos.DrawWireSphere(device.input.hoverSphere.WorldPosition, hoverRadius);
                }
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

                if (hit.collider != null &&
                    hit.collider.TryGetComponent(out BasisInteractableObject component) &&
                    component.IsInfluencable(input))
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
