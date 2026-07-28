using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// StripAdditionalDataAtLowQuality: Low/VeryLow tiers drop AdditionalAvatarData (face/behaviour
/// params are unreadable at those view distances); High/Medium keep it. Off = legacy copy-to-all.
/// </summary>
[Collection("Basis reduction statics")]
public class AdditionalDataStripTests : IDisposable
{
    private readonly bool _saved = BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality;
    public void Dispose() => BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = _saved;

    private static LocalAvatarSyncMessage HighWithAdditional()
    {
        return new LocalAvatarSyncMessage
        {
            AdditionalAvatarDatas = new[] { new AdditionalAvatarData { messageIndex = 2, array = new byte[] { 1, 2, 3 } } },
            AdditionalAvatarDataSize = 1,
            LinkedAvatarIndex = 7,
        };
    }

    [Fact]
    public void StripOn_LowTiersDropAdditional_MediumKeepsIt()
    {
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = true;
        var high = HighWithAdditional();
        var med = new LocalAvatarSyncMessage();
        var low = new LocalAvatarSyncMessage();
        var vlow = new LocalAvatarSyncMessage();

        BasisServerReductionSystemEvents.PropagateAdditionalData(high, ref med, ref low, ref vlow);

        Assert.Same(high.AdditionalAvatarDatas, med.AdditionalAvatarDatas);
        Assert.Equal(high.AdditionalAvatarDataSize, med.AdditionalAvatarDataSize);
        Assert.Null(low.AdditionalAvatarDatas);
        Assert.Equal(0, low.AdditionalAvatarDataSize);
        Assert.Null(vlow.AdditionalAvatarDatas);
        Assert.Equal(0, vlow.AdditionalAvatarDataSize);
        Assert.Equal(high.LinkedAvatarIndex, med.LinkedAvatarIndex);
        Assert.Equal(high.LinkedAvatarIndex, low.LinkedAvatarIndex);
        Assert.Equal(high.LinkedAvatarIndex, vlow.LinkedAvatarIndex);
    }

    [Fact]
    public void StripOff_AllTiersKeepAdditional()
    {
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = false;
        var high = HighWithAdditional();
        var med = new LocalAvatarSyncMessage();
        var low = new LocalAvatarSyncMessage();
        var vlow = new LocalAvatarSyncMessage();

        BasisServerReductionSystemEvents.PropagateAdditionalData(high, ref med, ref low, ref vlow);

        Assert.Same(high.AdditionalAvatarDatas, med.AdditionalAvatarDatas);
        Assert.Same(high.AdditionalAvatarDatas, low.AdditionalAvatarDatas);
        Assert.Same(high.AdditionalAvatarDatas, vlow.AdditionalAvatarDatas);
        Assert.Equal(high.AdditionalAvatarDataSize, low.AdditionalAvatarDataSize);
        Assert.Equal(high.AdditionalAvatarDataSize, vlow.AdditionalAvatarDataSize);
    }
}
