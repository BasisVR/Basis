using UnityEngine;

/// <summary>
/// Base class for all content objects within the Basis system.
/// Inherits from <see cref="BasisNetworkContentBase"/>, enabling networked behavior
/// and common metadata support for content bundles.
/// </summary>
public abstract class BasisContentBase : BasisNetworkContentBase
{
    /// <summary>
    /// Description of the bundle this content belongs to, providing metadata for loading
    /// and runtime management.
    /// </summary>
    [SerializeField]
    public BasisBundleDescription BasisBundleDescription;
}
