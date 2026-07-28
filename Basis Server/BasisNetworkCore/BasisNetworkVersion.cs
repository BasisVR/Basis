namespace Basis.Network.Core
{
    public class BasisNetworkVersion
    {
        // 39: avatar delta compression (DeltaAvatarChannel, ContentShare channel merge).
        // 40: High bone quality 10->12 bits for body/limb joints (anti-shimmer; larger High packet).
        // 41: end-effector anchoring block (+39B on High) — hand/foot world targets + swivel.
        // 42: int24-mm position on Medium/Low/VeryLow (-3B), extended interval-byte encoding past
        //     305ms, upstream/P2P avatar deltas + keyframe request control frames on DeltaAvatarChannel.
        // 43: AdditionalAvatarData entries always carry the [size][messageIndex] header (size-0
        //     entries previously desynced the section) — old/new peers must not mix.
        // 44: end-effector swivel dropped (-4B on High, block 39->35, High 236->232) — the receiver
        //     took its pole from the FK joint and never read it. Body-fit (arm/leg/torso segment
        //     scales, quantized to 3x ushort) added to the avatar-change record, with a
        //     kind-discriminated fit-only update. Join fill batched + Deflate'd per batch instead of
        //     one packet per player (the redundancy is across players, not inside one record).
        //     playerUUID and playerPlatform compactly encoded (BasisCompactId / BasisPlatformCodec).
        public static ushort ServerVersion = 45;
    }
}
