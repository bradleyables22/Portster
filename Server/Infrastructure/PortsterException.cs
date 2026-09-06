namespace Portster;

public sealed class PortsterException(string code, string message, string writeDisposition = "notAttempted") : Exception(message)
{
    public string Code { get; } = code;
    public string WriteDisposition { get; } = writeDisposition;
}
