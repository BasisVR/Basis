using System;
using JetBrains.Annotations;
using UnityEngine;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(PanelElementDescriptor))]
    public abstract class PanelComponent : AddressableUIInstanceBase
    {

        public PanelElementDescriptor Descriptor { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            Descriptor = GetComponent<PanelElementDescriptor>();
        }

        [UsedImplicitly]
        public virtual void OnComponentUsed()
        {
        }
    }
}
