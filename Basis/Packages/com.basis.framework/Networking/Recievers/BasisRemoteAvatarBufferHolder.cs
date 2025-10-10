using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using UnityEngine;

namespace Basis.Scripts.Networking.Receivers
{
    [Serializable]
    public class BasisRemoteAvatarBufferHolder
    {
        public bool HasFirst = false;
        public bool HasLast = false;

        [SerializeField] public BasisAvatarBuffer First;
        [SerializeField] public BasisAvatarBuffer Last;

        public void ClearAndRelease()
        {
            if (HasFirst)
            {
                BasisAvatarBufferPool.Release(First);
                HasFirst = false;
            }
            if (HasLast)
            {
                BasisAvatarBufferPool.Release(Last);
                HasLast = false;
            }
        }
    }
}
