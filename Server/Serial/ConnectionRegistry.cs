using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portster.Platform;

namespace Portster;

public sealed class ConnectionRegistry(ServerPolicy policy, IPortCatalog catalog, ISerialTransportFactory factory,
    IAuditLog audit, ILogger<ConnectionRegistry> logger) : BackgroundService
{
    private readonly object registryLock = new();
    private readonly Dictionary<string, SerialConnection> connectionsByHandle = [];
    private readonly Dictionary<string, SerialConnection?> portReservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CaptureSession> capturesByHandle = [];
    private readonly Dictionary<string, string> retiredHandles = [];
    private bool isStopping;

    public Task<IReadOnlyList<PortInfo>> ListAsync(CancellationToken token) => catalog.ListAsync(token);

    public SerialConnection Get(string handle)
    {
        lock (registryLock)
        {
            if (handle is not null && connectionsByHandle.TryGetValue(handle, out var connection))
            {
                return connection;
            }
            if (handle is not null && retiredHandles.TryGetValue(handle, out var code))
            {
                throw new PortsterException(code, "This handle has ended. Open a new connection; handles cannot be reused.");
            }
            throw new PortsterException("INVALID_HANDLE", "Unknown connection handle for this server process.");
        }
    }

    public async Task<OpenedConnection> OpenAsync(string portName, SerialSettings settings, DeviceProfile? profile,
        OperationContext operation, CancellationToken token)
    {
        policy.DemandPort(portName);
        if (settings is null)
        {
            throw new PortsterException("INVALID_SETTINGS", "Explicit serial settings are required.");
        }
        settings.Validate();
        if (settings.FlowControl is System.IO.Ports.Handshake.XOnXOff or System.IO.Ports.Handshake.RequestToSendXOnXOff &&
            (!policy.AllowWrites || profile?.Safety.AllowWrites == false))
        {
            throw new PortsterException("WRITE_NOT_ALLOWED", "Software flow control can transmit control bytes and requires write permission.");
        }
        if ((settings.Dtr || settings.Rts || settings.FlowControl != System.IO.Ports.Handshake.None) &&
            (!policy.AllowSignals || profile?.Safety.AllowSignals == false))
        {
            throw new PortsterException(
                "SIGNALS_NOT_ALLOWED",
                "Opening with asserted DTR/RTS or flow control requires signal permission. Opening can still change electrical line state on some adapters.");
        }
        var devices = await catalog.ListAsync(token);
        var device = devices.FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))
            ?? throw new PortsterException("DEVICE_NOT_FOUND", "The requested port is not currently present.");
        // Resolve again immediately before open; a changed profile match cannot silently select another port.
        if (profile is not null && ProfileStore.Resolve(profile.Selector, devices).PortName != device.PortName)
        {
            throw new PortsterException("DEVICE_CHANGED", "The profile now resolves to another port. Refresh discovery before retrying.");
        }
        operation.Device = device;
        operation.ProfileId = profile?.Id;
        operation.ProfileRevision = profile?.Revision;
        operation.UartSettings = settings;
        lock (registryLock)
        {
            if (isStopping)
            {
                throw new PortsterException("SERVER_STOPPING", "The server is shutting down.");
            }
            if (portReservations.ContainsKey(device.PortName))
            {
                throw new PortsterException("PORT_BUSY", "This port is already open, opening, or closing in this server.");
            }
            if (portReservations.Count >= policy.MaxConnections)
            {
                throw new PortsterException("CONNECTION_LIMIT", "The configured connection limit has been reached.");
            }
            portReservations.Add(device.PortName, null);
        }
        Task<ISerialTransport>? opening = null;
        ISerialTransport? transport = null;
        try
        {
            audit.Record(operation, "openIntent", "pending");
            token.ThrowIfCancellationRequested();
            opening = Task.Run(() => factory.Open(device.PortName, settings, policy.WriteTimeoutMs), CancellationToken.None);
            transport = await opening.WaitAsync(TimeSpan.FromMilliseconds(policy.MaxWaitMs), token);
            token.ThrowIfCancellationRequested();
            lock (registryLock)
            {
                if (isStopping)
                {
                    throw new PortsterException("SERVER_STOPPING", "The server is shutting down.");
                }
                var connection = new SerialConnection(transport, device, settings, profile, policy, audit);
                portReservations[device.PortName] = connection;
                connectionsByHandle.Add(connection.Handle, connection);
                operation.ConnectionHandle = connection.Handle;
                return connection.Describe();
            }
        }
        catch (Exception ex)
        {
            // Preserve the reservation until the native open and cleanup really finish.
            // Repeated cancellations cannot accumulate opens against the same device.
            _ = CleanupOpenAsync(opening, transport, device.PortName);
            if (ex is TimeoutException)
            {
                throw new PortsterException(
                    "OPEN_TIMEOUT",
                    "The driver open timed out. The port stays reserved until cleanup completes; opening may have changed control lines.");
            }
            if (ex is UnauthorizedAccessException)
            {
                throw new PortsterException("PORT_BUSY_OR_DENIED", "Another application may own the port, or Windows denied access.");
            }
            throw;
        }
    }

    private async Task CleanupOpenAsync(Task<ISerialTransport>? opening, ISerialTransport? transport, string port)
    {
        try
        {
            if (transport is null && opening is not null)
            {
                transport = await opening;
            }
            if (transport is not null)
            {
                await Task.Run(transport.Dispose);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cleanup after opening {Port}", port);
        }
        finally
        {
            lock (registryLock)
            {
                portReservations.Remove(port);
            }
        }
    }

    public async Task<OpenedConnection> OpenProfileAsync(DeviceProfile profile, OperationContext operation, CancellationToken token)
    {
        var selected = ProfileStore.Resolve(profile.Selector, await catalog.ListAsync(token));
        return await OpenAsync(selected.PortName, profile.Settings, profile, operation, token);
    }

    public async Task<Done> CloseAsync(string handle, OperationContext operation, CancellationToken token)
    {
        SerialConnection connection;
        lock (registryLock)
        {
            if (handle is not null && retiredHandles.TryGetValue(handle, out var code) && code == "CLOSED_HANDLE")
            {
                return new();
            }
            connection = Get(handle!);
            operation.ConnectionHandle = connection.Handle;
            operation.Device = connection.Device;
            operation.ProfileId = connection.Profile?.Id;
            operation.ProfileRevision = connection.Profile?.Revision;
        }
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
        }
        catch (TimeoutException)
        {
            throw new PortsterException(
                "CLOSE_PENDING",
                "Native driver cleanup is still pending. The port remains reserved and this handle cannot perform further writes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PortsterException(
                "CLOSE_FAILED",
                "Native driver cleanup failed. The port remains reserved; restart the server before attempting to reuse it.");
        }
        lock (registryLock)
        {
            Retire(connection, "CLOSED_HANDLE");
        }
        return new();
    }

    private void Retire(SerialConnection connection, string code)
    {
        connectionsByHandle.Remove(connection.Handle);
        portReservations.Remove(connection.Device.PortName);
        retiredHandles[connection.Handle] = code;
        while (retiredHandles.Count > 256)
        {
            retiredHandles.Remove(retiredHandles.Keys.First());
        }
    }

    public CaptureInfo StartCapture(string handle, int? capacityBytes, OperationContext operation)
    {
        var capacity = capacityBytes ?? policy.MaxCaptureBytes;
        if (capacity < 1024 || capacity > policy.MaxCaptureBytes)
        {
            throw new PortsterException("CAPTURE_LIMIT", $"Capture capacity must be 1024..{policy.MaxCaptureBytes} bytes.");
        }
        lock (registryLock)
        {
            var connection = Get(handle);
            using var lease = connection.Use(operation, true);
            if (capturesByHandle.Count >= policy.MaxCaptures)
            {
                throw new PortsterException("CAPTURE_LIMIT", "Capture retention slots are full. Stopped captures remain readable until their expiry.");
            }
            var capture = connection.StartCapture(capacity);
            capturesByHandle[capture.Handle] = capture;
            operation.CaptureHandle = capture.Handle;
            return capture.Describe();
        }
    }

    public CaptureSession GetCapture(string handle)
    {
        lock (registryLock)
        {
            if (handle is null || !capturesByHandle.TryGetValue(handle, out var capture))
            {
                throw new PortsterException("INVALID_CAPTURE", "Unknown or expired capture handle.");
            }
            if (capture.Expired)
            {
                throw new PortsterException("EXPIRED_CAPTURE", "Capture retention expired.");
            }
            return capture;
        }
    }

    public async Task<ReadData> ReadCaptureAsync(string handle, long cursor, ReadOptions? options, OperationContext operation, CancellationToken token)
    {
        var capture = GetCapture(handle);
        operation.ConnectionHandle = capture.ConnectionHandle;
        operation.CaptureHandle = capture.Handle;
        operation.ReceiveCursor = cursor;
        operation.ReadOptions = options ?? new();
        lock (registryLock)
        {
            if (connectionsByHandle.TryGetValue(capture.ConnectionHandle, out var connection))
            {
                operation.Device = connection.Device;
                operation.ProfileId = connection.Profile?.Id;
                operation.ProfileRevision = connection.Profile?.Revision;
            }
        }
        return await capture.ReadAsync(cursor, options, token);
    }

    public CaptureInfo StopCapture(string handle, OperationContext operation)
    {
        lock (registryLock)
        {
            var capture = GetCapture(handle);
            operation.ConnectionHandle = capture.ConnectionHandle;
            operation.CaptureHandle = capture.Handle;
            capture.Stop();
            if (connectionsByHandle.TryGetValue(capture.ConnectionHandle, out var connection))
            {
                connection.RemoveCapture(handle);
            }
            return capture.Describe();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (registryLock)
                {
                    foreach (var capture in capturesByHandle.Values.Where(c => c.Expired).ToArray())
                    {
                        capture.Stop("expired");
                        capturesByHandle.Remove(capture.Handle);
                        if (connectionsByHandle.TryGetValue(capture.ConnectionHandle, out var owner))
                        {
                            owner.RemoveCapture(capture.Handle);
                        }
                    }
                    foreach (var connection in connectionsByHandle.Values.ToArray())
                    {
                        if (connection.IsDisposed)
                        {
                            Retire(connection, connection.State == "expired" ? "EXPIRED_HANDLE" : "CLOSED_HANDLE");
                            continue;
                        }
                        if (connection.TryExpire())
                        {
                            _ = connection.DisposeAsync();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        SerialConnection[] remaining;
        lock (registryLock)
        {
            isStopping = true;
            remaining = connectionsByHandle.Values.ToArray();
        }
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(remaining.Select(c => c.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Some native drivers did not complete shutdown cleanup within five seconds");
        }
    }
}
