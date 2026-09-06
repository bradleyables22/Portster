using System.IO.Ports;
using System.Text;

namespace Portster.Tests;

public class ValidationTests
{
    [Fact]
    public void BinaryEncodingsRoundTripAtEverySmallWriteBoundary()
    {
        for (var size = 1; size <= 32; size++)
        {
            var bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
            Assert.Equal(bytes, new Payload { Encoding = PayloadEncoding.Base64, Data = Convert.ToBase64String(bytes) }.Decode(size));
            Assert.Equal(bytes, new Payload { Encoding = PayloadEncoding.Hex, Data = Convert.ToHexString(bytes) }.Decode(size));
        }
    }
    [Fact]
    public void Utf8LimitUsesBytesAndRejectsUnpairedSurrogates()
    {
        var payload = new Payload { Encoding = PayloadEncoding.Utf8, Data = "Aé😀" };
        Assert.Equal(Encoding.UTF8.GetBytes(payload.Data), payload.Decode(7));
        Assert.Equal("WRITE_LIMIT", Assert.Throws<PortsterException>(() => payload.Decode(6)).Code);
        Assert.Equal("INVALID_PAYLOAD", Assert.Throws<PortsterException>(() => (payload with { Data = "\uD800" }).Decode(8)).Code);
    }
    [Theory]
    [InlineData(PayloadEncoding.Base64)]
    [InlineData(PayloadEncoding.Hex)]
    [InlineData(PayloadEncoding.Utf8)]
    public void EmptyPayloadsCannotBeSubmitted(PayloadEncoding encoding) =>
        Assert.Equal("WRITE_LIMIT", Assert.Throws<PortsterException>(() => new Payload { Encoding = encoding }.Decode(32)).Code);

