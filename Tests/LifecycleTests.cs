using Microsoft.Extensions.Logging.Abstractions;
using Portster;

namespace Portster.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task ExpiryClosesIdleConnectionsAndRetiresCaptureRetention()
    {
        var policy = Fixtures.Policy with { InactivityTimeoutSeconds = 1, CaptureLifetimeSeconds = 2 };
        // Short durations exercise the same lifecycle without making the suite wait production TTLs.
        using var registry = new ConnectionRegistry(policy, new FakeCatalog(new PortInfo("COM7")), new FakeFactory(() => new FakeSerial()), new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        await registry.StartAsync(default);
        try
        {
            var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
            var capture = registry.StartCapture(opened.ConnectionHandle, 1024, new("capture"));
            await Task.Delay(1200);
            Assert.NotNull(registry.Get(opened.ConnectionHandle)); // Live capture keeps an otherwise idle connection open.
            await Task.Delay(2100);
            Assert.Throws<PortsterException>(() => registry.GetCapture(capture.CaptureHandle));
            await Fixtures.Eventually(() =>
            {
                try { registry.Get(opened.ConnectionHandle); return false; }
                catch (PortsterException ex) { return ex.Code == "EXPIRED_HANDLE"; }
            });
        }
        finally { await registry.StopAsync(default); }
    }
    [Fact]
    public async Task TimedOutWriteReservationLastsUntilTheNativeWriteReturns()
    {
        using var release = new ManualResetEventSlim();
        var serial = new FakeSerial { OnWrite = _ => release.Wait(8000) };
        var policy = Fixtures.Policy with { WriteTimeoutMs = 100 };
        using var registry = new ConnectionRegistry(policy, new FakeCatalog(new PortInfo("COM7")), new FakeFactory(() => serial), new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        await registry.StartAsync(default);
        try
        {
            var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
            await Assert.ThrowsAsync<PortsterException>(() => registry.Get(opened.ConnectionHandle).WriteAsync(new() { Data = "01" }, new("write"), default));
            await Task.Delay(1100); // Allow the reaper to inspect the completed native close.
            Assert.True(serial.Disposed);
            Assert.Equal("PORT_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        }
        finally { release.Set(); await registry.StopAsync(default); }
    }
}
