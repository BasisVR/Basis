using System;
using UnityEngine;
/// <summary>
/// Represents base content with an assigned client-generated identifier that can be resolved
/// to a server-assigned ushort network ID.
/// </summary>
public abstract class BasisNetworkContentBase : MonoBehaviour
{

    public virtual string clientIdentifier
    {
        get
        {
            return ContentInformation.LoadedNetID;
        }
   
    }
    public Action<BasisContentInformation> OnClientIdentifierAssigned;

    public virtual BasisContentInformation ContentInformation { get; protected set; } = new BasisContentInformation() { IsAdminLocked = false, LoadedNetID = string.Empty};
    /// <summary>
    /// Attempts to get the currently assigned GUID identifier.
    /// </summary>
    /// <param name="identifier">Out string of the identifier</param>
    /// <returns>True if identifier is assigned</returns>
    public bool TryGetIdentifier(out BasisContentInformation identifier)
    {
        if(ContentInformation.LoadedNetID == string.Empty)
        {
            identifier = default;
            return false;
        }
        identifier = ContentInformation;
        return true;
    }
    /// <summary>
    /// Attempts to get the currently assigned GUID identifier.
    /// </summary>
    /// <param name="identifier">Out string of the identifier</param>
    /// <returns>True if identifier is assigned</returns>
    public bool TryGetNetworkGUIDIdentifier(out string identifier)
    {
        if (ContentInformation.LoadedNetID == string.Empty)
        {
            identifier = default;
            return false;
        }
        identifier = ContentInformation.LoadedNetID;
        return true;
    }

    /// <summary>
    /// Sets the string-based identifier used to resolve to a server-assigned ushort network ID.
    /// </summary>
    /// <param name="identifier">Client-side identifier string</param>
    public void AssignContentIdentifier(BasisContentInformation identifier)
    {
        ContentInformation = identifier;
        OnClientIdentifierAssigned?.Invoke(ContentInformation);
    }
    public struct BasisContentInformation
    {
        /// <summary>
        /// 0 = Game object, 1 = Scene,
        /// </summary>
        public byte Mode;
        /// <summary>
        /// this is a unique string that this Object is linked with over the network.
        /// </summary>
        public string LoadedNetID;

        public string UUIDOfCreator;
        /// <summary>
        /// normal users can't remove these items
        /// never net written just handled by server
        /// </summary>
        public bool IsAdminLocked;

        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public float QuaternionX;
        public float QuaternionY;
        public float QuaternionZ;
        public float QuaternionW;

        public float ScaleX;
        public float ScaleY;
        public float ScaleZ;

        public bool Persist;
        /// <summary>
        /// when true this item is "static": pickup is disabled for everyone and the object is
        /// frozen in place (vehicles are locked out). Server-authoritative, set via ModifyResource.
        /// </summary>
        public bool Static;
        /// <summary>
        /// when true the static lock is "admin tier": only a moderator (not the item's creator) may
        /// change or clear it, and it implies <see cref="Static"/> is also true. Distinct from
        /// <see cref="IsAdminLocked"/>, which is the removal lock.
        /// </summary>
        public bool StaticAdminLocked;
        /// <summary>
        /// this is used to state if the scale should be set or
        /// just use whatever scale it thinks it is.
        /// </summary>
        public bool ModifyScale;
        /// <summary>
        /// Determines how the resource should be loaded on clients.
        /// 0 = Immediate (spawn right away, existing behavior),
        /// 2 = Synchronized (download, report readiness, spawn when all ready or 5-min timeout),
        /// 3 = Predownload (download and cache to disc on every client, never spawn).
        /// </summary>
        public byte LoadStrategy;
    }

}
