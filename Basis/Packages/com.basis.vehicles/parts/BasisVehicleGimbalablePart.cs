using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    public abstract class BasisVehicleGimbalablePart : BasisVehiclePart
    {
        [Header("Gimbal")]
        /// <summary>
        /// The maximum angle the part can gimbal or rotate in degrees. This can technically be any number,
        /// but larger values result in strange behavior, even close to 90 degrees and especially beyond.
        /// Note: The initial gimbal must be set before adding the object to the hierarchy.
        /// </summary>
        [Range(0.0f, 90.0f)]
        [Tooltip("Recommended values are close to 0.0, usually not more than 30.0.")]
        public float MaxGimbalDegrees = 0.0f;

        /// <summary>
        /// Optionally, you may also want to allow the gimbal to be adjusted based on linear input.
        /// For example, if the user wants to go forward, and the thruster points downward,
        /// we can gimbal the thruster slightly backward to help thrust forward.
        /// The default is 0.0 for thrusters and 0.5 for hover thrusters.
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Recommended values are between 0.0 and 0.5.")]
        public float LinearGimbalAdjustRatio = 0.0f;

        /// <summary>
        /// The speed at which the gimbal angle changes, in degrees per second. If negative, the angle changes instantly.
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float GimbalDegreesPerSecond = 60.0f;

        /// <summary>
        /// The ratio of the maximum gimbal angles the part is rotated to.
        /// The vector's length may not be longer than 1.0.
        /// Note: The initial gimbal must be set before adding the object to the hierarchy.
        /// </summary>
        [Tooltip("Length must not exceed 1.0.")]
        public Vector2 TargetGimbalRatio = Vector2.zero;

        /// <summary>
        /// The current gimbal angles in radians, tending towards TargetGimbalRatio * MaxGimbalDegrees * Mathf.Deg2Rad.
        /// If GimbalDegreesPerSecond is negative, this will equal the target value.
        /// </summary>
        private Vector2 _currentGimbalRadians = Vector2.zero;

        protected BasisCalibratedCoords _parentTransformToBody = BasisCalibratedCoords.Identity;
        protected Quaternion _restQuaternion = Quaternion.identity;
        protected Quaternion _restQuaternionToBody = Quaternion.identity;
        protected Quaternion _bodyToRestQuaternion = Quaternion.identity;
        protected bool _negateGimbal = true;

        protected override void OnEnable()
        {
            base.OnEnable();
            RecalculateTransforms();
            //MakeDebugMesh();
        }

        protected virtual void FixedUpdate()
        {
            // Move the current gimbal radians towards the target value.
            Vector2 targetGimbalRadians = Vector2.ClampMagnitude(TargetGimbalRatio, 1.0f) * (Mathf.Deg2Rad * MaxGimbalDegrees);
            if (GimbalDegreesPerSecond < 0.0f)
            {
                _currentGimbalRadians = targetGimbalRadians;
            }
            else
            {
                float gimbalChange = GimbalDegreesPerSecond * Mathf.Deg2Rad * Time.fixedDeltaTime;
                _currentGimbalRadians = Vector2.MoveTowards(_currentGimbalRadians, targetGimbalRadians, gimbalChange);
            }
            transform.localRotation = _restQuaternion * GetGimbalRotationQuaternion();
        }

        private void RecalculateTransforms()
        {
            if (_parentBody == null)
            {
                return;
            }
            // Get the transform from the parent to the body.
            _parentTransformToBody = BasisCalibratedCoords.Identity;
            Transform t = transform.parent;
            while (t != null && t.gameObject != _parentBody.gameObject)
            {
                _parentTransformToBody.rotation = t.localRotation * _parentTransformToBody.rotation;
                _parentTransformToBody.position = t.localPosition + (t.localRotation * _parentTransformToBody.position);
                t = t.parent;
            }
            // Get the rotation of the rest orientation of the part's gimbal.
            Quaternion gimbalInv = Quaternion.Inverse(GetGimbalRotationQuaternion());
            _restQuaternion = transform.localRotation * gimbalInv;
            // Use both of those to determine the rest quaternion to body and its inverse.
            _restQuaternionToBody = _parentTransformToBody.rotation * _restQuaternion;
            _bodyToRestQuaternion = Quaternion.Inverse(_restQuaternionToBody);
            // Where is this part relative to the center of mass? We may need to negate the gimbal.
            Quaternion restRot = _parentTransformToBody.rotation * _restQuaternion;
            Vector3 restPos = _parentTransformToBody.position + (_parentTransformToBody.rotation * transform.localPosition);
            Vector3 offset = restPos - _parentBody.centerOfMass;
            _negateGimbal = Vector3.Dot(offset, restRot * Vector3.forward) < 0.0f;
        }

        /// <summary>
        /// Derived classes can call this to set the gimbal before using the linear input for linear force.
        /// </summary>
        protected void SetGimbalFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            if (MaxGimbalDegrees == 0.0f)
            {
                TargetGimbalRatio = Vector2.zero;
                return;
            }
            // Set the gimbal based on the local angular input.
            Vector3 localAngularInput = _bodyToRestQuaternion * angularInput;
            TargetGimbalRatio = Vector2.ClampMagnitude(new Vector2(-localAngularInput.x, -localAngularInput.y), 1.0f);
            // Adjust the gimbal based on linear input (optional but significantly improves handling).
            if (linearInput == Vector3.zero || LinearGimbalAdjustRatio == 0.0f)
            {
                return;
            }
            Quaternion currentRot = _restQuaternionToBody * GetGimbalRotationQuaternion();
            Vector3 localLinearInput = Quaternion.Inverse(currentRot) * linearInput;
            Vector2 linearGimbalAdjust = Vector2.ClampMagnitude(new Vector2(-localLinearInput.y, localLinearInput.x), 1.0f) * LinearGimbalAdjustRatio;
            TargetGimbalRatio = Vector2.ClampMagnitude(TargetGimbalRatio + linearGimbalAdjust, 1.0f);
        }

        protected Quaternion GetGimbalRotationQuaternion()
        {
            if (_currentGimbalRadians == Vector2.zero)
            {
                return Quaternion.identity;
            }
            float angleMag = _currentGimbalRadians.magnitude;
            float sinNorm = Mathf.Sin(angleMag * 0.5f) / angleMag;
            float cosHalf = Mathf.Cos(angleMag * 0.5f);
            return new Quaternion(
                _currentGimbalRadians.x * sinNorm,
                _currentGimbalRadians.y * sinNorm,
                0.0f,
                cosHalf
            );
        }

        /// <summary>
        /// Sqrt(1/2) aka sqrt(0.5) aka 1/sqrt(2) aka sqrt(2)/2 aka sin(45 degrees) aka cos(45 degrees).
        /// </summary>
        private const float SQRT12 = 0.707106781186547524400844362104849f;
        private void MakeDebugMesh()
        {
            // Make a debug mesh to visualize the gimbal.
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(capsule.GetComponent<CapsuleCollider>());
            capsule.transform.SetParent(transform, false);
            capsule.transform.SetLocalPositionAndRotation(new Vector3(0.0f, 0.0f, -1.0f), new Quaternion(SQRT12, 0.0f, 0.0f, SQRT12));
            capsule.transform.localScale = new Vector3(0.1f, 1.0f, 0.1f);
        }
    }
}
