using System;
using JetBrains.Annotations;
using UnityEngine;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(PanelElementDescriptor))]
    public abstract class PanelComponent : AddressableUIInstanceBase
    {

        public PanelElementDescriptor Descriptor
        {
            get
            {
                if (!_descriptor) _descriptor = GetComponent<PanelElementDescriptor>();
                return _descriptor;
            }
        }

        private PanelElementDescriptor _descriptor;

        [UsedImplicitly]
        public virtual void OnComponentUsed()
        {
        }
    }
}
