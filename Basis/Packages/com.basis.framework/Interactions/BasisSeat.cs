using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking.NetworkedAvatar;
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

        #region Occupant Rotation
        [Header("Occupant Rotation")]
        [Tooltip("Total sweep the occupant may turn themselves with the right stick in VR or mouse look on desktop, centred on the seat's forward. 0 holds them forward, 360 is a free spin, 90 gives 45 degrees either way.")]
        [SerializeField, Range(0f, 360f)] private float _occupantRotationRangeDegrees = 0f;

        [Tooltip("Step the occupant's turn is quantised to. 0 turns smoothly.")]
        [SerializeField, Range(0f, 180f)] private float _occupantRotationSnapDegrees = 0f;

        private float _occupantYawDegrees;
        private float _occupantYawRaw;

        public float OccupantRotationRangeDegrees
        {
            get => _occupantRotationRangeDegrees;
            set
            {
                _occupantRotationRangeDegrees = Mathf.Clamp(value, 0f, 360f);
                SetOccupantYaw(_occupantYawRaw);
            }
        }

        public float OccupantRotationSnapDegrees
        {
            get => _occupantRotationSnapDegrees;
            set
            {
                _occupantRotationSnapDegrees = Mathf.Clamp(value, 0f, 180f);
                SetOccupantYaw(_occupantYawRaw);
            }
        }

        public BasisSeatRotationLimits OccupantRotationLimits =>
            new BasisSeatRotationLimits(_occupantRotationRangeDegrees, _occupantRotationSnapDegrees);

        public float OccupantYawDegrees => _occupantYawDegrees;

        public Action<float> OnOccupantYawChanged;

        public bool TurnOccupant(float deltaDegrees)
        {
            float resolved = BasisSeatFit.AddOccupantYaw(_occupantYawRaw, deltaDegrees, OccupantRotationLimits, out _occupantYawRaw);
            return CommitOccupantYaw(resolved);
        }

        public bool SetOccupantYaw(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return false;
            }
            BasisSeatRotationLimits limits = OccupantRotationLimits;
            // Limited seats clamp the request as-is (a +1000 twist stops at +limit); wrapping
            // first would flip which side of the range a large request lands on. Matches
            // BasisSeatFit.AddOccupantYaw's raw handling.
            _occupantYawRaw = limits.AllowsRotation
                ? (limits.IsFullCircle
                    ? BasisSeatFit.WrapDegrees(degrees)
                    : Mathf.Clamp(degrees, -limits.HalfRangeDegrees, limits.HalfRangeDegrees))
                : 0f;
            return CommitOccupantYaw(BasisSeatFit.ResolveOccupantYaw(_occupantYawRaw, limits));
        }

        public float TakeOccupantLookYaw(float lookDegrees)
        {
            float before = _occupantYawDegrees;
            SetOccupantYaw(before + lookDegrees);
            return Mathf.DeltaAngle(before, _occupantYawDegrees);
        }

        public void ApplyNetworkedOccupantYaw(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return;
            }
            _occupantYawRaw = degrees;
            _occupantYawDegrees = degrees;
        }

        public void ResetOccupantYaw()
        {
            _occupantYawRaw = 0f;
            CommitOccupantYaw(0f);
        }

        private bool CommitOccupantYaw(float resolved)
        {
            if (Mathf.Approximately(resolved, _occupantYawDegrees))
            {
                return false;
            }
            _occupantYawDegrees = resolved;
            OnOccupantYawChanged?.Invoke(resolved);
            return true;
        }
        #endregion Occupant Rotation

        #region Occupant Body
        [Header("Occupant Body")]
        [Tooltip("Locked holds the hips at the seat's back point. Unless Tracked releases them when the player has a hips tracker. Free always lets them follow the player's head and trackers, while the seat still carries them.")]
        [SerializeField] private BasisSeatLockMode _hipsLock = BasisSeatLockMode.Locked;

        [Tooltip("Locked poses the left leg into the seat. Unless Tracked releases it when the player has a tracker on that leg. Free always leaves it to trackers and foot placement.")]
        [SerializeField] private BasisSeatLockMode _leftFootLock = BasisSeatLockMode.Locked;

        [Tooltip("Locked poses the right leg into the seat. Unless Tracked releases it when the player has a tracker on that leg. Free always leaves it to trackers and foot placement.")]
        [SerializeField] private BasisSeatLockMode _rightFootLock = BasisSeatLockMode.Locked;

        [Tooltip("How far a script may move the occupant from the seat's fit point with SetOccupantOffset, in the seat's local units. 0 holds them on the fit point.")]
        [SerializeField, Min(0f)] private float _occupantOffsetLimit = 0f;

        [Tooltip("How locked legs are arranged. Kneeling raises the hips so the knees clear the seat.")]
        [SerializeField] private BasisSeatPose _pose = BasisSeatPose.Seated;

        [Header("Occupant Limb Targets")]
        [Tooltip("Where the left hand goes, such as a steering wheel grip. With no rotation the hand is in its T-pose, palm down with the fingers pointing out to the side. Leave empty to not control the hand.")]
        [SerializeField] private Transform _leftHandTarget;

        [Tooltip("Where the right hand goes, such as a steering wheel grip. With no rotation the hand is in its T-pose, palm down with the fingers pointing out to the side. Leave empty to not control the hand.")]
        [SerializeField] private Transform _rightHandTarget;

        [Tooltip("Locked holds the left hand on its target, even over a VR controller. Unless Tracked lets a VR controller or hand tracker keep it. Free ignores the target.")]
        [SerializeField] private BasisSeatLockMode _leftHandLock = BasisSeatLockMode.UnlessTracked;

        [Tooltip("Locked holds the right hand on its target, even over a VR controller. Unless Tracked lets a VR controller or hand tracker keep it. Free ignores the target.")]
        [SerializeField] private BasisSeatLockMode _rightHandLock = BasisSeatLockMode.UnlessTracked;

        [Tooltip("Where the sole of the left foot rests, such as a pedal. Its up axis points out of the sole and its forward axis points along the toes. Used while Left Foot Lock holds the leg. Leave empty to use the seat pose.")]
        [SerializeField] private Transform _leftFootTarget;

        [Tooltip("Where the sole of the right foot rests, such as a pedal. Its up axis points out of the sole and its forward axis points along the toes. Used while Right Foot Lock holds the leg. Leave empty to use the seat pose.")]
        [SerializeField] private Transform _rightFootTarget;

        private Vector3 _occupantOffset;

        public BasisSeatPose Pose
        {
            get => _pose;
            set
            {
                if (_pose != value)
                {
                    _pose = value;
                    OnOccupantBodyChanged?.Invoke();
                }
            }
        }

        public void ApplyNetworkedPose(BasisSeatPose pose)
        {
            _pose = Enum.IsDefined(typeof(BasisSeatPose), pose) ? pose : BasisSeatPose.Seated;
        }

        public Transform LeftHandTarget
        {
            get => _leftHandTarget;
            set => _leftHandTarget = value;
        }

        public Transform RightHandTarget
        {
            get => _rightHandTarget;
            set => _rightHandTarget = value;
        }

        public BasisSeatLockMode LeftHandLock
        {
            get => _leftHandLock;
            set => _leftHandLock = value;
        }

        public BasisSeatLockMode RightHandLock
        {
            get => _rightHandLock;
            set => _rightHandLock = value;
        }

        public Transform LeftFootTarget
        {
            get => _leftFootTarget;
            set => _leftFootTarget = value;
        }

        public Transform RightFootTarget
        {
            get => _rightFootTarget;
            set => _rightFootTarget = value;
        }

        public void SetHandTargets(Transform left, Transform right)
        {
            _leftHandTarget = left;
            _rightHandTarget = right;
        }

        public void SetFootTargets(Transform left, Transform right)
        {
            _leftFootTarget = left;
            _rightFootTarget = right;
        }

        public void ClearLimbTargets()
        {
            SetHandTargets(null, null);
            SetFootTargets(null, null);
        }

        public BasisSeatLockMode HipsLock
        {
            get => _hipsLock;
            set
            {
                if (_hipsLock != value)
                {
                    _hipsLock = value;
                    OnOccupantBodyChanged?.Invoke();
                }
            }
        }

        public BasisSeatLockMode LeftFootLock
        {
            get => _leftFootLock;
            set => _leftFootLock = value;
        }

        public BasisSeatLockMode RightFootLock
        {
            get => _rightFootLock;
            set => _rightFootLock = value;
        }

        public float OccupantOffsetLimit
        {
            get => _occupantOffsetLimit;
            set
            {
                _occupantOffsetLimit = Mathf.Max(0f, value);
                SetOccupantOffset(_occupantOffset);
            }
        }

        public Vector3 OccupantOffset => _occupantOffset;

        public float NetworkedSoleHeight { get; set; }

        public Action OnOccupantBodyChanged;

        public bool SetOccupantOffset(Vector3 seatLocalOffset)
        {
            if (float.IsNaN(seatLocalOffset.x) || float.IsNaN(seatLocalOffset.y) || float.IsNaN(seatLocalOffset.z)
                || float.IsInfinity(seatLocalOffset.x) || float.IsInfinity(seatLocalOffset.y) || float.IsInfinity(seatLocalOffset.z))
            {
                return false;
            }
            Vector3 resolved = Vector3.ClampMagnitude(seatLocalOffset, _occupantOffsetLimit);
            if (resolved == _occupantOffset)
            {
                return false;
            }
            _occupantOffset = resolved;
            OnOccupantBodyChanged?.Invoke();
            return true;
        }

        public void ResetOccupantOffset()
        {
            SetOccupantOffset(Vector3.zero);
        }

        public void ApplyNetworkedOccupantOffset(Vector3 seatLocalOffset)
        {
            if (float.IsNaN(seatLocalOffset.x) || float.IsNaN(seatLocalOffset.y) || float.IsNaN(seatLocalOffset.z)
                || float.IsInfinity(seatLocalOffset.x) || float.IsInfinity(seatLocalOffset.y) || float.IsInfinity(seatLocalOffset.z))
            {
                return;
            }
            _occupantOffset = seatLocalOffset;
        }
        #endregion Occupant Body

        #region Occupancy
        private IBasisPlayer _occupant;

        public bool HasOccupant => IsSeatTakenByAnyone;

        public bool IsAvailable => IsSeatTakenByAnyone == false && BasisSettingsDefaults.DisableSeats.RawValue == false;

        public bool IsLocalPlayerSeated => LocallyInSeat;

        public bool TryGetOccupant(out IBasisPlayer player)
        {
            player = IsSeatTakenByAnyone ? _occupant : null;
            return player != null;
        }

        public void SetOccupantRecord(IBasisPlayer player)
        {
            _occupant = player;
        }

        public bool TrySeatLocalPlayer()
        {
            if (LocallyInSeat || IsAvailable == false)
            {
                return false;
            }

            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (local == null || local.LocalSeatDriver == null)
            {
                return false;
            }

            local.LocalSeatDriver.Sit(this);
            OnEnterSeat(local);
            base.OnInteractStart(null);
            return true;
        }

        public bool EjectLocalPlayer()
        {
            if (LocallyInSeat == false)
            {
                return false;
            }
            RequestExit();
            return true;
        }
        #endregion Occupancy

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
        private Mesh _seatHighlightMesh;
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
            if (!BasisSeatFit.BuildFrame(Back, Foot, Knee, (float)SpineAngleDegrees, out BasisSeatFitFrame frame))
            {
                return;
            }
            SpineDir = Quaternion.AngleAxis((float)SpineAngleDegrees, Left) * UpperLegDir;
            SpineNorm = Vector3.Cross(SpineDir, Left);
            UpperLegPerp = frame.UpperLegPerp;
            LowerLegPerp = frame.LowerLegPerp;
            SpineRotation = frame.SpineRotation;
            UpperLegLength = Vector3.Distance(Back, Knee);
            LowerLegLength = Vector3.Distance(Knee, Foot);
            LegAngleDegrees = frame.LegAngleDegrees;
            if (LegAngleDegrees < 5.0f || LegAngleDegrees > 170.0f)
            {
                BasisDebug.LogWarning("BasisSeat: The angle between the upper and lower leg control lines is very extreme (" + LegAngleDegrees + " degrees). This may cause issues with seating animation.");
            }
        }
        #endregion Seat Internals

        #region Highlight Code
        private void _writeSeatHighlightMesh(Mesh mesh)
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
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
        }
        public BasisSeatFitFrame GetFitFrame()
        {
            return new BasisSeatFitFrame
            {
                Back = Back,
                Knee = Knee,
                Foot = Foot,
                UpperLegDir = UpperLegDir,
                UpperLegPerp = UpperLegPerp,
                LowerLegDir = LowerLegDir,
                LowerLegPerp = LowerLegPerp,
                SpineRotation = SpineRotation,
                SpineAngleDegrees = (float)SpineAngleDegrees,
                LegAngleDegrees = LegAngleDegrees,
            };
        }

        public BasisSeatFitResult SolveFit(in BasisSeatFitLegs legs)
        {
            Vector3 scale = transform.lossyScale;
            float seatScale = Mathf.Max((Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f, 1e-4f);
            BasisSeatFitLegs seatLegs = legs.InSeatUnits(seatScale);
            BasisSeatFitFrame frame = GetFitFrame();
            BasisSeatFitResult fit = BasisSeatFit.Solve(frame, seatLegs);
            fit.Back += frame.SpineRotation * Vector3.up * BasisSeatFit.PosePelvisLift(_pose, seatLegs.UpperLegLength);
            return fit;
        }

        public bool TryCalculateOccupantPose(BasisTransformMapping mapping, Vector3 rootScale, float occupantYawDegrees, out Vector3 hipsWorldPos, out Quaternion rootWorldRot, out Quaternion hipsRestFromRoot)
        {
            hipsWorldPos = default;
            rootWorldRot = Quaternion.identity;
            hipsRestFromRoot = Quaternion.identity;
            if (mapping == null || !BasisSeatFitLegs.TryFromMapping(mapping.TposeFromRoot, rootScale, NetworkedSoleHeight, out BasisSeatFitLegs legs)
                || !mapping.TposeFromRoot.TryGetValue(HumanBodyBones.Hips, out BasisCalibratedCoords hipsRest))
            {
                return false;
            }
            hipsRestFromRoot = hipsRest.rotation;
            BasisSeatFitResult fit = SolveFit(legs);
            BasisSeatFit.ComposeHipsWorld(transform.localToWorldMatrix, transform.rotation, SpineRotation, fit.Back + _occupantOffset,
                occupantYawDegrees, out hipsWorldPos, out Quaternion seated, out _);
            rootWorldRot = seated * Quaternion.Inverse(BasisGenericBoneRotationUtils.GetCharacterBasis(mapping));
            return true;
        }

        /// <summary>
        /// </summary>
        public void CalculateSeatPositionRotation(in BasisSeatFitLegs legs, out Quaternion hipsWorldRot, out Vector3 hipsWorldPos)
        {
            BasisSeatFitResult fit = SolveFit(legs);
            BasisSeatFit.ComposeHipsWorld(transform.localToWorldMatrix, transform.rotation, SpineRotation, fit.Back + _occupantOffset,
                _occupantYawDegrees, out hipsWorldPos, out hipsWorldRot, out _);
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
        public override void Awake()
        {
            base.Awake();
            _recalculateHelperVectors();
        }

        private void OnValidate()
        {
            // Triggered whenever inspector values change.
            _recalculateHelperVectors();
            if (_seatHighlightMeshFilter != null)
            {
                if (_seatHighlightMesh == null)
                {
                    _seatHighlightMesh = new Mesh();
                    _seatHighlightMeshFilter.sharedMesh = _seatHighlightMesh;
                }
                _writeSeatHighlightMesh(_seatHighlightMesh);
            }
        }

        private void DestroyHighlightMesh()
        {
            if (_seatHighlightMesh == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Destroy(_seatHighlightMesh);
            }
            else
            {
                DestroyImmediate(_seatHighlightMesh);
            }
            _seatHighlightMesh = null;
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
            DestroyHighlightMesh();
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
            if (!LocallyInSeat && BasisSettingsDefaults.DisableSeats.RawValue) return false;
            // Can only hover when not already hovering or interacting.
            return LocallyInSeat || CheckUsabilityWithState(input, BasisInteractInputState.Ignored) && IsSeatTakenByAnyone == false;
        }
        public void SetSeatOccupied(bool seatOccupiedByAnyone)
        {
            IsSeatTakenByAnyone = seatOccupiedByAnyone;
        }
        public override bool CanInteract(BasisInput input)
        {
            if (!LocallyInSeat && BasisSettingsDefaults.DisableSeats.RawValue) return false;
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

            DrawLimbTargetGizmo(_leftHandTarget);
            DrawLimbTargetGizmo(_rightHandTarget);
            DrawLimbTargetGizmo(_leftFootTarget);
            DrawLimbTargetGizmo(_rightFootTarget);
        }

        private void DrawLimbTargetGizmo(Transform target)
        {
            if (target == null)
            {
                return;
            }
            Gizmos.DrawWireSphere(target.position, 0.04f);
            Gizmos.DrawLine(transform.position, target.position);
        }
    }
}
