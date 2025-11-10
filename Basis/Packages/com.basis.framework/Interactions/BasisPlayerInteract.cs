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
        public LayerMask IgnoreRaycasting;
        public LayerMask playerLayer;
        public LayerMask LocalPlayerAvatar;
        public static LayerMask Mask;
        public static QueryTriggerInteraction TriggerInteraction = QueryTriggerInteraction.UseGlobal;
        [Tooltip("How far the player can interact with objects. Must hold that raycastDistance > hoverRadius")]
        public float raycastDistance = 5.0f;
        [Tooltip("How far the player Hover.")]
        public float hoverRadius = 0.5f;
        // NOTE: this needs to be >= max number of colliders it can potentiall hit a scene, otherwise it will behave oddly
        public static int k_MaxPhysicHitCount = 128;
        public bool OnlySortClosest = true;
        [SerializeField]
        public BasisInteractInput[] InteractInputs = new BasisInteractInput[] { };

        public Material LineMaterial;
        private AsyncOperationHandle<Material> asyncOperationLineMaterial;
        public float interactLineWidth = 0.015f;
        public bool renderInteractLines = true;
        private bool interactLinesActive = false;

        public static string LoadMaterialAddress = "Interactable/InteractLineMat.mat";
        const int k_UpdatePriority = 201;
        public static BasisPlayerInteract Instance;
        public void OnEnable()
        {
            IgnoreRaycasting = LayerMask.NameToLayer("Ignore Raycast");
            playerLayer = LayerMask.NameToLayer("Player");
            LocalPlayerAvatar = LayerMask.NameToLayer("LocalPlayerAvatar");
            // Create a LayerMask that includes all layers
            LayerMask allLayers = ~0;

            // Exclude the "Ignore Raycast" and "Player" layers using bitwise AND and NOT operations
            Mask = allLayers & ~(1 << (int)IgnoreRaycasting) & ~(1 << (int)playerLayer) & ~(1 << (int)LocalPlayerAvatar);
        }
        private void Start()
        {
            Instance = this;
            BasisLocalPlayer.AfterFinalMove.AddAction(k_UpdatePriority, PollSystem);
            var Devices = BasisDeviceManagement.Instance.AllInputDevices;
            Devices.OnListAdded += OnInputChanged;
            Devices.OnListItemRemoved += OnInputRemoved;
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
            BasisLocalPlayer.AfterFinalMove.RemoveAction(k_UpdatePriority, PollSystem);
            var Device = BasisDeviceManagement.Instance.AllInputDevices;
            Device.OnListAdded -= OnInputChanged;
            Device.OnListItemRemoved -= OnInputRemoved;
            int count = InteractInputs.Length;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInteractInput input = InteractInputs[Index];
                if (input.lineRenderer != null)
                {
                    Destroy(input.lineRenderer.gameObject);
                    input.lineRenderer = null;
                }
            }
        }
        private void OnInputChanged(BasisInput Input)
        {
            // TODO: need a different config value for can interact/pickup/grab. Mainly input action/trigger values
            if (Input.HasRaycaster)
            {
                AddInput(Input);
            }
            // device removed handled elsewhere
        }
        private void OnInputRemoved(BasisInput input)
        {
            RemoveInput(input.UniqueDeviceIdentifier);
        }
        // simulate after IK update
        [BurstCompile]
        private void PollSystem()
        {
#if UNITY_EDITOR//just remove when your profiling this
            UnityEngine.Profiling.Profiler.BeginSample("Interactable System");
#endif
            if (InteractInputs == null)
            {
                return;
            }
            var InteractInputsCount = InteractInputs.Length;
            if (InteractInputsCount == 0)
            {
                return;
            }
            for (int Index = 0; Index < InteractInputsCount; Index++)
            {
                BasisInteractInput interactInput = InteractInputs[Index];
                if (interactInput.input == null)
                {
                    BasisDebug.LogWarning("Pickup input device unexpectedly null, input devices likely changed");
                    continue;
                }
                BasisHoverSphere hoverSphere = interactInput.hoverSphere;

                // poll hover
                hoverSphere.PollSystem(interactInput.input.RaycastCoord.position);

                RaycastHit rayHit;
                BasisInteractableObject hitInteractable = null;
                bool isValidRayHit =
                    interactInput.input.BasisPointRaycaster.FirstHit(out rayHit, raycastDistance) && // UI will block pickup interact
                    ((1 << rayHit.collider.gameObject.layer) & Mask) != 0 &&
                    rayHit.collider.TryGetComponent(out hitInteractable);

                bool isValidHoverHit = false;
                if (hoverSphere.ResultCount != 0 && ClosestInfluencableHover(hoverSphere, interactInput.input) is var result && result.Item2 != null)
                {
                    isValidHoverHit = true;
                    hitInteractable = result.Item2;
                }

                if (isValidRayHit || isValidHoverHit)
                {
                    if (hitInteractable != null)
                    {
                        // NOTE: this will skip a frame of hover after stopping interact
                        interactInput = UpdatePickupState(hitInteractable, interactInput);
                    }
                    else
                    {
                        BasisDebug.LogWarning("Player Interact expected a registered hit but found null. This is a bug, please report.");
                    }
                }
                // hover misssed entirely. test for drop & clear hover
                else
                {
                    if (interactInput.lastTarget != null)
                    {
                        // Implementation could allow for hovering and holding of the same object, clear independently

                        bool autoHold = BasisDeviceManagement.IsUserInDesktop() && interactInput.lastTarget.AutoHold == BasisAutoHold.Yes;
                        // TODO: proximity check so we dont keep interacting with objects out side of player's reach. Needs an impl that wont break under lag though. `|| !interactInput.targetObject.IsWithinRange(interactInput.input.transform)`
                        // Drop logic: only drop when not triggered
                        if (
                            !interactInput.lastTarget.IsInteractTriggered(interactInput.input) &&
                            interactInput.lastTarget.IsInteractingWith(interactInput.input) &&
                            !autoHold
                        )
                        {
                            interactInput.lastTarget.OnInteractEnd(interactInput.input);
                        }

                        if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                        {
                            interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                        }
                    }
                }

                // write changes back
                InteractInputs[Index] = interactInput;
            }
            // TODO: replace with UniqueCounterList
            // Iterate over all the inputs
            for (int Index = 0; Index < InteractInputsCount; Index++)
            {
                BasisInteractInput input = InteractInputs[Index];
                if (input.lastTarget != null && input.lastTarget.RequiresUpdateLoop)
                {
                    input.lastTarget.InputUpdate();
                }
            }


            // apply line renderer
            if (renderInteractLines)
            {
                interactLinesActive = true;
                for (int Index = 0; Index < InteractInputsCount; Index++)
                {
                    BasisInteractInput input = InteractInputs[Index];
                    if (input.lastTarget != null && input.lastTarget.IsHoveredBy(input.input))
                    {
                        Vector3 origin = input.input.RaycastCoord.position;
                        Vector3 start;
                        // desktop offset for center eye (a little to the bottom right)
                        if (IsDesktopCenterEye(input.input))
                        {
                            start = input.input.RaycastCoord.position + (input.input.RaycastCoord.rotation * Vector3.forward * 0.1f) + Vector3.down * 0.1f + (input.input.RaycastCoord.rotation * Vector3.right * 0.1f);
                        }
                        else
                        {
                            start = origin;
                        }
                        if (input.lineRenderer != null)
                        {
                            Vector3 endPos = input.lastTarget.GetCollider().ClosestPoint(origin);
                            input.lineRenderer.SetPosition(0, start);
                            input.lineRenderer.SetPosition(1, endPos);
                            input.lineRenderer.enabled = true;
                        }
                    }
                    else
                    {
                        if (input.lineRenderer)
                        {
                            input.lineRenderer.enabled = false;
                        }
                    }
                }
            }
            // turn all the lines off
            else if (interactLinesActive)
            {
                interactLinesActive = false;
                for (int Index = 0; Index < InteractInputsCount; Index++)
                {
                    BasisInteractInput input = InteractInputs[Index];
                    input.lineRenderer.enabled = false;
                }
            }
#if UNITY_EDITOR//just remove when your profiling this
            UnityEngine.Profiling.Profiler.EndSample();
#endif
        }
        private BasisInteractInput UpdatePickupState(BasisInteractableObject hitInteractable, BasisInteractInput interactInput)
        {
            // hit a different target than last time
            if (interactInput.lastTarget != null && interactInput.lastTarget.GetInstanceID() != hitInteractable.GetInstanceID())
            {
                bool holdDropTriggered = interactInput.lastTarget.IsHoldDropTriggered(interactInput.input);

                // Holding Logic:
                // last target had input trigger
                if (interactInput.lastTarget.IsInteractTriggered(interactInput.input))
                {
                    // clear hover of last
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                    }

                    bool shouldHold = hitInteractable.AutoHold == BasisAutoHold.Yes; // TODO before merge, is dooly being silly

                    // interacted with new hit since last frame & we aren't holding (in which case do nothing)
                    if (hitInteractable.CanInteract(interactInput.input) &&
                        (!interactInput.lastTarget.IsInteractingWith(interactInput.input) || shouldHold))
                    {
                        hitInteractable.OnInteractStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }
                // No primary trigger
                // auto hold & remove
                else
                {
                    bool removeTarget = false;

                    bool autoHoldDropped = true;
                    if (IsDesktopCenterEye(interactInput.input))
                    {
                        autoHoldDropped = interactInput.lastTarget.AutoHold != BasisAutoHold.Yes ||
                                            interactInput.lastTarget.AutoHold == BasisAutoHold.Yes &&
                                            holdDropTriggered;
                    }

                    // end interact of hit (unlikely since we just hit it this update)
                    if (hitInteractable.IsInteractingWith(interactInput.input))
                    {
                        hitInteractable.OnInteractEnd(interactInput.input);
                    }

                    // end interact of previous object
                    if (
                        interactInput.lastTarget.IsInteractingWith(interactInput.input) && autoHoldDropped
                    )
                    {
                        interactInput.lastTarget.OnInteractEnd(interactInput.input);
                        removeTarget = true;
                    }

                    // hover missed previous object
                    if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                    {
                        interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                        removeTarget = true;
                    }

                    if (removeTarget)
                    {
                        interactInput.lastTarget = null;
                    }

                    // try hovering new interactable
                    if (hitInteractable.CanHover(interactInput.input) && autoHoldDropped)
                    {
                        hitInteractable.OnHoverStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }
            }
            // hitting same interactable
            else
            {
                if (hitInteractable.IsInteractTriggered(interactInput.input))
                {
                    // first clear hover
                    if (hitInteractable.IsHoveredBy(interactInput.input))
                    {
                        hitInteractable.OnHoverEnd(interactInput.input, hitInteractable.CanInteract(interactInput.input));
                    }

                    // then try to interact
                    bool shouldHold = hitInteractable.AutoHold == BasisAutoHold.Yes;// || interactInput.input.isHeld

                    if (hitInteractable.CanInteract(interactInput.input))
                    {
                        if (!hitInteractable.IsInteractingWith(interactInput.input) || shouldHold)
                        {
                            hitInteractable.OnInteractStart(interactInput.input);
                            interactInput.lastTarget = hitInteractable;
                        }
                    }
                }
                else
                {

                    bool autoHoldDropped = true;
                    if (IsDesktopCenterEye(interactInput.input))
                    {
                        autoHoldDropped = hitInteractable.AutoHold != BasisAutoHold.Yes ||
                                            hitInteractable.AutoHold == BasisAutoHold.Yes &&
                                            hitInteractable.IsHoldDropTriggered(interactInput.input);
                    }

                    // end interact if not holding and we're still interacting
                    if (hitInteractable.IsInteractingWith(interactInput.input) && autoHoldDropped)
                    {
                        hitInteractable.OnInteractEnd(interactInput.input);
                    }

                    // hover logic
                    if (hitInteractable.CanHover(interactInput.input))
                    {
                        hitInteractable.OnHoverStart(interactInput.input);
                        interactInput.lastTarget = hitInteractable;
                    }
                }
            }

            return interactInput;
        }
        private void RemoveInput(string uid)
        {
            // Find the inputs to remove based on the UID
            BasisInteractInput[] inputs = InteractInputs.Where(x => x.deviceUid == uid).ToArray();
            int length = inputs.Length;

            if (length > 0) // If matching inputs were found
            {
                BasisInteractInput input = inputs[0];

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

                // Destroy the interact origin
                if (input.lineRenderer != null)
                {
                    Destroy(input.lineRenderer.gameObject);
                    input.lineRenderer = null;
                }
                // Manually resize the array
                InteractInputs = InteractInputs
                    .Where(x => x.deviceUid != input.deviceUid) // Exclude the removed input
                    .ToArray();
            }
            else
            {
                BasisDebug.LogError($"Interact Inputs has multiple inputs of the same UID {uid}. Please report this bug.");
            }
        }
        private void AddInput(BasisInput input)
        {
            GameObject interactOrigin = new GameObject($"{input.name} Line Renderer");

            LineRenderer lineRenderer = interactOrigin.AddComponent<LineRenderer>();

            // deskies cant hover grab :)
            // TODO: pass up max hits for config
            BasisHoverSphere hoverSphere = new BasisHoverSphere(input.RaycastCoord.position, hoverRadius, 128, Mask, !IsDesktopCenterEye(input), OnlySortClosest);

            interactOrigin.transform.SetParent(BasisLocalPlayer.Instance.transform);
            interactOrigin.layer = IgnoreRaycasting;
            interactOrigin.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            lineRenderer.enabled = false;
            lineRenderer.material = LineMaterial;
            lineRenderer.startWidth = interactLineWidth;
            lineRenderer.endWidth = interactLineWidth;
            lineRenderer.useWorldSpace = true;
            lineRenderer.textureMode = LineTextureMode.Tile;
            lineRenderer.positionCount = 2;
            lineRenderer.numCapVertices = 0;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            BasisInteractInput interactInput = new BasisInteractInput()
            {
                deviceUid = input.UniqueDeviceIdentifier,
                input = input,
                lineRenderer = lineRenderer,
                hoverSphere = hoverSphere,
            };
            List<BasisInteractInput> interactInputList = InteractInputs.ToList();
            interactInputList.Add(interactInput);
            InteractInputs = interactInputList.ToArray();
        }
        private void OnDrawGizmos()
        {
            int count = InteractInputs.Length;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInteractInput device = InteractInputs[Index];


                Gizmos.color = Color.magenta;
                if (device.hoverSphere.ResultCount > 1)
                {
                    var hits = device.hoverSphere.Results[1..device.hoverSphere.ResultCount] // skip first, is colored later
                        .Select(hit => hit.collider.TryGetComponent(out BasisInteractableObject component) ? (hit, component) : (default, null))
                        .Where(hit => hit.component != null && hit.hit.distanceToCenter != float.NegativeInfinity);
                    // hover list
                    foreach (var hit in hits)
                    {
                        // BasisDebug.Log($"hit: {hit}");
                        Gizmos.DrawLine(device.input.RaycastCoord.position, hit.Item1.closestPointToCenter);
                    }
                }


                // hover target
                Gizmos.color = Color.blue;
                if (device.hoverSphere != null && ClosestInfluencableHover(device.hoverSphere, device.input) is var result && result.Item2 != null)
                {
                    Gizmos.DrawLine(device.input.RaycastCoord.position, result.Item1.closestPointToCenter);
                }
                Gizmos.color = Color.gray;

                // hover sphere
                if (!IsDesktopCenterEye(device.input))
                {
                    Gizmos.DrawWireSphere(device.hoverSphere.WorldPosition, hoverRadius);
                }
            }
        }


        public bool ForceSetInteracting(BasisInteractableObject interactableObject, BasisInput input)
        {
            if (
                input.TryGetRole(out BasisBoneTrackedRole role) &&
                interactableObject.Inputs.ChangeStateByRole(role, BasisInteractInputState.Hovering)
                )
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
            else return false;
        }
        public bool IsDesktopCenterEye(BasisInput input)
        {
            return BasisDeviceManagement.IsUserInDesktop() && input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye;
        }
        /// <summary>
        /// Gets the closest InteractableObject in the given HoverSphere where IsInfluencable is true for the given input.
        /// </summary>
        /// <param name="hoverSphere">The hover sphere containing hover results.</param>
        /// <param name="input">The input used to check if the object is influencable.</param>
        /// <returns>
        /// A tuple containing the HoverResult and the corresponding InteractableObject that is influencable, or default values if none is found.
        /// </returns>
        private (BasisHoverResult, BasisInteractableObject) ClosestInfluencableHover(BasisHoverSphere hoverSphere, BasisInput input)
        {
            for (int Index = 0; Index < hoverSphere.ResultCount; Index++)
            {
                ref var hit = ref hoverSphere.Results[Index];

                if (hit.collider != null && hit.collider.TryGetComponent<BasisInteractableObject>(out var component))
                {
                    if (component.IsInfluencable(input))
                    {
                        return (hit, component);
                    }
                }
            }

            // Return default if none found
            return (default, null);
        }
    }
}
