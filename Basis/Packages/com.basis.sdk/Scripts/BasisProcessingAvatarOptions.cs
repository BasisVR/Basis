namespace Basis.Scripts.BasisSdk
{
    [System.Serializable]
    public class BasisProcessingAvatarOptions
    {
        /// <summary>
        /// When checked, the Basis SDK will not automatically rename bones which conflict with the avatar Humanoid bones.<br/>
        /// This serves as an opt-out, and we expect this to be used during testing if Basis migrates away from the Animation Rigging package.<br/>
        /// The Animation Rigging package is reason why we need to rename duplicated humanoid bone names in the first place.
        /// </summary>
        public bool doNotAutoRenameBones = false;

        /// <summary>
        /// Removes Unused Blendshapes, can be controlled through the callbacks
        /// when on the blendshape indexes break and need reconstruction from callback.
        /// </summary>
      // disabled does work just not for all avatars  public bool RemoveUnusedBlendshapes = false;
    }
}
