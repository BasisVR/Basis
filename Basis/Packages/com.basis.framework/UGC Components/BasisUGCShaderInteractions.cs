using Basis.Scripts.Behaviour;
using UnityEngine;
namespace Basis.Scripts.UGC.ShaderInteractions
{
    public class BasisUGCShaderInteractions : BasisAvatarMonoBehaviour
    {
        [SerializeField]
        public BasisUGCShaderInteractionsItem[] basisUGCShaderInteractionsItems;

        [System.Serializable]
        public struct BasisUGCShaderInteractionsItem
        {
            public BasisUGCMenuDescription Description;
            public BasisUGCShaderSettings[] ToggleableGameObjects;
        }
        [System.Serializable]
        public struct BasisUGCShaderSettings
        {
            public Material Material;
            public string MaterialProperty;
        }
    }
}
