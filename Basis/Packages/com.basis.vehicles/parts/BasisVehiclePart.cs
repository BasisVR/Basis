using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    public abstract class BasisVehiclePart : MonoBehaviour
    {
        /// <summary>
        /// The Rigidbody of the parent vehicle body this part is attached to.
        /// </summary>
        protected Rigidbody _parentBody = null;

        /// <summary>
        /// The BasisVehicleBody of the parent vehicle this part is attached to,
        /// if the Rigidbody is on the same GameObject as a BasisVehicleBody component.
        /// </summary>
        protected Main.BasisVehicleBody _parentVehicleBody = null;

        /// <summary>
        /// The particle system used for visual effects, if any.
        /// </summary>
        protected ParticleSystem _particles = null;
        /// <summary>
        /// The emission module of the particle system, if any.
        /// </summary>
        protected ParticleSystem.EmissionModule _particleEmission;

        /// <summary>
        /// If false, the part will not rotate or apply active forces (thrust, etc), but will still apply passive forces (lift, drag, etc).
        /// If you want to completely disable the part, disable the component instead using <see cref="UnityEngine.Behaviour.enabled"/>.
        /// </summary>
        [Tooltip("Whether to apply active forces. Passive forces still apply.")]
        public bool Active = true;

        /// <summary>
        /// Sets the wheel's steering and thrust based on vehicle input.
        /// All non-abstract classes extending BasisVehiclePart must implement handling of vehicle input.
        /// </summary>
        /// <param name="angularInput">The vehicle's angular input on a range of -1.0 to 1.0.</param>
        /// <param name="linearInput">The vehicle's linear input on a range of -1.0 to 1.0.</param>
        public abstract void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput);

        protected virtual void Awake()
        {
            _particles = GetComponent<ParticleSystem>();
            if (_particles == null)
            {
                _particles = GetComponentInChildren<ParticleSystem>();
            }
            if (_particles != null)
            {
                // This is a struct, but somehow the correct way is indeed to copy it.
                _particleEmission = _particles.emission;
            }
        }

        protected virtual void OnEnable()
        {
            _parentBody = FindParentRigidbody();
            if (_parentBody != null)
            {
                _parentVehicleBody = _parentBody.GetComponent<Main.BasisVehicleBody>();
                if (_parentVehicleBody != null)
                {
                    _parentVehicleBody.RegisterPart(this);
                }
            }
        }

        private Rigidbody FindParentRigidbody()
        {
            Transform parent = transform.parent;
            while (parent != null)
            {
                Rigidbody rb = parent.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    return rb;
                }
                BasisDebug.LogWarning("BasisVehicleHoverThruster should ideally be a direct child of a GameObject with Rigidbody and BasisVehicleBody components.");
                parent = parent.parent;
            }
            return null;
        }
    }
}
