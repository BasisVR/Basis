using Basis.Scripts.Networking.Compression;

namespace BasisNetworkClientConsole
{
    public class Randomizer
    {
        /// <summary>
        /// Per-step jitter for the random walk. Deliberately small — this is the "moving about"
        /// signal, not the spread. Where clients actually sit comes from <see cref="GetSpawnPosition"/>.
        /// </summary>
        public static Vector3 GetRandomOffset()
        {
            return new Vector3(
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f,
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f,
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f
            );
        }

        /// <summary>
        /// A spawn point distributed uniformly over a horizontal disc of the given radius.
        ///
        /// This matters for load-test fidelity. The server picks avatar quality AND send interval per
        /// PAIR by distance (High under 10m, Medium 30m, Low 50m, VeryLow beyond), so clients spawned
        /// on top of each other put every pair in the closest, most expensive tier — a worst case no
        /// real instance hits. Spreading them over a realistic radius is what makes a "resting network
        /// usage" number mean anything.
        ///
        /// The sqrt keeps the distribution uniform by AREA; without it points bunch toward the centre
        /// and the effective spread is much smaller than the radius suggests. Y stays near standing
        /// height with slight variation, since players occupy a floor rather than a sphere.
        /// </summary>
        public static Vector3 GetSpawnPosition(float radiusMeters)
        {
            if (radiusMeters <= 0f)
            {
                return GetRandomOffset();
            }

            double angle = Random.Shared.NextDouble() * System.Math.PI * 2.0;
            double radius = radiusMeters * System.Math.Sqrt(Random.Shared.NextDouble());

            return new Vector3(
                (float)(System.Math.Cos(angle) * radius),
                1f + (float)(Random.Shared.NextDouble() * 0.2 - 0.1),
                (float)(System.Math.Sin(angle) * radius)
            );
        }
    }
}
