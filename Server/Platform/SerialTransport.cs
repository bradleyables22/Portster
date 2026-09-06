using System.IO.Ports;

namespace Portster.Platform;

internal sealed class SerialTransport : ISerialTransport
{
    private readonly SerialPort port;
    private int pendingErrors;

    public SerialTransport(SerialPort port)
    {
        this.port = port;
        port.ErrorReceived += OnError;
    }

    private void OnError(object sender, SerialErrorReceivedEventArgs args) => Interlocked.Or(ref pendingErrors, (int)args.EventType);

    // Synchronous reads use SerialPort.ReadTimeout. BaseStream async cancellation/timeout
    // behavior differs across drivers; a bounded receive worker is intentional here.
    public int Read(byte[] buffer)
    {
        var error = Interlocked.Exchange(ref pendingErrors, 0);
        if (error != 0)
        {
            throw new SerialLineException($"Serial driver reported line/buffer error flags: {error}.");
        }
        return port.Read(buffer, 0, buffer.Length);
    }

    public void Write(byte[] data) => port.Write(data, 0, data.Length);

    public SignalData GetSignals() => new(port.CtsHolding, port.DsrHolding, port.CDHolding,
        port.DtrEnable, port.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff ? null : port.RtsEnable, port.BreakState,
        port.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff);

    public void SetSignals(bool? dtr, bool? rts)
    {
        if (rts.HasValue && port.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
        {
            throw new PortsterException("SIGNAL_CONFLICT", "RTS is managed by hardware flow control.");
        }
        if (dtr.HasValue)
        {
            port.DtrEnable = dtr.Value;
        }
        if (rts.HasValue)
        {
            port.RtsEnable = rts.Value;
        }
    }

    public void SetBreak(bool enabled) => port.BreakState = enabled;

    public void Dispose()
    {
        port.ErrorReceived -= OnError;
        port.Dispose();
    }
}
