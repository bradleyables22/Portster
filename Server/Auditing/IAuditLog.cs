namespace Portster;

public interface IAuditLog
{
    void Record(OperationContext operation, string phase, string outcome);
}
