namespace Portster.Platform;

public interface ISerialTransportFactory
{
    ISerialTransport Open(string portName, SerialSettings settings, int writeTimeoutMs);
}
