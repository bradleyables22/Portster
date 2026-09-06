using System.Diagnostics;
using Portster;

namespace Portster.Tests;

public class BufferTests
{
    private static void Feed(ReceiveBuffer buffer, byte[] data) => buffer.Append(data, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    [Fact]
    public async Task ReadsAreNonDestructiveAndOverrunsAreCountedExactly()
    {
        var buffer = new ReceiveBuffer(4);
        Feed(buffer, [0, 1, 2]);
        var first = await BufferReader.ReadAsync(buffer, 0, new() { WaitMs = 0 }, Fixtures.Policy, default);
        var replay = await BufferReader.ReadAsync(buffer, 0, new() { WaitMs = 0 }, Fixtures.Policy, default);
        Assert.Equal(first, replay);
        Feed(buffer, [3, 4, 5]);
        var overrun = await BufferReader.ReadAsync(buffer, 0, new() { WaitMs = 0 }, Fixtures.Policy, default);
        Assert.Equal("overrun", overrun.Completion);
        Assert.Equal(2, overrun.LostBytes); Assert.Equal(2, overrun.StartCursor);
        Assert.Equal("02030405", overrun.Hex); Assert.Equal(6, overrun.NextCursor);
    }
    [Fact]
    public async Task OversizedAppendAndInvalidCursorDoNotCorruptOffsets()
    {
        var buffer = new ReceiveBuffer(3); Feed(buffer, [0, 1, 2, 3, 4, 5, 6, 7]);
        var result = await BufferReader.ReadAsync(buffer, 0, new() { WaitMs = 0 }, Fixtures.Policy, default);
        Assert.Equal("050607", result.Hex); Assert.Equal(5, result.LostBytes);
        Assert.Equal("INVALID_CURSOR", Assert.Throws<PortsterException>(() => buffer.Snapshot(9, 10)).Code);
    }
    [Fact]
    public async Task SplitDelimiterReturnsOnlyOneFrameAndLeavesTrailingData()
    {
        var buffer = new ReceiveBuffer(32);
        var read = BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.Delimiter, DelimiterHex = "0D0A" }, Fixtures.Policy, default);
        Feed(buffer, [65, 13]); Assert.False(read.IsCompleted);
        Feed(buffer, [10, 66, 13, 10]);
        var result = await read;
        Assert.Equal("410D0A", result.Hex); Assert.Equal("delimiter", result.Completion);
        var trailing = await BufferReader.ReadAsync(buffer, result.NextCursor, new() { WaitMs = 0 }, Fixtures.Policy, default);
        Assert.Equal("420D0A", trailing.Hex);
    }
    [Fact]
    public async Task PartialTimeoutAndDisconnectKeepBytes()
    {
        var buffer = new ReceiveBuffer(32); Feed(buffer, [1, 2]);
        var options = new ReadOptions { Mode = CompletionMode.Length, Length = 4, WaitMs = 20 };
        var timeout = await BufferReader.ReadAsync(buffer, 0, options, Fixtures.Policy, default);
        Assert.Equal("timeout", timeout.Completion); Assert.Equal(2, timeout.ByteCount);
        buffer.Complete("disconnected");
        var disconnected = await BufferReader.ReadAsync(buffer, 0, options, Fixtures.Policy, default);
        Assert.Equal("disconnected", disconnected.Completion); Assert.Equal(timeout.Hex, disconnected.Hex);
    }
    [Fact]
    public async Task CancellationInterruptsAnEmptyWait()
    {
        using var cancel = new CancellationTokenSource();
        var task = BufferReader.ReadAsync(new(32), 0, new() { WaitMs = 10000 }, Fixtures.Policy, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
    [Fact]
    public async Task IdleGapUsesMonotonicReceiptTime()
    {
        var buffer = new ReceiveBuffer(32); Feed(buffer, [27, 0, 65]);
        var result = await BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.IdleGap, IdleGapMs = 20 }, Fixtures.Policy, default);
        Assert.Equal("idleGap", result.Completion); Assert.Equal("..A", result.TextPreview);
        Assert.NotNull(result.FirstReceivedUtc); Assert.NotNull(result.LastReceivedUtc);
    }
    [Theory]
    [InlineData("ABC", PayloadEncoding.Hex)]
    [InlineData("GG", PayloadEncoding.Hex)]
    [InlineData("%%%", PayloadEncoding.Base64)]
    public void MalformedPayloadsAreRejected(string value, PayloadEncoding encoding) =>
        Assert.Equal("INVALID_PAYLOAD", Assert.Throws<PortsterException>(() => new Payload { Data = value, Encoding = encoding }.Decode(16)).Code);
    [Fact]
    public void FramingAndWriteLimitsAreValidatedBeforeIo()
    {
        Assert.Throws<PortsterException>(() => new ReadOptions { WaitMs = -1 }.Validate(Fixtures.Policy));
        Assert.Throws<PortsterException>(() => new ReadOptions { Mode = CompletionMode.Length, Length = 5000, MaxBytes = 4 }.Validate(Fixtures.Policy));
        Assert.Throws<PortsterException>(() => new Payload { Encoding = PayloadEncoding.Utf8, Data = "éé" }.Decode(3));
    }
}
