using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using UnityEngine;

namespace Basis.Scripts.Networking.Receivers
{
    [Serializable]
    public class BasisRemoteAvatarBufferHolder
    {
        public bool HasCurrentBuffer = false;
        public bool HasNextBuffer = false;

        [NonSerialized] private BasisAvatarBuffer Current;
        [NonSerialized] private BasisAvatarBuffer Next;

        public void ClearAndRelease()
        {
            ReleaseCurrent();
            ReleaseNext();
        }
        public BasisAvatarBuffer RequestCurrent()
        {
            return Current;
        }
        public BasisAvatarBuffer RequestNext()
        {
            return Next;
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
