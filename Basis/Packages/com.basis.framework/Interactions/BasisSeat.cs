using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
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
        private AsyncOperationHandle<Material> _asyncOperationHighlightMat;
        private GameObject _seatHighlightObject;
        private MeshFilter _seatHighlightMeshFilter;
        private Material _colliderHighlightMat;
        private const string k_LoadMaterialAddress = "Interactable/InteractHighlightMat.mat";

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
        private BasisInput _interactingInput = null;

        public override bool CanHover(BasisInput input)
        {
            // Can only hover when not already hovering or interacting.
            return _checkUsabilityWithState(input, BasisInteractInputState.Ignored);
        }

        public override bool CanInteract(BasisInput input)
        {
            return true;
        }

        public override bool IsHoveredBy(BasisInput input)
        {
            return true;
        }

        public override bool IsInteractingWith(BasisInput input)
        {
            return _interactingInput == input;
        }

        public override void InputUpdate()
        {
            // BasisInteractableObject requires overriding this but I don't think we need it?
        }

        /// <summary>
        /// Called when hovering begins for an input. Promotes the input to the <c>Hovering</c> state,
        /// shows highlight, and invokes <see cref="BasisInteractableObject.OnHoverStartEvent"/>.
        /// </summary>
        /// <param name="input">The input source beginning hover.</param>
        public override void OnHoverStart(BasisInput input)
        {
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
            OnHoverEndEvent?.Invoke(input, willInteract);
            HighlightSeat(false);
        }

        public override void OnInteractStart(BasisInput input)
        {
            _interactingInput = input;
            Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance.LocalSeatDriver.Sit(this);
        }

        public override void OnInteractEnd(BasisInput input)
        {
            _interactingInput = null;
        }
        #endregion Basis Integration
    }
}
