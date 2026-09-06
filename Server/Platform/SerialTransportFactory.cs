using System.IO.Ports;

namespace Portster.Platform;

public sealed class SerialTransportFactory : ISerialTransportFactory
{
    public ISerialTransport Open(string portName, SerialSettings settings, int writeTimeoutMs)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PortsterException(
                "UNSUPPORTED_PLATFORM",
                "This release supports Windows. The serial engine is shared; other discovery providers are not yet included.");
        }
        var port = new SerialPort(portName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.FlowControl,
            DtrEnable = settings.Dtr,
            ReadTimeout = 100,
            WriteTimeout = writeTimeoutMs,
            // Preserve bytes exactly, including NUL and parity-error bytes.
            DiscardNull = false,
            ParityReplace = 0
        };
        if (settings.FlowControl is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
        {
            port.RtsEnable = settings.Rts;
        }
        try
        {
            port.Open();
            return new SerialTransport(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }
}
