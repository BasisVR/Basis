using System;
using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    /// <summary>
    /// Avatar component that hides additional transforms while this avatar is the local
    /// player's first-person view. For non-standard heads (long necks, helmets, hair pieces,
    /// masks) that obscure the camera even when the head bone alone is hidden.
    /// </summary>
    public class BasisHeadChop : MonoBehaviour
    {
        [Serializable]
        public struct HeadChopTarget
        {
            public Transform Target;
            [Range(0f, 1f)]
            public float Scale;
        }

        public HeadChopTarget[] Targets = Array.Empty<HeadChopTarget>();
    }
}
