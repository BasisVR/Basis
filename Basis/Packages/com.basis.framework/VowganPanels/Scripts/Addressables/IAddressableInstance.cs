using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public interface IAddressableInstance
    {
        public Action OnReleased { get; set; }
        public bool IsReleased { get; }
        public void ReleaseInstance();
        public void OnCreateEvent();
        public void OnReleaseEvent();
    }
}
