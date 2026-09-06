using System.IO.Ports;

namespace Portster;

public sealed record SerialSettings
{
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake FlowControl { get; init; } = Handshake.None;
    public bool Dtr { get; init; }
    public bool Rts { get; init; }

    public void Validate()
    {
        if (BaudRate is < 50 or > 4_000_000 || DataBits is < 5 or > 8 || !Enum.IsDefined(Parity) ||
            !Enum.IsDefined(StopBits) || StopBits == StopBits.None || !Enum.IsDefined(FlowControl))
        {
            throw new PortsterException(
                "INVALID_SETTINGS",
                "Unsupported UART settings: baud 50..4000000, data bits 5..8, and defined parity/stop/flow values are required.");
        }
        if (Rts && FlowControl is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
        {
            throw new PortsterException("INVALID_SETTINGS", "RTS cannot be controlled manually while RTS/CTS flow control is enabled.");
        }
    }
}
