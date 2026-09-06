namespace Portster;

internal sealed class OperationLease(Action release) : IDisposable
{
    public void Dispose() => release();
}
