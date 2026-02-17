using Basis.Network.Core.Compression;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [System.Serializable]
    public class BasisStoredAvatarData
    {
        [SerializeField]
        public LocalAvatarSyncMessage LASM = new LocalAvatarSyncMessage(new byte[BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.High)]);
    }
}