    [Fact]
    public void NullOversizedAndUnknownPayloadsHaveActionableErrors()
    {
        foreach (var payload in new[] { new Payload { Data = null! }, new() { Data = new string('A', 100) }, new() { Encoding = (PayloadEncoding)999, Data = "AA" } })
            Assert.Equal("INVALID_PAYLOAD", Assert.Throws<PortsterException>(() => payload.Decode(8)).Code);
    }
    [Fact]
    public void PortNamesRequireAnExactMatchIncludingTrailingNewlines()
    {
        foreach (var value in new[] { "COM0", "COM01", " COM7", "COM7 ", "COM7\n", "COM7\r\n", "COM7\0", "/dev/ttyUSB0", "\\\\.\\COM7", "COM1000000" })
            Assert.False(ServerPolicy.IsPortName(value), $"Accepted {System.Text.Json.JsonSerializer.Serialize(value)}");
        Assert.True(ServerPolicy.IsPortName("com7")); Assert.True(ServerPolicy.IsPortName("COM999999"));
        Assert.False(ServerPolicy.IsPortName(null));
    }
    public static IEnumerable<object[]> InvalidSettings()
    {
        yield return ["baud below supported range", new SerialSettings { BaudRate = 49 }];
        yield return ["baud above supported range", new SerialSettings { BaudRate = 4_000_001 }];
        yield return ["too few data bits", new SerialSettings { DataBits = 4 }];
        yield return ["too many data bits", new SerialSettings { DataBits = 9 }];
        yield return ["unknown parity", new SerialSettings { Parity = (Parity)999 }];
        yield return ["no stop bit", new SerialSettings { StopBits = StopBits.None }];
        yield return ["unknown stop bits", new SerialSettings { StopBits = (StopBits)999 }];
        yield return ["unknown flow control", new SerialSettings { FlowControl = (Handshake)999 }];
        yield return ["manual RTS with hardware flow", new SerialSettings { Rts = true, FlowControl = Handshake.RequestToSend }];
        yield return ["manual RTS with combined flow", new SerialSettings { Rts = true, FlowControl = Handshake.RequestToSendXOnXOff }];
    }
    [Theory, MemberData(nameof(InvalidSettings))]
    public void InvalidUartCombinationsAreRejected(string scenario, SerialSettings settings)
    { Assert.NotEmpty(scenario); Assert.Equal("INVALID_SETTINGS", Assert.Throws<PortsterException>(settings.Validate).Code); }
    [Fact]
    public void SupportedUartBoundariesAndFlowModesAreAccepted()
    {
        foreach (var mode in Enum.GetValues<Handshake>())
            new SerialSettings { BaudRate = 50, DataBits = 5, StopBits = StopBits.OnePointFive, FlowControl = mode }.Validate();
        new SerialSettings { BaudRate = 4_000_000, DataBits = 8, StopBits = StopBits.Two, Parity = Parity.Mark }.Validate();
    }
    [Fact]
    public void DelimitersMustBeValidBoundedHexThatFitsTheRead()
    {
        foreach (var delimiter in new[] { null, "", "A", "GG", "00 01", new string('A', 130) })
            Assert.Equal("INVALID_FRAMING", Assert.Throws<PortsterException>(() => new ReadOptions { Mode = CompletionMode.Delimiter, DelimiterHex = delimiter }.Validate(Fixtures.Policy)).Code);
        Assert.Throws<PortsterException>(() => new ReadOptions { Mode = CompletionMode.Delimiter, DelimiterHex = "AABB", MaxBytes = 1 }.Validate(Fixtures.Policy));
        Assert.Equal(new byte[] { 0, 255 }, new ReadOptions { Mode = CompletionMode.Delimiter, DelimiterHex = "00ff", MaxBytes = 2 }.Validate(Fixtures.Policy));
    }
    [Fact]
    public void ReadLimitsAndFrameRequirementsRejectInvalidExtremes()
    {
        foreach (var options in new[]
        {
            new ReadOptions { Mode = (CompletionMode)999 }, new() { MaxBytes = 0 }, new() { MaxBytes = int.MaxValue },
            new() { WaitMs = int.MaxValue }, new() { Mode = CompletionMode.Length },
            new() { Mode = CompletionMode.Length, Length = 0 }, new() { Mode = CompletionMode.IdleGap, IdleGapMs = 19 },
            new() { Mode = CompletionMode.IdleGap, IdleGapMs = 10001 }
        }) Assert.Throws<PortsterException>(() => options.Validate(Fixtures.Policy));
        new ReadOptions { Mode = CompletionMode.Length, MaxBytes = 1, Length = 1, WaitMs = 0 }.Validate(Fixtures.Policy);
    }
    [Fact]
    public void SelectorAndSecretReferenceValidationRejectsTrailingNewlines()
    {
        using var temp = new TemporaryStore(); var profiles = new ProfileStore(temp.Paths, Fixtures.Policy);
        Assert.Equal("INVALID_PROFILE_ID", Assert.Throws<PortsterException>(() => profiles.Validate(Fixtures.Profile with { Id = "board\n" })).Code);
        Assert.Equal("INVALID_SELECTOR", Assert.Throws<PortsterException>(() => ProfileStore.ValidateSelector(new() { Vid = "0403\n", Pid = "6001" })).Code);
        Assert.Equal("INVALID_SELECTOR", Assert.Throws<PortsterException>(() => ProfileStore.ValidateSelector(new() { Vid = "0403", Pid = "6001", InterfaceNumber = "00\n" })).Code);
        Assert.Equal("INVALID_SECRET_REFERENCE", Assert.Throws<PortsterException>(() => profiles.Validate(Fixtures.Profile with { SecretReferences = new() { ["key"] = "env:TOKEN\n" } })).Code);
    }
    [Fact]
    public void InvalidSelectorCombinationsCannotFallBackToAPortHint()
    {
        foreach (var selector in new[]
        {
            new DeviceSelector(), new() { Vid = "0403", LastSeenPort = "COM7" }, new() { Pid = "6001" },
            new() { Vid = "XYZ1", Pid = "6001" }, new() { UsbSerialNumber = " " },
            new() { UsbSerialNumber = new string('A', 257) }, new() { LastSeenPort = "COM0" }
        }) Assert.Equal("INVALID_SELECTOR", Assert.Throws<PortsterException>(() => ProfileStore.ValidateSelector(selector)).Code);
    }
    [Fact]
    public void ExplicitPortAndCompositeInterfaceSelectionRespectEverySpecifiedField()
    {
        PortInfo[] ports = [new("COM7", "0403", "6001", "AAA", "00"), new("COM8", "0403", "6001", "AAA", "01")];
        Assert.Equal("COM7", ProfileStore.Resolve(new() { LastSeenPort = "com7" }, ports).PortName);
        Assert.Equal("COM8", ProfileStore.Resolve(new() { Vid = "0403", Pid = "6001", UsbSerialNumber = "AAA", InterfaceNumber = "01" }, ports).PortName);
        Assert.Throws<PortsterException>(() => ProfileStore.Resolve(new() { Vid = "0403", Pid = "6002", UsbSerialNumber = "AAA" }, ports));
    }
}
