using Basis.Scripts.BasisSdk;
using UnityEngine;
namespace Basis.Scripts.Behaviour
{
    public abstract class BasisAvatarMonoBehaviour : MonoBehaviour
    {
        // [HideInInspector]
        public bool IsInitalized = false;
        // [HideInInspector]
        public byte MessageIndex;
        public virtual void OnNetworkReady(bool IsLocallyOwned)
        {

        }
        /// <summary>
        /// data that came out of the server reduction system
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="IsADifferentAvatarLocally">Indicates if the avatar worn matches or not</param>
        public virtual void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
           // BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageServerReductionSystem", this.gameObject, BasisDebug.LogTag.Avatar);
        }
        /// <summary>
        /// Whether this behaviour type is visible in the Avatar SDK inspector's
        /// Network Behaviours section. Subclasses shadow with <c>new public static bool VisibleInAvatarMenu = false;</c> to hide.
        /// The inspector reads this per-type via reflection.
        /// </summary>
        public static bool VisibleInAvatarMenu = true;
#if UNITY_EDITOR
        /// <summary>
        /// Called in-editor when this component is added via the Avatar SDK inspector.
        /// Override to auto-configure serialized references (e.g., target meshes).
        /// Stripped from player builds.
        /// </summary>
        /// <param name="avatarRoot">The GameObject containing the BasisAvatar component.</param>
        /// <param name="avatar">The BasisAvatar component on the root.</param>
        public virtual void OnEditorSetup(GameObject avatarRoot, BasisAvatar avatar) { }
#endif
    }
}
