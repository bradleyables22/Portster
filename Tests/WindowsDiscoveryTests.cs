using Portster.Platform;

namespace Portster.Tests;

public class WindowsDiscoveryTests
{
    [Fact]
    public async Task ActualWindowsDiscoveryReturnsUniquePortNamesWithoutOpeningDevices()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var catalog = new WindowsPortCatalog();
        var ports = await catalog.ListAsync(timeout.Token);
        Assert.Equal(ports.Count, ports.Select(p => p.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ports, p => Assert.True(ServerPolicy.IsPortName(p.PortName)));
    }
}
