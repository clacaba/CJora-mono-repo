using CJora.CustomerEvents;
namespace CJora.Transactions.Application;
public interface ICustomerSnapshotStore
{
    /// <summary>Returns only after projection/inbox/audit or permanent rejection are durably committed.</summary>
    Task ApplyAsync(string topic,int partition,long offset,string? key,byte[]? payload,CancellationToken cancellationToken);
}
