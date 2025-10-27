using System;

namespace Basis.VowganUI
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
