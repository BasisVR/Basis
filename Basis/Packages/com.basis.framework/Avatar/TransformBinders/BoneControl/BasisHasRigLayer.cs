namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// Indicates whether a bone is currently associated with a rig layer.
    /// </summary>
    [System.Serializable]
    public enum BasisHasRigLayer : byte
    {
        /// <summary>
        /// The bone is controlled by a rig layer.
        /// </summary>
        HasRigLayer,

        /// <summary>
        /// The bone is not controlled by any rig layer.
        /// </summary>
        HasNoRigLayer,
    }
}
