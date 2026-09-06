using System.Text;

namespace Portster;

public sealed record Payload
{
    public PayloadEncoding Encoding { get; init; } = PayloadEncoding.Hex;
    public string Data { get; init; } = "";

    public byte[] Decode(int maximum)
    {
        var encodedLimit = Math.Max(maximum * 3L, 4 * ((maximum + 2L) / 3));
        if (Data is null || Data.Length > encodedLimit || !Enum.IsDefined(Encoding))
        {
            throw new PortsterException("INVALID_PAYLOAD", "Payload encoding or size is invalid.");
        }
        try
        {
            byte[] bytes = Encoding switch
            {
                PayloadEncoding.Base64 => Convert.FromBase64String(Data),
                PayloadEncoding.Hex => Convert.FromHexString(Data),
                PayloadEncoding.Utf8 => new UTF8Encoding(false, true).GetBytes(Data),
                _ => throw new FormatException()
            };
            if (bytes.Length is 0 || bytes.Length > maximum)
            {
                throw new PortsterException("WRITE_LIMIT", $"Payload must contain 1..{maximum} bytes.");
            }
            return bytes;
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException)
        {
            throw new PortsterException(
                "INVALID_PAYLOAD",
                "Payload is not valid for its declared encoding (hex must have an even number of digits, without separators).");
        }
    }
}
