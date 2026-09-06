namespace Portster.Platform;

public interface ISerialTransport : IDisposable
{
    int Read(byte[] buffer);
    void Write(byte[] data);
    SignalData GetSignals();
    void SetSignals(bool? dtr, bool? rts);
    void SetBreak(bool enabled);
}
