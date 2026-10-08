using Basis.Network.Core;
using Xunit;

[assembly: BasisNetworkPackage(typeof(BasisServerTests.CountingNetworkPackage))]
[assembly: BasisNetworkPackage(typeof(BasisServerTests.ThrowingNetworkPackage))]
[assembly: BasisNetworkPackage(typeof(BasisServerTests.NotANetworkPackage))]
[assembly: BasisNetworkPackage(typeof(BasisServerTests.StackNetworkPackage))]

namespace BasisServerTests;

public sealed class CountingNetworkPackage : IBasisNetworkPackage
{
    public static int Runs;
    public void Initialize() => Interlocked.Increment(ref Runs);
}

public sealed class ThrowingNetworkPackage : IBasisNetworkPackage
{
    public void Initialize() => throw new InvalidOperationException("package refused to start");
}

public sealed class NotANetworkPackage
{
}

public sealed class StackNetworkPackage : IBasisNetworkPackage
{
    public const string StackId = "test-package-stack";
    public void Initialize() => BasisNetworkStackRegistry.Register(StackId, "Test package stack", (listener, configuration) => null!);
}

public class NetworkPackageTests
{
    [Fact]
    public void Entries_run_once_and_failures_are_isolated()
    {
        BasisNetworkPackages.Initialize(typeof(NetworkPackageTests).Assembly, null!);
        int second = BasisNetworkPackages.Initialize(typeof(NetworkPackageTests).Assembly);

        Assert.Equal(0, second);
        Assert.Equal(1, Volatile.Read(ref CountingNetworkPackage.Runs));
        Assert.Contains(typeof(CountingNetworkPackage), BasisNetworkPackages.Loaded);
        Assert.Contains(typeof(StackNetworkPackage), BasisNetworkPackages.Loaded);
        Assert.DoesNotContain(typeof(ThrowingNetworkPackage), BasisNetworkPackages.Loaded);
        Assert.Contains(BasisNetworkPackages.Failures, f => f.Contains(nameof(ThrowingNetworkPackage)) && f.Contains("package refused to start"));
        Assert.Contains(BasisNetworkPackages.Failures, f => f.Contains(nameof(NotANetworkPackage)) && f.Contains(nameof(IBasisNetworkPackage)));
    }

    [Fact]
    public void A_package_can_register_a_network_stack()
    {
        BasisNetworkPackages.Initialize(typeof(NetworkPackageTests).Assembly);

        Assert.True(BasisNetworkStackRegistry.IsRegistered(StackNetworkPackage.StackId));
        Assert.Equal("Test package stack", BasisNetworkStackRegistry.GetDisplayName(StackNetworkPackage.StackId));
    }

    [Fact]
    public void Assemblies_without_packages_are_ignored()
    {
        Assert.Equal(0, BasisNetworkPackages.Initialize(typeof(object).Assembly, typeof(FactAttribute).Assembly));
        Assert.Equal(0, BasisNetworkPackages.Initialize(null!));
    }
}
