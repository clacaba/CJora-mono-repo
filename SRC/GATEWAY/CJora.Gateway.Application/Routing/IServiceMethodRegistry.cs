using CJora.Routing;

namespace CJora.Gateway.Application.Routing;

public interface IServiceMethodRegistry
{
    Task<ServiceMethod?> FindAsync(string service, string method, string requestType, CancellationToken cancellationToken);
}