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
public class PlayerInteract : MonoBehaviour
{


    [Tooltip("How far the player can interact with objects. Must hold that raycastDistance > hoverRadius")]
    public float raycastDistance = 5.0f;
    [Tooltip("How far the player Hover.")]
    public float hoverRadius = 0.5f;

    public bool OnlySortClosest = true;
    [Tooltip("Both of the above are relative to object transforms, objects with larger colliders may have issues")]
    [System.Serializable]
    public struct InteractInput
    {
        [HideInInspector]
        [field: System.NonSerialized]
        public string deviceUid { get; set; }
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public BasisInput input { get; set; }
        [HideInInspector]
        [field: System.NonSerialized]
        public Transform interactOrigin { get; set; }
        // TODO: use this ref
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public LineRenderer lineRenderer { get; set; }
        [SerializeField]
        public HoverSphere hoverSphere { get; set; }
        [SerializeField]
        [HideInInspector]
        [field: System.NonSerialized]
        public InteractableObject lastTarget { get; set; }

        public bool IsInput(BasisInput input)
        {
            return deviceUid == input.UniqueDeviceIdentifier;
        }
    }
    [SerializeField]
    public InteractInput[] InteractInputs = new InteractInput[] { };

    public Material LineMaterial;
    private AsyncOperationHandle<Material> asyncOperationLineMaterial;
    public float interactLineWidth = 0.015f;
    public bool renderInteractLines = true;
    private bool interactLinesActive = false;

