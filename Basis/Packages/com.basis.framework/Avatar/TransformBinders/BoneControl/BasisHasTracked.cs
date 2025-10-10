namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// Indicates whether a bone currently has an active tracker bound to it.
    /// </summary>
    [System.Serializable]
    public enum BasisHasTracked : byte
    {
        /// <summary>
        /// The bone is being driven by a tracker.
        /// </summary>
        HasTracker,

        /// <summary>
        /// The bone is not being driven by any tracker.
        /// </summary>
        HasNoTracker,
    }
}
