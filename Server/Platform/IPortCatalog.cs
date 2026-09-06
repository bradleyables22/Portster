namespace Portster.Platform;

public interface IPortCatalog
{
    Task<IReadOnlyList<PortInfo>> ListAsync(CancellationToken cancellationToken);
}
