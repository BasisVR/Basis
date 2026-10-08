using Basis.Scripts.BasisSdk;
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

    public bool UseCustomPassword;
    public string UserSelectedPassword;

    /// <summary>
    /// Transient component snapshot captured during the load-time content walk
    /// (<see cref="BasisContentHarvest"/>). Null outside loading; the load path
    /// clears it once consumed.
    /// </summary>
    [System.NonSerialized]
    public BasisContentHarvest Harvest;

    /// <summary>
    /// Returns this content's <see cref="BasisContentHarvest"/>, building one from a live walk if it
    /// was never captured (old content, or a spawn that bypassed the content police). Cached on
    /// <see cref="Harvest"/>.
    /// </summary>
    public BasisContentHarvest EnsureHarvest(bool includeInactive = true)
    {
        if (Harvest == null || Harvest.Components == null)
        {
            Harvest = BasisContentHarvest.BuildFrom(gameObject, includeInactive);
        }
        return Harvest;
    }
}
