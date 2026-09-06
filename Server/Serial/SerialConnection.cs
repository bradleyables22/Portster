using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using Portster.Platform;

namespace Portster;

public sealed class SerialConnection : IAsyncDisposable
{
    private readonly ISerialTransport transport;
    private readonly ServerPolicy policy;
    private readonly IAuditLog audit;
    private readonly object stateLock = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly ConcurrentDictionary<string, CaptureSession> captures = new();
    private readonly Task receiveTask;
    private Task? disposalTask;
    private Task? pendingDriverCall;
    private string? endReason;
    private int activeOperations;
    private int readInProgress;
    private long lastActivityTimestamp = Stopwatch.GetTimestamp();
    private long nextReadTimestamp;
    private long nextWriteTimestamp;
    private long nextSignalReadTimestamp;

    public string Handle { get; } = Guid.NewGuid().ToString("N");
    public PortInfo Device { get; }
    public SerialSettings Settings { get; }
    public DeviceProfile? Profile { get; }
    public ReceiveBuffer Buffer { get; }

    public string? State
    {
        get
        {
            lock (stateLock)
            {
                return endReason;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (stateLock)
            {
                return disposalTask?.IsCompletedSuccessfully == true;
            }
        }
    }

    public Task Disposal
    {
        get
        {
            lock (stateLock)
            {
                return disposalTask ?? Task.CompletedTask;
            }
        }
    }

    public SerialConnection(ISerialTransport transport, PortInfo device, SerialSettings settings,
        DeviceProfile? profile, ServerPolicy policy, IAuditLog audit)
    {
        this.transport = transport;
        Device = device;
        Settings = settings;
        Profile = profile;
        this.policy = policy;
        this.audit = audit;
        Buffer = new(policy.ReceiveBufferBytes);
        receiveTask = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public OpenedConnection Describe() => new(Handle, Device, Settings, 0, Buffer.Capacity,
        policy.InactivityTimeoutSeconds, Profile?.Id, Profile?.Revision);

    public IDisposable Use(OperationContext operation, bool requireOpen = false)
    {
        lock (stateLock)
        {
            if (endReason is "closed" or "expired")
            {
                throw new PortsterException(
                    endReason == "expired" ? "EXPIRED_HANDLE" : "CLOSED_HANDLE",
                    "This connection has ended. Open a new connection to obtain a new handle.");
            }
            if (requireOpen && endReason is not null)
            {
                throw new PortsterException("DEVICE_DISCONNECTED", "The connection ended. Close it and explicitly reopen; reconnection is never automatic.");
            }
            activeOperations++;
            lastActivityTimestamp = Stopwatch.GetTimestamp();
            PopulateAuditContext(operation);
            return new OperationLease(() =>
            {
                lock (stateLock)
                {
                    activeOperations--;
                    lastActivityTimestamp = Stopwatch.GetTimestamp();
                }
            });
        }
    }

    private void PopulateAuditContext(OperationContext operation)
    {
        operation.ConnectionHandle = Handle;
        operation.Device = Device;
        operation.ProfileId = Profile?.Id;
        operation.ProfileRevision = Profile?.Revision;
    }

    private void ReceiveLoop()
    {
        var bytes = new byte[4096];
        try
        {
            while (!lifetimeCancellation.IsCancellationRequested)
            {
                try
                {
                    var count = transport.Read(bytes);
                    if (count == 0)
                    {
                        EndConnection("disconnected");
                        return;
                    }
                    lock (stateLock)
                    {
                        var utc = DateTimeOffset.UtcNow;
                        var stamp = Stopwatch.GetTimestamp();
                        Buffer.Append(bytes.AsSpan(0, count), utc, stamp);
                        foreach (var capture in captures.Values)
                        {
                            capture.Append(bytes.AsSpan(0, count), utc, stamp);
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Periodic read timeouts let the worker observe cancellation.
                }
            }
        }
        catch (SerialLineException)
        {
            EndConnection("serialError");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            EndConnection("disconnected");
        }
        catch
        {
            EndConnection("ioFault");
        }
    }

    private void EndConnection(string reason)
    {
        lock (stateLock)
        {
            if (endReason is null)
            {
                endReason = reason;
                Buffer.Complete(reason);
                foreach (var capture in captures.Values)
                {
                    capture.Stop(reason);
                }
                var operation = new OperationContext("connection_ended");
                PopulateAuditContext(operation);
                try
                {
                    audit.Record(operation, "event", reason);
                }
                catch (PortsterException)
                {
                    // The connection has already ended; later tool calls report audit failures.
                }
            }
            if (disposalTask?.IsCompleted != true)
            {
                lifetimeCancellation.Cancel();
            }
        }
    }

    public bool TryExpire()
    {
        lock (stateLock)
        {
            if (activeOperations != 0 || captures.Values.Any(c => !c.Stopped) || Stopwatch.GetElapsedTime(lastActivityTimestamp).TotalSeconds < policy.InactivityTimeoutSeconds)
            {
                return false;
            }
            EndConnection("expired");
            // Expiry must remain distinguishable even if an earlier unplug ended reception.
            endReason = "expired";
            return true;
        }
    }

    public async Task<ReadData> ReadAsync(long cursor, ReadOptions? options, OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation);
        operation.ReceiveCursor = cursor;
        operation.ReadOptions = options ?? Profile?.Framing ?? new();
        if (Interlocked.CompareExchange(ref readInProgress, 1, 0) != 0)
        {
            throw new PortsterException("READ_BUSY", "Only one serial_read may wait on this connection at a time; use capture for a separate observer.");
        }
        try
        {
            RateLimit(ref nextReadTimestamp, policy.MinReadIntervalMs);
            return await BufferReader.ReadAsync(Buffer, cursor, options ?? Profile?.Framing ?? new(), policy, token);
        }
        finally
        {
            Volatile.Write(ref readInProgress, 0);
        }
    }

    public async Task<WriteData> WriteAsync(Payload payload, OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation, requireOpen: true);
        var bytes = ValidateWrite(payload);
        await EnterMutationAsync(token);
        try
        {
            return await WriteCoreAsync(bytes, operation, token);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<TransactionData> TransactAsync(Payload payload, ReadOptions? options, OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation, requireOpen: true);
        var bytes = ValidateWrite(payload);
        var framing = options ?? Profile?.Framing ?? new();
        framing.Validate(policy);
        await EnterMutationAsync(token);
        try
        {
            var cursor = Buffer.End;
            operation.ReceiveCursor = cursor;
            operation.ReadOptions = framing;
            var written = await WriteCoreAsync(bytes, operation, token);
            var read = await BufferReader.ReadAsync(Buffer, cursor, framing, policy, token);
            return new(written, read);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private byte[] ValidateWrite(Payload payload)
    {
        if (!policy.AllowWrites || Profile?.Safety.AllowWrites == false)
        {
            throw new PortsterException("WRITE_NOT_ALLOWED", "Writes are disabled by server or profile policy.");
        }
        if (payload is null)
        {
            throw new PortsterException("INVALID_PAYLOAD", "A payload object is required.");
        }
        return payload.Decode(Math.Min(policy.MaxWriteBytes, Profile?.Safety.MaxWriteBytes ?? policy.MaxWriteBytes));
    }

    private async Task EnterMutationAsync(CancellationToken token)
    {
        if (!await mutationLock.WaitAsync(0, token))
        {
            throw new PortsterException("CONNECTION_BUSY", "Another write, transaction, or signal operation is active. No operation was queued.");
        }
        if (State is not null)
        {
            mutationLock.Release();
            throw new PortsterException("DEVICE_DISCONNECTED", "The connection ended before the operation started.");
        }
    }

    private async Task<WriteData> WriteCoreAsync(byte[] bytes, OperationContext operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RateLimit(ref nextWriteTimestamp, Math.Max(policy.MinWriteIntervalMs, Profile?.Safety.MinWriteIntervalMs ?? 0));
        operation.PayloadBase64 = policy.AuditPayloads ? Convert.ToBase64String(bytes) : null;
        operation.BytesRequested = bytes.Length;
        audit.Record(operation, "writeIntent", "pending"); // durable intent, before any byte is submitted
        token.ThrowIfCancellationRequested();
        var task = StartDriverCall(() =>
        {
            transport.Write(bytes);
            return true;
        }, operation);
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(policy.WriteTimeoutMs + 250), token);
            operation.BytesSubmitted = bytes.Length;
            operation.WriteDisposition = "submittedToDriver";
            return new(bytes.Length, operation.WriteDisposition);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Retire the handle: a timed-out driver call must never overlap a later write.
            EndConnection("ioFault");
            _ = DisposeAsync();
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new PortsterException(ex is OperationCanceledException ? "WRITE_CANCELLED" : "WRITE_OUTCOME_UNKNOWN",
                "A write was attempted, but its outcome is unknown. The connection was retired. Do not retry automatically.", "attemptedOutcomeUnknown");
        }
    }

    public async Task<SignalData> GetSignalsAsync(OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation, true);
        await EnterMutationAsync(token);
        try
        {
            RateLimit(ref nextSignalReadTimestamp, policy.MinReadIntervalMs);
            return await RunDriverCallAsync(transport.GetSignals, token);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<Done> SetSignalsAsync(bool? dtr, bool? rts, OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation, true);
        DemandSignals();
        if (dtr is null && rts is null)
        {
            throw new PortsterException("INVALID_SIGNALS", "Provide dtr, rts, or both.");
        }
        if (rts.HasValue && Settings.FlowControl is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
        {
            throw new PortsterException("SIGNAL_CONFLICT", "RTS is managed by hardware flow control.");
        }
        await EnterMutationAsync(token);
        try
        {
            operation.Dtr = dtr;
            operation.Rts = rts;
            RateLimit(ref nextWriteTimestamp, Math.Max(policy.MinWriteIntervalMs, Profile?.Safety.MinWriteIntervalMs ?? 0));
            audit.Record(operation, "signalIntent", "pending");
            return await RunDriverCallAsync(() =>
            {
                transport.SetSignals(dtr, rts);
                return new Done();
            }, token);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<Done> SendBreakAsync(int durationMs, OperationContext operation, CancellationToken token)
    {
        using var lease = Use(operation, true);
        DemandSignals();
        if (durationMs is < 1 or > 1000)
        {
            throw new PortsterException("BREAK_LIMIT", "Break duration must be 1..1000 milliseconds.");
        }
        await EnterMutationAsync(token);
        try
        {
            operation.BreakDurationMs = durationMs;
            RateLimit(ref nextWriteTimestamp, Math.Max(policy.MinWriteIntervalMs, Profile?.Safety.MinWriteIntervalMs ?? 0));
            audit.Record(operation, "breakIntent", "pending");
            await RunDriverCallAsync(() =>
            {
                transport.SetBreak(true);
                return new Done();
            }, token);
            try
            {
                await Task.Delay(durationMs, token);
            }
            finally
            {
                await RunDriverCallAsync(() =>
                {
                    transport.SetBreak(false);
                    return new Done();
                }, CancellationToken.None);
            }
            return new();
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private void DemandSignals()
    {
        if (!policy.AllowSignals || Profile?.Safety.AllowSignals == false)
        {
            throw new PortsterException("SIGNALS_NOT_ALLOWED", "Signal changes and break require operator permission and, when present, profile permission.");
        }
    }

    private async Task<T> RunDriverCallAsync<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = StartDriverCall(action);
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(policy.WriteTimeoutMs), token);
        }
        catch
        {
            EndConnection("ioFault");
            _ = DisposeAsync();
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new PortsterException(
                "DRIVER_OPERATION_UNCERTAIN",
                "The driver operation did not complete normally. The handle was retired; control signals may have changed.");
        }
    }

    private Task<T> StartDriverCall<T>(Func<T> action, OperationContext? writeOperation = null)
    {
        lock (stateLock)
        {
            if (endReason is not null)
            {
                throw new PortsterException("CONNECTION_ENDED", "The connection ended before the driver call could start.");
            }
            if (writeOperation is not null)
            {
                writeOperation.WriteDisposition = "attemptedOutcomeUnknown";
            }
            var task = Task.Run(action, CancellationToken.None);
            pendingDriverCall = task;
            return task;
        }
    }

    internal static void RateLimit(ref long next, int intervalMs)
    {
        var now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref next))
        {
            throw new PortsterException("RATE_LIMITED", "Calls are too frequent for this connection or capture. Wait before retrying.");
        }
        Volatile.Write(ref next, now + (long)(intervalMs * (double)Stopwatch.Frequency / 1000));
    }

    public CaptureSession StartCapture(int capacity)
    {
        lock (stateLock)
        {
            if (endReason is not null)
            {
                throw new PortsterException("DEVICE_DISCONNECTED", "Cannot start a capture on an ended connection.");
            }
            var capture = new CaptureSession(Handle, capacity, policy);
            captures[capture.Handle] = capture;
            return capture;
        }
    }

    public void RemoveCapture(string handle)
    {
        lock (stateLock)
        {
            if (captures.TryRemove(handle, out var capture))
            {
                capture.Stop("stopped");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (stateLock)
        {
            if (disposalTask is not null)
            {
                return new(disposalTask);
            }
            EndConnection("closed");
            var pendingDriver = pendingDriverCall;
            // Do not run a potentially slow native close under the lifecycle lock.
            disposalTask = Task.Run(async () =>
            {
                try
                {
                    transport.Dispose();
                }
                finally
                {
                    await receiveTask;
                    // Closing a driver handle does not prove an in-flight native call has
                    // returned. Keep the port reservation until both have really ended.
                    if (pendingDriver is not null)
                    {
                        try
                        {
                            await pendingDriver;
                        }
                        catch
                        {
                        }
                    }
                }
            });
            return new(disposalTask);
        }
    }
}
