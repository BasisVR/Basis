using Basis.Network.Core;
using System.Runtime.CompilerServices;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetQualityIndex(float distSq)
        {
            if (distSq <= HighDistanceSq) return 3;   // High
            if (distSq <= MediumDistanceSq) return 2;  // Medium
            if (distSq <= LowDistanceSq) return 1;     // Low
            return 0;                                   // VeryLow
        }

        public static bool TryGetJoinSnapshot(Basis.Scripts.Networking.Compression.Vector3 viewerPosition, int subjectId, out LocalAvatarSyncMessage snapshot)
        {
            snapshot = default;

            if (!playerStates.TryGetValue(subjectId, out PlayerState subject) || subject == null)
            {
                return false;
            }

            LocalAvatarSyncMessage high = subject.SyncMessage.avatarSerialization;
            if (high.array == null)
            {
                return false;
            }

            if (subject.BypassReduction)
            {
                snapshot = high;
                return true;
            }

            float dx = viewerPosition.x - subject.Position.x;
            float dy = viewerPosition.y - subject.Position.y;
            float dz = viewerPosition.z - subject.Position.z;
            float distSq = dx * dx + dy * dy + dz * dz;

            LocalAvatarSyncMessage tier = GetQualityIndex(distSq) switch
            {
                3 => high,
                2 => subject.AvatarMedium,
                1 => subject.AvatarLow,
                _ => subject.AvatarVeryLow,
            };

            snapshot = tier.array != null ? tier : high;
            return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateIntervalFromDistanceSq(float distanceSq, out byte offsetByte, out int actualInterval)
        {
            int rawInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (distanceSq * BSRSIncreaseRate)));

            offsetByte = BasisNetworkCommons.EncodeAvatarIntervalByte(rawInterval, BSRSMillisecondDefaultInterval);
            actualInterval = BasisNetworkCommons.DecodeAvatarIntervalMs(offsetByte, BSRSMillisecondDefaultInterval);
        }
    }
}
