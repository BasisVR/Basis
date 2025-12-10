using Basis.Scripts.Networking.NetworkedAvatar;
using System;

namespace Basis.Scripts.Networking.Receivers
{
    [Serializable]
    public class BasisRemoteAvatarBufferHolder
    {
        public bool HasCurrentBuffer = false;
        public bool HasNextBuffer = false;

        public BasisAvatarBuffer Current { get; private set; }
        public BasisAvatarBuffer Next { get; private set; }

        public void ClearAndRelease()
        {
            ReleaseCurrent();
            ReleaseNext();
        }
        public void SetNext(ref BasisAvatarBuffer NextTarget)
        {
            Next = NextTarget;
        }
        public void SetCurrent(ref BasisAvatarBuffer CurrentTarget)
        {
            Current = CurrentTarget;
        }
        public void ReleaseCurrent()
        {
            if (HasCurrentBuffer)
            {
                BasisAvatarBufferPool.Release(Current);
                Current = null;
                HasCurrentBuffer = false;
            }
        }
        public void ReleaseNext()
        {
            if (HasNextBuffer)
            {
                BasisAvatarBufferPool.Release(Next);
                Next = null;
                HasNextBuffer = false;
            }
        }
        public void NextBecomesCurrent()
        {
            if (HasCurrentBuffer)
            {
                ReleaseCurrent();
            }

            Current = Next;

            HasCurrentBuffer = true;
            HasNextBuffer = false;
        }
    }
}
