using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Piecewise avatar-interval byte codec (v42): bytes 0..199 keep the legacy base+b meaning,
/// bytes 200..255 step 12 ms so distant receivers can pace below the old 3.3 Hz floor.
/// </summary>
public class AvatarIntervalCodecTests
{
    private const int Base = 50;

    [Fact]
    public void LegacyRegion_IsExactAndUnchanged()
    {
        for (int ms = Base; ms < Base + BasisNetworkCommons.AvatarIntervalExtendedStart; ms++)
        {
            byte b = BasisNetworkCommons.EncodeAvatarIntervalByte(ms, Base);
            Assert.Equal(ms - Base, b);
            Assert.Equal(ms, BasisNetworkCommons.DecodeAvatarIntervalMs(b, Base));
        }
    }

    [Fact]
    public void EveryByte_RoundTripsThroughDecodeThenEncode()
    {
        for (int b = 0; b <= byte.MaxValue; b++)
        {
            int ms = BasisNetworkCommons.DecodeAvatarIntervalMs((byte)b, Base);
            Assert.Equal((byte)b, BasisNetworkCommons.EncodeAvatarIntervalByte(ms, Base));
        }
    }

    [Fact]
    public void Decode_IsStrictlyMonotonic_AndCapsUnderReceiverWindowClamp()
    {
        int prev = -1;
        for (int b = 0; b <= byte.MaxValue; b++)
        {
            int ms = BasisNetworkCommons.DecodeAvatarIntervalMs((byte)b, Base);
            Assert.True(ms > prev, $"byte {b} not monotonic: {ms} <= {prev}");
            prev = ms;
        }
        // Max encodable interval stays under the receiver's 1 s interpolation-window clamp.
        Assert.Equal(Base + 200 + 55 * 12, prev);
        Assert.True(prev < 1000);
    }

    [Fact]
    public void ExtendedRegion_QuantizesWithinHalfStep()
    {
        for (int ms = Base + 200; ms <= Base + 200 + 55 * 12; ms += 7)
        {
            byte b = BasisNetworkCommons.EncodeAvatarIntervalByte(ms, Base);
            int back = BasisNetworkCommons.DecodeAvatarIntervalMs(b, Base);
            Assert.True(Math.Abs(back - ms) <= BasisNetworkCommons.AvatarIntervalExtendedStepMs / 2 + 1,
                $"{ms} -> byte {b} -> {back}");
        }
    }

    [Fact]
    public void OutOfRange_Clamps()
    {
        Assert.Equal(0, BasisNetworkCommons.EncodeAvatarIntervalByte(0, Base));
        Assert.Equal(0, BasisNetworkCommons.EncodeAvatarIntervalByte(Base, Base));
        Assert.Equal(byte.MaxValue, BasisNetworkCommons.EncodeAvatarIntervalByte(Base + 5000, Base));
        Assert.Equal(byte.MaxValue, BasisNetworkCommons.EncodeAvatarIntervalByte(int.MaxValue - Base, Base));
    }
}
