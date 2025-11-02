using System;

namespace Basis.BasisUI
{
    public interface IAddressableInstance
    {
        public Action OnInstanceReleased { get; set; }
        public bool IsReleased { get; }
        public void ReleaseInstance();
        public void OnCreateEvent();
        public void OnReleaseEvent();
        public bool HasRunCreateEvent { get; }
    }
}
