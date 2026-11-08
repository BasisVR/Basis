using System;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Contacts
{
    [AddComponentMenu("Basis/Contacts/Basis Contact Sender")]
    public class BasisContactSender : MonoBehaviour
    {
        public BasisContactShape shape = BasisContactShape.Sphere;
        public Vector3 center = Vector3.zero;
        public float radius = 0.05f;
        public float height = 0.2f;
        public string[] tags = new string[] { "Custom" };
        [NonSerialized] public BasisAvatar OwnerAvatar;

        private void OnEnable()
        {
            OwnerAvatar = GetComponentInParent<BasisAvatar>(true);
            BasisContacts.Register(this);
        }

        private void OnDisable()
        {
            BasisContacts.Unregister(this);
        }

        public void GetWorldSegment(out Vector3 a, out Vector3 b, out float worldRadius)
        {
            BasisContacts.GetSegment(transform, shape, center, radius, height, out a, out b, out worldRadius);
        }

        private void OnDrawGizmosSelected()
        {
            GetWorldSegment(out Vector3 a, out Vector3 b, out float worldRadius);
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.9f);
            Gizmos.DrawWireSphere(a, worldRadius);
            if (shape == BasisContactShape.Capsule)
            {
                Gizmos.DrawWireSphere(b, worldRadius);
                Gizmos.DrawLine(a, b);
            }
        }
    }
}