    public static string LoadMaterialAddress = "Interactable/InteractLineMat.mat";
    const int k_UpdatePriority = 201;
    public LayerMask InteractableLayerMask;
    private void Start()
    {
        BasisLocalPlayer.Instance.AfterFinalMove.AddAction(k_UpdatePriority, PollSystem);
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
        BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(k_UpdatePriority, PollSystem);
        var Device = BasisDeviceManagement.Instance.AllInputDevices;
        Device.OnListAdded -= OnInputChanged;
        Device.OnListItemRemoved -= OnInputRemoved;
        int count = InteractInputs.Length;
        for (int Index = 0; Index < count; Index++)
        {
            InteractInput input = InteractInputs[Index];
            if (input.interactOrigin != null)
            {
                Destroy(input.interactOrigin.gameObject);
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
            InteractInput interactInput = InteractInputs[Index];
            if (interactInput.input == null)
            {
                BasisDebug.LogWarning("Pickup input device unexpectedly null, input devices likely changed");
                continue;
            }
            HoverSphere hoverSphere = interactInput.hoverSphere;

            // poll hover
            hoverSphere.PollSystem(interactInput.interactOrigin.position);

            RaycastHit rayHit;
            InteractableObject hitInteractable = null;
            bool isValidRayHit =
                interactInput.input.BasisPointRaycaster.FirstHit(out rayHit, raycastDistance) && // UI will block pickup interact
                ((1 << rayHit.collider.gameObject.layer) & InteractableLayerMask) != 0 &&
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
                    // seperate if blocks in case implementation allows for hovering and holding of the same object

                    // TODO: proximity check so we dont keep interacting with objects out side of player's reach. Needs an impl that wont break under lag though. `|| !interactInput.targetObject.IsWithinRange(interactInput.input.transform)`
                    // Drop logic: only drop when not triggered
                    if (!interactInput.lastTarget.IsInteractTriggered(interactInput.input) && interactInput.lastTarget.IsInteractingWith(interactInput.input))
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
            InteractInput input = InteractInputs[Index];
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
                InteractInput input = InteractInputs[Index];
                if (input.lastTarget != null && input.lastTarget.IsHoveredBy(input.input))
                {
                    Vector3 origin = input.interactOrigin.position;
                    Vector3 start;
                    // desktop offset for center eye (a little to the bottom right)
                    if (IsDesktopCenterEye(input.input))
                    {
                        start = input.interactOrigin.position + (input.interactOrigin.forward * 0.1f) + Vector3.down * 0.1f + (input.interactOrigin.right * 0.1f);
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
                InteractInput input = InteractInputs[Index];
                input.lineRenderer.enabled = false;
            }
        }
#if UNITY_EDITOR//just remove when your profiling this
        UnityEngine.Profiling.Profiler.EndSample();
#endif
    }

    private InteractInput UpdatePickupState(InteractableObject hitInteractable, InteractInput interactInput)
    {
        // hit a different target than last time
        if (interactInput.lastTarget != null && interactInput.lastTarget.GetInstanceID() != hitInteractable.GetInstanceID())
        {
            // Holding Logic: 
            if (interactInput.lastTarget.IsInteractTriggered(interactInput.input))
            {
                // clear hover
                if (interactInput.lastTarget.IsHoveredBy(interactInput.input))
                {
                    interactInput.lastTarget.OnHoverEnd(interactInput.input, false);
                }

                // interacted with new hit since last frame & we arent holding (in which case do nothing)
                if (hitInteractable.CanInteract(interactInput.input) && !interactInput.lastTarget.IsInteractingWith(interactInput.input))
                {
                    hitInteractable.OnInteractStart(interactInput.input);
                    interactInput.lastTarget = hitInteractable;
                }
            }
            // No trigger
            else
            {
                bool removeTarget = false;
                // end iteract of hit (unlikely since we just hit it this update)
                if (hitInteractable.IsInteractingWith(interactInput.input))
                {
                    hitInteractable.OnInteractEnd(interactInput.input);
                }
                // end interact of previous object
                if (interactInput.lastTarget.IsInteractingWith(interactInput.input))
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

                // remove here in case both hover and interact ended
                if (removeTarget)
                {
                    interactInput.lastTarget = null;
                }

                // try hovering new interactable
                if (hitInteractable.CanHover(interactInput.input))
                {
                    hitInteractable.OnHoverStart(interactInput.input);
                    interactInput.lastTarget = hitInteractable;
                }
            }
        }
        // hitting same interactable
        else
        {
            // Pickup logic: 
            // per input an object can be either held or hovered, not both. Objects can ignore this by purposfully modifying IsHovered/IsInteracted.
            if (hitInteractable.IsInteractTriggered(interactInput.input))
            {
                // first clear hover...
                if (hitInteractable.IsHoveredBy(interactInput.input))
                {
                    // will interact this frame
                    hitInteractable.OnHoverEnd(interactInput.input, hitInteractable.CanInteract(interactInput.input));
                }

                // then try to interact
                // TODO: hand set pickup limitations
                if (hitInteractable.CanInteract(interactInput.input))
                {
                    hitInteractable.OnInteractStart(interactInput.input);
                    interactInput.lastTarget = hitInteractable;
                }
            }
            // not holding
            // hover if we arent holding, drop any held
            else
            {
                // first end interact...
                if (hitInteractable.IsInteractingWith(interactInput.input))
                {
                    hitInteractable.OnInteractEnd(interactInput.input);
                }

                // then hover
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
        InteractInput[] inputs = InteractInputs.Where(x => x.deviceUid == uid).ToArray();
        int length = inputs.Length;

        if (length > 0) // If matching inputs were found
        {
            InteractInput input = inputs[0];

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
            Destroy(input.interactOrigin.gameObject);

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
        GameObject interactOrigin = new GameObject("Interact Origin");

        LineRenderer lineRenderer = interactOrigin.AddComponent<LineRenderer>();

        // deskies cant hover grab :)
        // TODO: pass up max hits for config 
        HoverSphere hoverSphere = new HoverSphere(interactOrigin.transform.position, hoverRadius, 128, InteractableLayerMask, !IsDesktopCenterEye(input), OnlySortClosest);

        interactOrigin.transform.SetParent(input.transform);
        interactOrigin.layer = LayerMask.NameToLayer("Ignore Raycast");
        // TODO: custom config to use center of palm instead of raycast offset (IK palm? but that breaks input on a bad avi upload, no?)
        interactOrigin.transform.SetLocalPositionAndRotation(input.BasisDeviceMatchSettings.PositionRayCastOffset, Quaternion.Euler(input.BasisDeviceMatchSettings.RotationRaycastOffset));

        lineRenderer.enabled = false;
        lineRenderer.material = LineMaterial;
        lineRenderer.startWidth = interactLineWidth;
        lineRenderer.endWidth = interactLineWidth;
        lineRenderer.useWorldSpace = true;
        lineRenderer.textureMode = LineTextureMode.Tile;
        lineRenderer.positionCount = 2;
        lineRenderer.numCapVertices = 0;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;




        InteractInput interactInput = new InteractInput()
        {
            deviceUid = input.UniqueDeviceIdentifier,
            input = input,
            interactOrigin = interactOrigin.transform,
            lineRenderer = lineRenderer,
            hoverSphere = hoverSphere,
        };
        List<InteractInput> interactInputList = InteractInputs.ToList();
        interactInputList.Add(interactInput);
        InteractInputs = interactInputList.ToArray();
    }

    private void OnDrawGizmos()
    {
        int count = InteractInputs.Length;
        for (int Index = 0; Index < count; Index++)
        {
            InteractInput device = InteractInputs[Index];


            Gizmos.color = Color.magenta;
            if (device.hoverSphere.ResultCount > 1)
            {
                var hits = device.hoverSphere.Results[1..device.hoverSphere.ResultCount] // skip first, is colored later
                    .Select(hit => hit.collider.TryGetComponent(out InteractableObject component) ? (hit, component) : (default, null))
                    .Where(hit => hit.component != null && hit.hit.distanceToCenter != float.NegativeInfinity);
                // hover list
                foreach (var hit in hits)
                {
                    // BasisDebug.Log($"hit: {hit}");
                    Gizmos.DrawLine(device.interactOrigin.position, hit.Item1.closestPointToCenter);
                }
            }


            // hover target
            Gizmos.color = Color.blue;
            if (device.hoverSphere != null && ClosestInfluencableHover(device.hoverSphere, device.input) is var result && result.Item2 != null)
            {
                Gizmos.DrawLine(device.interactOrigin.position, result.Item1.closestPointToCenter);
            }
            Gizmos.color = Color.gray;

            // hover sphere
            if (!IsDesktopCenterEye(device.input))
            {
                Gizmos.DrawWireSphere(device.hoverSphere.WorldPosition, hoverRadius);
            }
        }
    }

    public bool IsDesktopCenterEye(BasisInput input)
    {
        return input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye;
    }

    /// <summary>
    /// Gets the closest InteractableObject in the given HoverSphere where IsInfluencable is true for the given input.
    /// </summary>
    /// <param name="hoverSphere">The hover sphere containing hover results.</param>
    /// <param name="input">The input used to check if the object is influencable.</param>
    /// <returns>
    /// A tuple containing the HoverResult and the corresponding InteractableObject that is influencable, or default values if none is found.
    /// </returns>
    private (HoverSphere.HoverResult, InteractableObject) ClosestInfluencableHover(HoverSphere hoverSphere, BasisInput input)
    {
        for (int Index = 0; Index < hoverSphere.ResultCount; Index++)
        {
            ref var hit = ref hoverSphere.Results[Index];

            if (hit.collider != null && hit.collider.TryGetComponent<InteractableObject>(out var component))
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
