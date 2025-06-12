using System;

namespace BasisNetworkServer.Security
{
    [Serializable]
    public enum BasisUserRestrictionMode
    {
        Normal,
        BlackList,
        WhiteList,
    }
}
