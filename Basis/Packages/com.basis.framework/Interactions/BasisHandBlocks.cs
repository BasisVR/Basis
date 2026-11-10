using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Scripting.LifecycleManagement;

namespace Basis.Scripts.BasisSdk.Interactions
{
    [Flags]
    public enum BasisHandBlockKind : byte
    {
        None = 0,
        Raycast = 1,
        Pickup = 2,
        Touch = 4,
        All = Raycast | Pickup | Touch,
    }
    [AutoStaticsCleanup]
    public static partial class BasisHandBlocks
    {
        public const int Left = 0, Right = 1;
        private static readonly Dictionary<string, ushort> owners = new Dictionary<string, ushort>();
        private static byte left, right;
        public static int OwnerCount => owners.Count;
        public static bool ValidHand(int hand) => hand == Left || hand == Right;
        public static bool ValidKinds(BasisHandBlockKind kinds) => kinds != BasisHandBlockKind.None && (kinds & ~BasisHandBlockKind.All) == 0;
        public static BasisHandBlockKind Of(int hand) => (BasisHandBlockKind)(hand == Left ? left : hand == Right ? right : 0);
        public static bool IsBlocked(int hand, BasisHandBlockKind kinds) => (Of(hand) & kinds) != 0;
        public static bool IsBlocked(BasisInput input, BasisHandBlockKind kinds) => (left | right) != 0 && IsBlocked(HandOf(input), kinds);
        public static bool Holds(string owner) => owner != null && owners.ContainsKey(owner);
        public static BasisHandBlockKind HeldBy(string owner, int hand) => owner != null && ValidHand(hand) && owners.TryGetValue(owner, out ushort packed) ? (BasisHandBlockKind)(byte)(packed >> (hand * 8)) : BasisHandBlockKind.None;
        public static bool Add(string owner, int hand, BasisHandBlockKind kinds) => Change(owner, hand, kinds, true);
        public static bool Remove(string owner, int hand, BasisHandBlockKind kinds) => Change(owner, hand, kinds, false);
        public static void RemoveOwner(string owner)
        {
            if (owner != null && owners.Remove(owner)) Rebuild();
        }
        public static void Clear()
        {
            if (owners.Count == 0) return;
            owners.Clear();
            Rebuild();
        }
        public static int HandOf(BasisInput input)
        {
            if (input == null || !input.TryGetRole(out BasisBoneTrackedRole role)) return -1;
            if (role == BasisBoneTrackedRole.LeftHand) return Left;
            if (role == BasisBoneTrackedRole.RightHand) return Right;
            if (BasisPlayerInteract.IsDesktopCenterEye(input)) return BasisDominantHand.IsLeftHanded ? Left : Right;
            return -1;
        }
        private static bool Change(string owner, int hand, BasisHandBlockKind kinds, bool add)
        {
            if (string.IsNullOrEmpty(owner) || !ValidHand(hand) || !ValidKinds(kinds)) return false;
            owners.TryGetValue(owner, out ushort packed);
            int bits = (int)kinds << (hand * 8);
            ushort next = (ushort)(add ? packed | bits : packed & ~bits);
            if (next == packed) return true;
            if (next == 0) owners.Remove(owner);
            else owners[owner] = next;
            Rebuild();
            return true;
        }
        private static void Rebuild()
        {
            int all = 0;
            foreach (ushort packed in owners.Values) all |= packed;
            left = (byte)(all & 0xFF);
            right = (byte)(all >> 8);
        }
    }
}
