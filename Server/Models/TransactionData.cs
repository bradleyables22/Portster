namespace Portster;

public sealed record TransactionData(WriteData Write, ReadData Read,
    string Correlation = "Framing only; unsolicited or delayed data may be included. No automatic retry.");
