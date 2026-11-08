using System;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Contacts
{
    [AddComponentMenu("Basis/Contacts/Basis Contact Receiver")]
    public class BasisContactReceiver : MonoBehaviour
    {
        public BasisContactShape shape = BasisContactShape.Sphere;
        public Vector3 center = Vector3.zero;
        public float radius = 0.1f;
        public float height = 0.3f;
        public string[] tags = new string[0];
        public bool allowSelf = true;
        public bool allowOthers = true;
        public bool allowWorld = true;
        public BasisContactReceiverMode mode = BasisContactReceiverMode.Constant;
        public float pulseSeconds = 0.2f;
        public string parameter = "";
        [NonSerialized] public BasisAvatar OwnerAvatar;
        public float Value { get; private set; }
        public bool IsTouched { get; private set; }
        public int TouchingPlayerId { get; private set; } = BasisContacts.NoPlayer;
        public int LastTouchingPlayerId { get; private set; } = BasisContacts.NoPlayer;
        public event Action<BasisContactReceiver> OnContactBegin;
        public event Action<BasisContactReceiver> OnContactEnd;
        public event Action<BasisContactReceiver> OnValueChanged;

        private void OnEnable()
        {
            OwnerAvatar = GetComponentInParent<BasisAvatar>(true);
            BasisContacts.Register(this);
        }

        private void OnDisable()
        {
            BasisContacts.Unregister(this);
            ApplyContactState(0f, false, BasisContacts.NoPlayer);
        }

        public void GetWorldSegment(out Vector3 a, out Vector3 b, out float worldRadius)
        {
            BasisContacts.GetSegment(transform, shape, center, radius, height, out a, out b, out worldRadius);
        }

        public void ApplyContactState(float value, bool touched, int playerId)
        {
            bool began = touched && !IsTouched, ended = !touched && IsTouched, valueChanged = !Mathf.Approximately(value, Value);
            Value = value;
            IsTouched = touched;
            TouchingPlayerId = touched ? playerId : BasisContacts.NoPlayer;
            if (touched)
            {
                LastTouchingPlayerId = playerId;
            }
            if (began)
            {
                Raise(OnContactBegin);
            }
            if (ended)
            {
                Raise(OnContactEnd);
            }
            if (valueChanged)
            {
                Raise(OnValueChanged);
            }
            if (began || ended || valueChanged)
            {
                BasisContacts.RaiseReceiverChanged(this);
            }
        }

        private void Raise(Action<BasisContactReceiver> handler)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Contact receiver {name} listener threw: {e}");
            }
        }

        private void OnDrawGizmosSelected()
        {
            GetWorldSegment(out Vector3 a, out Vector3 b, out float worldRadius);
            Gizmos.color = IsTouched ? new Color(0.2f, 1f, 0.3f, 0.9f) : new Color(0.2f, 0.6f, 1f, 0.9f);
            Gizmos.DrawWireSphere(a, worldRadius);
            if (shape == BasisContactShape.Capsule)
            {
                Gizmos.DrawWireSphere(b, worldRadius);
                Gizmos.DrawLine(a, b);
            }
        }
    }
}
