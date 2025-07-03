using System;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [Serializable]
    public enum BasisInteractInputState
    {
        /// <summary>
        /// Input in scene/initialized, BasisInput & Bone Control is null
        /// </summary>
        NotAdded,
        /// <summary>
        /// Input in scene, not affecting this interactable
        /// </summary>
        Ignored,
        /// <summary>
        /// Input is hovering
        /// </summary>
        Hovering,
        /// <summary>
        /// Input is interacting
        /// </summary>
        Interacting,
    }
}
