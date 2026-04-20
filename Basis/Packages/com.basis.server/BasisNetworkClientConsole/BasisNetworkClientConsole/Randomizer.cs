using Basis.Scripts.Networking.Compression;

namespace BasisNetworkClientConsole
{
    public class Randomizer
    {
        public static Vector3 GetRandomOffset()
        {
            return new Vector3(
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f,
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f,
                (float)(Random.Shared.NextDouble() * 2 - 1) / 4f
            );
        }
    }
}
