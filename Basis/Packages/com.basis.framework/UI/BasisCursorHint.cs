using UnityEngine;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Attach to any UI GameObject to declare what cursor type should be shown when this element is hovered.
    /// If a custom texture is set and CursorType is Custom, the reticle will use that texture.
    /// </summary>
    [AddComponentMenu("Basis/UI/Cursor Hint")]
    public class BasisCursorHint : MonoBehaviour
    {
        [Tooltip("The cursor type to display when this UI element is hovered.")]
        public BasisCursorType CursorType = BasisCursorType.Pointer;

        [Tooltip("Optional custom texture for the reticle when CursorType is Custom.")]
        public Texture2D CustomTexture;
    }
}
