using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Portster.Tests;

public class CaptureTests
{
    [Fact]
    public async Task CaptureStartsAtZeroAndIncludesOnlyFutureBytes()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        serial.Feed(1, 2); await Fixtures.Eventually(() => connection.Buffer.End == 2);
        var capture = connection.StartCapture(1024); serial.Feed(3, 4);
        var data = await capture.ReadAsync(0, new() { Mode = CompletionMode.Length, Length = 2 }, default);
        Assert.Equal(0, data.StartCursor); Assert.Equal("0304", data.Hex); Assert.Equal(2, data.NextCursor);
    }
    [Fact]
    public async Task CaptureOverrunDoesNotChangeConnectionRetention()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        var capture = connection.StartCapture(1024); serial.Feed(Enumerable.Repeat((byte)42, 2048).ToArray());
        await Fixtures.Eventually(() => capture.Buffer.End == 2048);
        var captured = await capture.ReadAsync(0, new() { WaitMs = 0 }, default);
        var normal = await connection.ReadAsync(0, new() { WaitMs = 0 }, new("read"), default);
        Assert.Equal(1024, captured.LostBytes); Assert.Equal("overrun", captured.Completion);
        Assert.Equal(0, normal.LostBytes); Assert.Equal(2048, normal.ByteCount);
    }
    [Fact]
    public async Task StopFreezesCaptureButLeavesItsRetainedBytesReadable()
    {
        var capture = new CaptureSession("connection", 1024, Fixtures.Policy);
        capture.Append([1, 2], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()); capture.Stop(); capture.Stop();
        capture.Append([3, 4], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        var data = await capture.ReadAsync(0, new() { Mode = CompletionMode.Length, Length = 3 }, default);
        Assert.Equal("stopped", data.Completion); Assert.Equal("0102", data.Hex); Assert.True(capture.Describe().Stopped);
    }
    [Fact]
    public async Task CaptureReadCancellationReleasesItsSingleReaderSlot()
    {
        var capture = new CaptureSession("connection", 1024, Fixtures.Policy with { MinReadIntervalMs = 0 });
        using var cancel = new CancellationTokenSource(); var pending = capture.ReadAsync(0, new() { WaitMs = 10000 }, cancel.Token);
        Assert.Equal("READ_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => capture.ReadAsync(0, new(), default))).Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        capture.Append([1], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        Assert.Equal("01", (await capture.ReadAsync(0, new(), default)).Hex);
    }
    [Fact]
    public async Task ExpiredCapturesRefuseReadsAndStopCollecting()
    {
        var capture = new CaptureSession("connection", 1024, Fixtures.Policy with { CaptureLifetimeSeconds = 0 });
        capture.Append([1], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        Assert.True(capture.Stopped); Assert.Equal(0, capture.Buffer.End);
        Assert.Equal("EXPIRED_CAPTURE", (await Assert.ThrowsAsync<PortsterException>(() => capture.ReadAsync(0, new(), default))).Code);
    }
    [Fact]
    public async Task RetentionAndCapacityLimitsAreEnforcedIncludingStoppedCaptures()
    {
        var serial = new FakeSerial(); using var registry = new ConnectionRegistry(Fixtures.Policy with { MaxCaptures = 1 },
            new FakeCatalog(new PortInfo("COM7")), new FakeFactory(() => serial), new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        try
        {
            foreach (var size in new[] { 0, 1023, Fixtures.Policy.MaxCaptureBytes + 1 })
                Assert.Equal("CAPTURE_LIMIT", Assert.Throws<PortsterException>(() => registry.StartCapture(opened.ConnectionHandle, size, new("capture"))).Code);
            var capture = registry.StartCapture(opened.ConnectionHandle, 1024, new("capture"));
            registry.StopCapture(capture.CaptureHandle, new("stop"));
            Assert.Equal("CAPTURE_LIMIT", Assert.Throws<PortsterException>(() => registry.StartCapture(opened.ConnectionHandle, 1024, new("capture"))).Code);
            Assert.NotNull(registry.GetCapture(capture.CaptureHandle));
        }
        finally { await registry.CloseAsync(opened.ConnectionHandle, new("close"), default); }
    }
    [Fact]
    public async Task RegistryCaptureRemainsReadableAfterItsConnectionIsClosed()
    {
        var serial = new FakeSerial(); using var registry = new ConnectionRegistry(Fixtures.Policy,
            new FakeCatalog(new PortInfo("COM7")), new FakeFactory(() => serial), new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        var capture = registry.StartCapture(opened.ConnectionHandle, 1024, new("capture")); serial.Feed(1, 2);
        await Fixtures.Eventually(() => registry.GetCapture(capture.CaptureHandle).Buffer.End == 2);
        await registry.CloseAsync(opened.ConnectionHandle, new("close"), default);
        var read = await registry.ReadCaptureAsync(capture.CaptureHandle, 0, new(), new("read"), default);
        Assert.Equal("0102", read.Hex); Assert.Equal("closed", read.ConnectionState);
        Assert.Equal("INVALID_CAPTURE", Assert.Throws<PortsterException>(() => registry.GetCapture("unknown")).Code);
    }
}
