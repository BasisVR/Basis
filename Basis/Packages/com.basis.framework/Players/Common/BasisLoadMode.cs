namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Defines how an avatar or player resource should be loaded into the scene.
    /// </summary>
    public enum BasisLoadMode : byte
    {
        /// <summary>
        /// Load the asset by downloading it from a remote source (network).
        /// </summary>
        Download = 0,

        /// <summary>
        /// Load the asset from a local file or pre-installed package.
        /// </summary>
        Local = 1,

        /// <summary>
        /// Load the asset directly by referencing an existing <see cref="UnityEngine.GameObject"/> in the scene or project.
        /// </summary>
        ByGameobjectReference = 2,
    };
}
