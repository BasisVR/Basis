using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.Scripts.BasisSdk.Interactions
{
    public class BasisSeat : BasisInteractableObject
    {
        public enum ShowSeatHighlightMode
        {
            Never,
            Always,
            OnHover,
            OnHoverOrInEditor,
        }

        public ShowSeatHighlightMode highlightMode = ShowSeatHighlightMode.OnHoverOrInEditor;

        #region Seat Internals
        [Header("Seat Control Points")]
        [SerializeField] private Vector3 _back = new(0.0f, 0.0f, -0.25f);
        [SerializeField] private Vector3 _foot = new(0.0f, -0.5f, 0.25f);
        [SerializeField] private Vector3 _knee = new(0.0f, 0.0f, 0.25f);
        [SerializeField, Range(0.1f, 179.9f)] private double _spineAngleDegrees = 90.0;

        /// <summary>
        /// The seat position control point corresponding to the character's back position in meters.
        /// Note that all points describe the seat itself in local space, not the positions of the character's bones.
        /// </summary>
        [Header("Seat Control Points")]
        public Vector3 Back
        {
            get => _back;
            set
            {
                _back = value;
                OnValidate();
            }
        }

        /// <summary>
        /// The seat position control point corresponding to the character's foot position in meters.
        /// Note that all points describe the seat itself in local space, not the positions of the character's bones.
        /// </summary>
        public Vector3 Foot
        {
            get => _foot;
            set
            {
                _foot = value;
                OnValidate();
            }
        }

        /// <summary>
        /// The seat position control point corresponding to the character's knee position in meters.
        /// Note that all points describe the seat itself in local space, not the positions of the character's bones.
        /// </summary>
        public Vector3 Knee
        {
            get => _knee;
            set
            {
                _knee = value;
                OnValidate();
            }
        }

        /// <summary>
        /// The seat angle between the spine and the back-knee line in degrees. Recommended values are close to 90 degrees, and going over is better than going under.
        /// This is specified with double precision to avoid precision errors when serializing/deserializing to JSON for OMI_seat interchange.
        /// </summary>
        public double SpineAngleDegrees
        {
            get => _spineAngleDegrees;
            set
            {
                _spineAngleDegrees = value;
                OnValidate();
            }
        }

        // These are calculated in `_recalculateHelperVectors` based on the public control points.
        // The default values are provided as sane reference for normal seats, they are not actually used.
        public Vector3 Left { get; private set; } = Vector3.left;
        public Vector3 SpineDir { get; private set; } = Vector3.up;
        public Vector3 SpineNorm { get; private set; } = Vector3.forward;
        public Vector3 UpperLegDir { get; private set; } = Vector3.forward;
        public Vector3 UpperLegPerp { get; private set; } = Vector3.up;
        public Vector3 LowerLegDir { get; private set; } = Vector3.down;
        public Vector3 LowerLegPerp { get; private set; } = Vector3.forward;
        public Quaternion SpineRotation { get; private set; } = Quaternion.identity;
        public float UpperLegLength { get; private set; } = 0.5f;
        public float LowerLegLength { get; private set; } = 0.5f;
        public float LegAngleDegrees { get; private set; } = 90.0f;

        private AsyncOperationHandle<Material> _asyncOperationHighlightMat;
        private GameObject _seatHighlightObject;
        private MeshFilter _seatHighlightMeshFilter;
        private Material _colliderHighlightMat;
        private const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";
        public Action<BasisPlayer> OnLocalPlayerEnterSeat;
        public Action<BasisPlayer> OnLocalPlayerExitSeat;
        private BasisInput _interactingInput = null;
        private bool IsSeatTakenByAnyone = false;
        public bool LocallyInSeat;
        public bool ResetPitchOnEntry = false;
        public bool ExitRequiresAllDevicesPressed;
        public BasisInputKey ExitKey = BasisInputKey.Trigger;
        public BasisBoneTrackedRole[] ExitRoles = null;

        // Internal latch to prevent repeat firing while held (used when ExitOnPressDown == true).
        private bool _exitLatch;
        public void SetPoints(Vector3 back, Vector3 foot, Vector3 knee, double angle = 90.0)
        {
            _back = back;
            _foot = foot;
            _knee = knee;
            _spineAngleDegrees = angle;
            _recalculateHelperVectors();
        }

        private Vector3 _directionTo(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            return dir.normalized;
        }

        private void _recalculateHelperVectors()
        {
            UpperLegDir = _directionTo(Back, Knee);
            LowerLegDir = _directionTo(Knee, Foot);
            Left = Vector3.Cross(LowerLegDir, UpperLegDir).normalized;
            if (Left == Vector3.zero)
            {
                return;
            }
            SpineDir = Quaternion.AngleAxis((float)SpineAngleDegrees, Left) * UpperLegDir;
            SpineNorm = Vector3.Cross(SpineDir, Left);
            UpperLegPerp = Vector3.Cross(Left, UpperLegDir);
            LowerLegPerp = Vector3.Cross(Left, LowerLegDir);
            SpineRotation = Quaternion.LookRotation(SpineNorm, SpineDir);
            UpperLegLength = Vector3.Distance(Back, Knee);
            LowerLegLength = Vector3.Distance(Knee, Foot);
            LegAngleDegrees = Vector3.Angle(UpperLegDir, LowerLegDir);
            if (LegAngleDegrees < 5.0f || LegAngleDegrees > 170.0f)
            {
                BasisDebug.LogWarning("BasisSeat: The angle between the upper and lower leg control lines is very extreme (" + LegAngleDegrees + " degrees). This may cause issues with seating animation.");
            }
        }
        #endregion Seat Internals

        #region Highlight Code
        private Mesh _generateSeatHighlightMesh()
        {
            const float k_lineWidth = 0.1f;
            float seatWidth = Mathf.Min(Vector3.Distance(Back, Knee), Vector3.Distance(Knee, Foot));
            Vector3 rightOuter = Left * (seatWidth * -0.5f);
            Vector3 rightInner = Left * (seatWidth * -(0.5f - k_lineWidth));
            Vector3 leftInner = Left * (seatWidth * (0.5f - k_lineWidth));
            Vector3 leftOuter = Left * (seatWidth * 0.5f);
            Vector3[] vertices =
            {
                Foot + rightOuter, // 0
                Foot + rightInner, // 1
                Foot + leftInner, // 2
                Foot + leftOuter, // 3
                Knee + rightOuter, // 4
                Knee + rightInner, // 5
                Knee + leftInner, // 6
                Knee + leftOuter, // 7
                Back + rightOuter, // 8
                Back + rightInner, // 9
                Back + leftInner, // 10
                Back + leftOuter, // 11
                Back + SpineDir * (seatWidth * 1.0f), // 12
                Back + SpineDir * (seatWidth * (1.0f - k_lineWidth * 1.5f)), // 13
                Back + rightInner + UpperLegDir * (seatWidth * k_lineWidth), // 14
                Back + leftInner + UpperLegDir * (seatWidth * k_lineWidth), // 15
                Knee + rightInner - UpperLegDir * (seatWidth * k_lineWidth), // 16
                Knee + leftInner - UpperLegDir * (seatWidth * k_lineWidth), // 17
            };
            int[] triangles =
            {
                0, 4, 1, 1, 4, 5, // Foot to Knee Right
                2, 6, 3, 3, 6, 7, // Foot to Knee Left
                4, 8, 5, 5, 8, 9, // Knee to Back Right
                6, 10, 7, 7, 10, 11, // Knee to Back Left
                8, 13, 9, 8, 12, 13, // Back to Spine Tip Right
                10, 13, 11, 11, 13, 12, // Back to Spine Tip Left
                9, 10, 14, 10, 15, 14, // Back Upper Leg
                5, 16, 6, 6, 16, 17, // Knee Upper Leg
                // Repeat the same triangles in the reverse winding order for the back faces.
                1, 4, 0, 5, 4, 1, // Foot to Knee Right
                3, 6, 2, 7, 6, 3, // Foot to Knee Left
                5, 8, 4, 9, 8, 5, // Knee to Back Right
                7, 10, 6, 11, 10, 7, // Knee to Back Left
                9, 13, 8, 13, 12, 8, // Back to Spine Tip Right
                11, 13, 10, 11, 12, 13, // Back to Spine Tip Left
                14, 10, 9, 14, 15, 10, // Back Upper Leg
                6, 16, 5, 17, 16, 6, // Knee Upper Leg
            };
            Mesh mesh = new Mesh
            {
                vertices = vertices,
                triangles = triangles
            };
            return mesh;
        }
        public void CalculateSeatPositionRotation(BasisRemotePlayer Player, out Quaternion hipsWorldRot, out Vector3 hipsWorldPos)
        {
            // seat orientation
            Quaternion seatQuat = transform.rotation;
            hipsWorldRot = seatQuat * SpineRotation;

            var refs = Player.RemoteAvatarDriver.References;
            Vector3 scale = Player.AvatarTransform.localScale;
            // grab T-pose dictionary once
            var tpose = refs.Tpose;
            // pull out the leg joints you need
            var leftUpper = tpose[HumanBodyBones.LeftUpperLeg];
            var leftLower = tpose[HumanBodyBones.LeftLowerLeg];
            var leftFoot = tpose[HumanBodyBones.LeftFoot];
            var leftToe = tpose[HumanBodyBones.LeftToes];

            Vector3 LUL_World = Vector3.Scale(scale, leftUpper.position);
            Vector3 LLL_World = Vector3.Scale(scale, leftLower.position);
            Vector3 LF_World = Vector3.Scale(scale, leftFoot.position);
            Vector3 LT_World = Vector3.Scale(scale, leftToe.position);

            // finally call your leg placement helper
            hipsWorldPos = ApplyRemoteLeg(leftLower.rotation, leftUpper.rotation, LF_World, LLL_World, LUL_World, LT_World);
        }
        /// <summary>
        ///
        /// </summary>
        /// <param name="LeftLowerLegControlRotation"></param>
        /// <param name="LeftUpperLegControlRotation"></param>
        /// <param name="LeftFootControl"></param>
        /// <param name="LeftLowerLegControl"></param>
        /// <param name="LeftUpperLegControl"></param>
        /// <param name="LeftToeControl"></param>
        /// <returns></returns>
        public Vector3 ApplyRemoteLeg(Quaternion LeftLowerLegControlRotation, Quaternion LeftUpperLegControlRotation, Vector3 LeftFootControl, Vector3 LeftLowerLegControl, Vector3 LeftUpperLegControl, Vector3 LeftToeControl)
        {

            Vector3 leftLowerLegOffset = LeftFootControl - LeftLowerLegControl;
            Vector3 leftUpperLegOffset = LeftLowerLegControl - LeftUpperLegControl;
            float footThickness = Mathf.Max(LeftFootControl.y, LeftToeControl.y);

            float upperLegLength = leftUpperLegOffset.magnitude;
            float lowerLegLength = leftLowerLegOffset.magnitude;
            float totalLegLength = upperLegLength + lowerLegLength;
            float spineBackThickness = totalLegLength * 0.14f;
            float upperLegBackRadius = totalLegLength * 0.14f;
            float upperLegKneeRadius = totalLegLength * 0.08f;
            float lowerLegKneeRadius = totalLegLength * 0.10f;
            float lowerLegFootRadius = totalLegLength * 0.06f;
            // Calculate the desired upper leg rotations based on the thickness of the legs.
            float upperLegAngleVsSeatRadians = Mathf.Asin((upperLegBackRadius - upperLegKneeRadius) / upperLegLength);
            // Calculate the desired lower leg rotations based on the thickness of the legs.
            float lowerLegAngleVsSeatRadians = Mathf.Asin((lowerLegKneeRadius - lowerLegFootRadius) / lowerLegLength);
            Vector3 targetFoot = Foot + (LowerLegPerp * lowerLegFootRadius) - (LowerLegDir * footThickness);
            Vector3 targetKnee = Knee + (UpperLegPerp * upperLegKneeRadius) + (UpperLegDir * GetAdjustmentScalar(LegAngleDegrees, lowerLegKneeRadius, upperLegKneeRadius, upperLegLength));
            Vector3 targetBack = Back + (UpperLegPerp * upperLegBackRadius) + (UpperLegDir * GetAdjustmentScalar(180.0f - (float)SpineAngleDegrees, spineBackThickness, upperLegBackRadius, upperLegLength));
            float upperLegAngleVsSpineRadians = upperLegAngleVsSeatRadians + Mathf.Deg2Rad * (float)SpineAngleDegrees;
            Vector3 targetUpperLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(upperLegAngleVsSpineRadians), Mathf.Sin(upperLegAngleVsSpineRadians));
            Quaternion desiredLeftUpperLegRot = AlignAroundLocalX(LeftUpperLegControlRotation, leftUpperLegOffset, targetUpperLegDirRelToHips);
            float upperLegHorizontalTravelRatio = Vector3.Dot(UpperLegDir, SpineRotation * desiredLeftUpperLegRot * Vector3.down);
            float availableUpperLegHorizontalTravel = Vector3.Distance(targetKnee - UpperLegPerp * upperLegKneeRadius, targetBack - UpperLegPerp * upperLegBackRadius);
            float characterUpperLegHorizontalTravel = upperLegLength * upperLegHorizontalTravelRatio;
            if (characterUpperLegHorizontalTravel < availableUpperLegHorizontalTravel)
            {
                targetBack += UpperLegDir * (availableUpperLegHorizontalTravel - characterUpperLegHorizontalTravel);
            }
            else
            {
                targetKnee += UpperLegDir * (characterUpperLegHorizontalTravel - availableUpperLegHorizontalTravel);
            }
            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians - Mathf.Deg2Rad * ((float)SpineAngleDegrees + LegAngleDegrees);
            Vector3 targetLowerLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(lowerLegAngleVsSpineRadians), -Mathf.Sin(lowerLegAngleVsSpineRadians));
            Quaternion desiredLeftLowerLegRot = AlignAroundLocalX(LeftLowerLegControlRotation, leftLowerLegOffset, targetLowerLegDirRelToHips);
            float lowerLegVerticalTravelRatio = Vector3.Dot(LowerLegDir, SpineRotation * desiredLeftLowerLegRot * Vector3.down);
            float availableLowerLegVerticalTravel = Vector3.Distance(targetFoot + LowerLegDir * lowerLegFootRadius, targetKnee + LowerLegDir * lowerLegKneeRadius);
            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;
            if (characterLowerLegVerticalTravel >= availableLowerLegVerticalTravel)
            {
                targetKnee += LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);
                if (characterUpperLegHorizontalTravel > availableUpperLegHorizontalTravel)
                {
                    targetKnee = ClosestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                }
                targetBack = ClosestPointOnSphere(targetBack, targetKnee, upperLegLength);
            }
            Vector3 pelvisWorldPos = transform.TransformPoint(targetBack);

            return pelvisWorldPos;
        }
        public static Vector3 ClosestPointOnSphere(Vector3 point, Vector3 sphereCenter, float sphereRadius)
        {
            Vector3 dir = point - sphereCenter;
            dir.Normalize();
            return sphereCenter + dir * sphereRadius;
        }
        public static float GetAdjustmentScalar(float angle, float alignedOffset, float perpOffset, float limit)
        {
            if (angle > 90.001f)
            {
                return Mathf.Min(alignedOffset / Mathf.Sin(angle * Mathf.Deg2Rad) - perpOffset / Mathf.Tan(angle * Mathf.Deg2Rad), limit);
            }
            return Mathf.Min(alignedOffset * Mathf.Sin(angle * Mathf.Deg2Rad), limit);
        }

        /// <summary>
        /// Aligns the local align direction (usually +Y) of the provided quaternion to point as closely as possible
        /// to the provided target direction, by rotating around the quaternion's local X axis.
        /// The returned rotation is parent-relative, it should be applied on the left side of the original quaternion.
        /// </summary>
        public static Quaternion AlignAroundLocalX(Quaternion quat, Vector3 localAlign, Vector3 targetNormalized)
        {
            Vector3 x = quat * Vector3.right;
            // Project target onto the local YZ plane with the local +X as the normal vector.
            Vector3 targetProj = targetNormalized - Vector3.Dot(targetNormalized, x) * x;
            if (targetProj.sqrMagnitude < 1e-6f)
            {
                BasisDebug.LogWarning("BasisLocalSeatDriver.AlignYAroundLocalX: Failed to align legs to the seat, the local X axis is not sideways enough.");
                return quat;
            }
            targetProj.Normalize();
            // Project dest onto the same plane.
            Vector3 localAlignProj = localAlign - Vector3.Dot(localAlign, x) * x;
            localAlignProj.Normalize();
            // Find signed angle between current Y and targetProj in the local YZ plane around the local X axis.
            float angle = Mathf.Rad2Deg * Mathf.Atan2(
                Vector3.Dot(Vector3.Cross(localAlignProj, targetProj), x),
                Vector3.Dot(localAlignProj, targetProj)
            );
            // Calculate rotation within a sandwich to allow it to be applied in parent space.
            return quat * Quaternion.AngleAxis(angle, Vector3.right) * Quaternion.Inverse(quat);
        }
        public void HighlightSeat(bool hover)
        {
            if (_seatHighlightObject == null)
            {
                return;
            }
            switch (highlightMode)
            {
                case ShowSeatHighlightMode.Never:
                    hover = false;
                    break;
                case ShowSeatHighlightMode.Always:
                    hover = true;
                    break;
                case ShowSeatHighlightMode.OnHoverOrInEditor:
#if UNITY_EDITOR
                    hover = true;
#endif
                    break;
            }
            _seatHighlightObject.SetActive(hover);
        }
        #endregion Highlight Code

        #region Unity Lifecycle Hooks
        private void OnValidate()
        {
            // Triggered whenever inspector values change.
            _recalculateHelperVectors();
            if (_seatHighlightMeshFilter != null)
            {
                if (_seatHighlightMeshFilter.mesh != null)
                {
                    DestroyImmediate(_seatHighlightMeshFilter.mesh);
                }
                _seatHighlightMeshFilter.mesh = _generateSeatHighlightMesh();
            }
        }

        public void Start()
        {
            // Load the highlight material, the same one as `BasisPickupInteractable` (because it looks cool).
            AsyncOperationHandle<Material> op = Addressables.LoadAssetAsync<Material>(k_LoadMaterialAddress);
            _colliderHighlightMat = op.WaitForCompletion();
            _asyncOperationHighlightMat = op;
            // Create a mesh gizmo for the seat highlight.
            _seatHighlightObject = new GameObject("SeatHighlight");
            _seatHighlightObject.transform.SetParent(transform, false);
            _seatHighlightMeshFilter = _seatHighlightObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = _seatHighlightObject.AddComponent<MeshRenderer>();
            meshRenderer.material = _colliderHighlightMat;
            OnValidate(); // Will generate the mesh and assign it.
            HighlightSeat(false);
        }

        public override void OnDestroy()
        {
            if (LocallyInSeat)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    BasisLocalPlayer.Instance?.LocalSeatDriver.Stand();
                }
                SetSeatOccupied(false);
                LocallyInSeat = false;
            }
            if (_seatHighlightMeshFilter != null)
            {
                if (_seatHighlightMeshFilter.mesh != null)
                {
                    DestroyImmediate(_seatHighlightMeshFilter.mesh);
                }
            }
            if (_asyncOperationHighlightMat.IsValid())
            {
                _asyncOperationHighlightMat.Release();
            }
            base.OnDestroy();
        }
        #endregion Unity Lifecycle Hooks

        #region Basis Integration
        public override bool CanHover(BasisInput input)
        {
            // Can only hover when not already hovering or interacting.
            return LocallyInSeat || CheckUsabilityWithState(input, BasisInteractInputState.Ignored) && IsSeatTakenByAnyone == false;
        }
        public void SetSeatOccupied(bool seatOccupiedByAnyone)
        {
            IsSeatTakenByAnyone = seatOccupiedByAnyone;
        }
        public override bool CanInteract(BasisInput input)
        {
            return LocallyInSeat || CheckUsabilityWithState(input, BasisInteractInputState.Hovering) && IsSeatTakenByAnyone == false;
        }

        public override bool IsHoveredBy(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
        }

        public override bool IsInteractingWith(BasisInput input)
        {
            return _interactingInput == input;
        }


        /// <summary>
        /// Called when hovering begins for an input. Promotes the input to the <c>Hovering</c> state,
        /// shows highlight, and invokes <see cref="BasisInteractableObject.OnHoverStartEvent"/>.
        /// </summary>
        /// <param name="input">The input source beginning hover.</param>
        public override void OnHoverStart(BasisInput input)
        {
            var found = Inputs.FindExcludeExtras(input);
            if (found == null)
            {
                return;
            }
            //  if (found.Value.GetState() != BasisInteractInputState.Ignored)
            // {
            // BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " input state is not ignored OnHoverStart, this shouldn't happen");
            // }

            var added = Inputs.ChangeStateByRole(found.Value.Role, BasisInteractInputState.Hovering);
            if (!added)
            {
                BasisDebug.LogWarning(nameof(BasisPickupInteractable) + " did not find role for input on hover");
            }

            OnHoverStartEvent?.Invoke(input);
            HighlightSeat(true);
        }

        /// <summary>
        /// Called when hover ends for an input. Optionally clears state if interaction won't begin,
        /// hides highlight, and invokes <see cref="BasisInteractableObject.OnHoverEndEvent"/>.
        /// </summary>
        /// <param name="input">The input source ending hover.</param>
        /// <param name="willInteract">Whether interaction is about to begin.</param>
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
                HighlightSeat(false);
            }
        }

        public override void OnInteractStart(BasisInput input)
        {
            if (InteractionTimerValidation() == false)
            {
                return;
            }
            // Clear any existing interacting inputs first
            Inputs.ForEachWithState(OnInteractEnd, BasisInteractInputState.Interacting);

            if (!input.TryGetRole(out BasisBoneTrackedRole role) || !Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                return;
            }

            if (wrapper.GetState() != BasisInteractInputState.Hovering)
            {
                return;
            }
            Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Interacting);
            _interactingInput = input;
            if (LocallyInSeat)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    BasisLocalPlayer.Instance?.LocalSeatDriver.Stand();
                }
                SetSeatOccupied(false);
                LocallyInSeat = false;
            }
            else
            {
                BasisLocalPlayer.Instance.LocalSeatDriver.Sit(this);
                OnEnterSeat(BasisLocalPlayer.Instance);
            }
            base.OnInteractStart(input);
        }
        public void OnDisable()
        {
            if (LocallyInSeat)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    BasisLocalPlayer.Instance?.LocalSeatDriver.Stand();
                }
                SetSeatOccupied(false);
                LocallyInSeat = false;
            }
        }

        public override void OnInteractEnd(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
            {
                if (wrapper.GetState() == BasisInteractInputState.Interacting)
                {
                    Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);
                    _interactingInput = null;
                }
            }
        }

        public void OnEnterSeat(BasisPlayer player)
        {
            SetSeatOccupied(true);
            LocallyInSeat = true;
            OnLocalPlayerEnterSeat?.Invoke(player);
        }

        /// <summary>
        /// this one actually does the callback.
        /// </summary>
        public void OnExitSeat(BasisPlayer player)
        {
            base.OnInteractEnd(null);
            SetSeatOccupied(false);
            LocallyInSeat = false;
            OnLocalPlayerExitSeat?.Invoke(player);
        }
        public void LateUpdate()
        {
            if (LocallyInSeat)
            {
                if (ExitRoles == null || ExitRoles.Length == 0)
                {
                    return;
                }

                bool anyPressed = false;
                bool allPressed = true;

                for (int i = 0; i < ExitRoles.Length; i++)
                {
                    var role = ExitRoles[i];

                    // If a role isn't available, treat it as NOT pressed (safer).
                    if (!Inputs.TryGetByRole(role, out BasisInputWrapper wrapper) || wrapper.Source == null)
                    {
                        allPressed = false;
                        continue;
                    }

                    bool pressed = HasState(wrapper.Source.CurrentInputState, ExitKey);

                    anyPressed |= pressed;
                    allPressed &= pressed;

                    // Micro-early-outs
                    if (!ExitRequiresAllDevicesPressed && anyPressed)
                    {
                        break;
                    }

                    if (ExitRequiresAllDevicesPressed && !allPressed)
                    {
                        // can't break here if you want to keep checking for side effects; we don't, so break.
                        break;
                    }
                }

                bool exitConditionMet = ExitRequiresAllDevicesPressed ? allPressed : anyPressed;

                if (exitConditionMet && !_exitLatch)
                {
                    BasisDebug.Log($"Exit Condition Met!");
                    _exitLatch = true;
                    RequestExit();
                }
                else if (!exitConditionMet)
                {
                    _exitLatch = false;
                }
            }
        }
        private void RequestExit()
        {
            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance?.LocalSeatDriver.Stand();
            }
            SetSeatOccupied(false);
            LocallyInSeat = false;
        }
        #endregion Basis Integration
        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.DrawSphere(_back, 0.04f);
            Gizmos.DrawSphere(_knee, 0.04f);
            Gizmos.DrawSphere(_foot, 0.04f);

            Gizmos.DrawLine(_back, _knee);
            Gizmos.DrawLine(_knee, _foot);

            // show local forward
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.3f);

            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
