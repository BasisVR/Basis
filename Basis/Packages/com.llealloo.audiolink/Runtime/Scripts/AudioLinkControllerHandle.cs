
#if UDONSHARP
using UdonSharp;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDKBase;
using VRC.Udon;

namespace AudioLink
{
    public class AudioLinkControllerHandle : UdonSharpBehaviour
    {
        public ParentConstraint parentConstraint;

        private ParentConstraint selfConstraint;

        public void Start()
        {
            selfConstraint = GetComponent<ParentConstraint>();
        }

        public override void OnPickup()
        {
            Networking.SetOwner(Networking.LocalPlayer, parentConstraint.gameObject);

            selfConstraint.enabled = false;

            parentConstraint.enabled = true;

            int me = -1, other = -1;
            for (int i = 0; i < 2; i++)
            {
                if (parentConstraint.GetSource(i).sourceTransform == transform)
                {
                    me = i;
                }
                else
                {
                    other = i;
                }
            }

            if (parentConstraint.GetSource(other).weight > 0)
            {
                var otherSource = parentConstraint.GetSource(other);
                otherSource.weight = 0.5f;
                var meSource = parentConstraint.GetSource(me);
                meSource.weight = 0.5f;
                parentConstraint.SetSource(me, meSource);
                parentConstraint.SetSource(other, otherSource);
            }
            else
            {
                var meSource = parentConstraint.GetSource(me);
                meSource.weight = 1;
                parentConstraint.SetSource(me, meSource);
            }
        }

        public override void OnDrop()
        {
            selfConstraint.enabled = true;

            int me = -1, other = -1;
            for (int i = 0; i < 2; i++)
            {
                if (parentConstraint.GetSource(i).sourceTransform == transform)
                {
                    me = i;
                }
                else
                {
                    other = i;
                }
            }

            var meSource = parentConstraint.GetSource(me);
            meSource.weight = 0;
            parentConstraint.SetSource(me, meSource);
            if (parentConstraint.GetSource(other).weight > 0)
            {
                var otherSource = parentConstraint.GetSource(other);
                otherSource.weight = 1f;
                parentConstraint.SetSource(other, otherSource);
            }
            else
            {
                parentConstraint.enabled = false;
            }
        }
    }
}
#endif
