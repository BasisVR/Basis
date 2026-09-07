[System.Serializable]
public class BasisRemoteEncyptedBundle
{
    public string RemoteBeeFileLocation;
    // Opaque content-version tag for whatever currently lives at RemoteBeeFileLocation: an HTTP
    // validator (ETag, else Last-Modified) when the host supplies one, otherwise a creator-stamped
    // nonce. It is what lets a bee be re-uploaded to a STATIC url and still invalidate every
    // client's cache — url identity alone can never express "same address, new bytes".
    // Empty means "no version declared", and every consumer treats that as "the cache is
    // authoritative", which is exactly how loads behaved before this field existed. That is what
    // keeps already-cached bundles, older clients and the GUID-per-upload flow working untouched.
    public string RemoteVersionTag;
    // Set wherever RemoteBeeFileLocation arrives over the network (spawn/load-resource records,
    // relayed avatar changes, preloads). A network-sourced location is never resolved against the
    // local filesystem — only the HTTP(S) path with its scheme and SSRF checks may serve it.
    public bool IsNetworkSourced;

    /// <summary>
    /// An independent copy. Required wherever this record crosses an ownership boundary — the
    /// in-memory bundle registry keys on <see cref="RemoteVersionTag"/>, so an instance shared
    /// between a live wrapper, the on-disc meta cache and the library UI lets one of them
    /// silently re-key a bundle that another is still holding.
    /// </summary>
    public BasisRemoteEncyptedBundle Clone()
    {
        return new BasisRemoteEncyptedBundle
        {
            RemoteBeeFileLocation = RemoteBeeFileLocation,
            RemoteVersionTag = RemoteVersionTag,
            IsNetworkSourced = IsNetworkSourced,
        };
    }
}
