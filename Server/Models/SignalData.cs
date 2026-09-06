namespace Portster;

public sealed record SignalData(bool Cts, bool Dsr, bool CarrierDetect, bool Dtr, bool? Rts, bool Break,
    bool RtsManagedByFlowControl = false);
