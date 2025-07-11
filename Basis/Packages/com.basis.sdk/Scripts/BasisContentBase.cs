using System;
using UnityEngine;

/// <summary>
/// Represents base content with an assigned client-generated identifier that can be resolved
/// to a server-assigned ushort network ID.
/// </summary>
public abstract class BasisContentBase : MonoBehaviour
{
    [SerializeField]
    public BasisBundleDescription BasisBundleDescription;

    public string clientIdentifier { get; private set; } = string.Empty; // Represents the string used to look up the ushort on the server.

    public Action<string> OnClientIdentifierAssigned;

    public bool IsClientIdentifierAssigned { get; private set; } = false;

    /// <summary>
    /// Attempts to get the currently assigned GUID identifier.
    /// </summary>
    /// <param name="identifier">Out string of the identifier</param>
    /// <returns>True if identifier is assigned</returns>
    public bool TryGetNetworkGUIDIdentifier(out string identifier)
    {
        identifier = clientIdentifier;
        return IsClientIdentifierAssigned;
    }

    /// <summary>
    /// Sets the string-based identifier used to resolve to a server-assigned ushort network ID.
    /// </summary>
    /// <param name="identifier">Client-side identifier string</param>
    public void AssignNetworkGUIDIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            IsClientIdentifierAssigned = false;
            BasisDebug.LogError("Client identifier string is null or empty!");
            return;
        }

        clientIdentifier = identifier;
        IsClientIdentifierAssigned = true;
        OnClientIdentifierAssigned?.Invoke(clientIdentifier);
    }
}
