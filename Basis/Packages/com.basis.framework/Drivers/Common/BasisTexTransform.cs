using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public struct BasisTexTransform
    {
        public Texture texture;
        public Vector2 scale;
        public Vector2 offset;
        public BasisTexTransform(Texture tex, Vector2 scale, Vector2 offset)
        {
            this.texture = tex;
            this.scale = scale;
            this.offset = offset;
        }
    }
}
