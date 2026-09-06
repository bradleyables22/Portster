using System.Diagnostics;

namespace Portster.Tests;

public class BufferPropertyTests
{
    [Fact]
    public void RandomizedRingOperationsAgreeWithAnUnboundedReferenceLog()
    {
        var random = new Random(740219);
        foreach (var capacity in new[] { 1, 2, 7, 31, 256 })
        {
            var buffer = new ReceiveBuffer(capacity); var reference = new List<byte>();
            for (var step = 0; step < 1000; step++)
            {
                var chunk = new byte[random.Next(capacity * 3 + 1)]; random.NextBytes(chunk);
                buffer.Append(chunk, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()); reference.AddRange(chunk);
                var cursor = random.Next(reference.Count + 1); var count = random.Next(1, capacity * 2 + 1);
                var earliest = Math.Max(0, reference.Count - capacity); var start = Math.Max(cursor, earliest);
                var actual = buffer.Snapshot(cursor, count);
                Assert.Equal(reference.Skip(start).Take(count).ToArray(), actual.Bytes);
                Assert.Equal(earliest, actual.Earliest); Assert.Equal(reference.Count, actual.Latest);
                Assert.Equal(start, actual.Start); Assert.Equal(start - cursor, actual.Lost);
            }
        }
    }
    [Fact]
    public async Task EveryChunkBoundaryOfAnOverlappingDelimiterFindsTheSameFrame()
    {
        byte[] bytes = [65, 66, 65, 66, 65, 67, 99, 100];
        for (var split = 0; split <= bytes.Length; split++)
        {
            var buffer = new ReceiveBuffer(32);
            var read = BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.Delimiter, DelimiterHex = "41424143" }, Fixtures.Policy, default);
            buffer.Append(bytes.AsSpan(0, split), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
            await Task.Yield();
            buffer.Append(bytes.AsSpan(split), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
            var frame = await read;
            Assert.Equal("414241424143", frame.Hex); Assert.Equal(6, frame.NextCursor);
        }
    }
    [Fact]
    public async Task ExactFrameBoundaryWinsOverTheResponseSizeLimit()
    {
        var buffer = new ReceiveBuffer(16);
        buffer.Append([65, 10, 66, 10], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        var frame = await BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.Delimiter, DelimiterHex = "0A", MaxBytes = 2 }, Fixtures.Policy, default);
        Assert.Equal("delimiter", frame.Completion); Assert.Equal("410A", frame.Hex);
        var limited = await BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.Delimiter, DelimiterHex = "FFFF", MaxBytes = 2 }, Fixtures.Policy, default);
        Assert.Equal("maxBytes", limited.Completion); Assert.Equal("410A", limited.Hex);
    }
    [Fact]
    public async Task ATruncatedBufferCannotBeMistakenForAnIdleGap()
    {
        var buffer = new ReceiveBuffer(16);
        buffer.Append([1, 2, 3, 4], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        var result = await BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.IdleGap, IdleGapMs = 20, MaxBytes = 2 }, Fixtures.Policy, default);
        Assert.Equal("maxBytes", result.Completion); Assert.Equal(2, result.ByteCount);
    }
    [Fact]
    public async Task FrameTimestampsExcludeTrailingDataAndIgnoreWallClockReversal()
    {
        var buffer = new ReceiveBuffer(16); var time = DateTimeOffset.UtcNow;
        buffer.Append([65, 10], time, Stopwatch.GetTimestamp());
        buffer.Append([66, 10], time.AddHours(-1), Stopwatch.GetTimestamp());
        var frame = await BufferReader.ReadAsync(buffer, 0, new() { Mode = CompletionMode.Length, Length = 2 }, Fixtures.Policy, default);
        Assert.Equal(time, frame.FirstReceivedUtc); Assert.Equal(time, frame.LastReceivedUtc);
    }
    [Fact]
    public async Task CompletionWakesPendingReadersAndRejectsFurtherAppends()
    {
        var buffer = new ReceiveBuffer(8);
        var waiting = BufferReader.ReadAsync(buffer, 0, new() { WaitMs = 10000 }, Fixtures.Policy, default);
        buffer.Complete("stopped");
        buffer.Append([1, 2], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("stopped", result.Completion); Assert.Equal(0, result.ByteCount); Assert.Equal(0, buffer.End);
        buffer.Complete("different"); Assert.Equal("stopped", buffer.Snapshot(0, 1).EndState);
    }
    [Fact]
    public async Task ConcurrentAppendAndSnapshotNeverProduceTornBytes()
    {
        var buffer = new ReceiveBuffer(257);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20000; i++) buffer.Append([(byte)(i % 251)], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        });
        for (var i = 0; i < 2000; i++)
        {
            var snapshot = buffer.Snapshot(0, 127);
            Assert.Equal(Enumerable.Range(0, snapshot.Bytes.Length).Select(n => (byte)((snapshot.Start + n) % 251)), snapshot.Bytes);
            await Task.Yield();
        }
        await writer;
    }
}
