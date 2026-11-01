using CJora.CustomerEvents;
using CJora.Kafka;
using CJora.Routing;
using CJora.Routing.Persistence;
using CJora.Transactions.Application;
using CJora.Transactions.Infrastructure;

namespace CJora.Transactions.Api;
public static class CustomerSynchronization
{
    public static void AddCustomerSynchronization(this WebApplicationBuilder builder)
    {
        if(!builder.Configuration.GetValue<bool>("CustomerSync:Enabled"))return;
        if(!builder.Environment.IsDevelopment())throw new InvalidOperationException("Local customer synchronization requires Development; production transport is not configured.");
        var connection=builder.Configuration.GetConnectionString("TransactionsSync")??throw new InvalidOperationException("Transactions sync connection required.");
        builder.Services.AddSingleton<IRoutingErrorRecorder>(_=>new RoutingErrorRecorder(connection,
            Path.Combine(builder.Environment.ContentRootPath,".local","errors","customer-sync.jsonl"),"CJORA_TRANSACTIONS","Transactions"));
        builder.Services.AddSingleton<ICustomerSnapshotStore>(new CustomerSnapshotStore(connection));
        builder.Services.AddHostedService(p=>new SnapshotConsumer("localhost:9092",CustomerSnapshot.Topic,"cjora-transactions-customers-v1",
            p.GetRequiredService<ICustomerSnapshotStore>().ApplyAsync,p.GetRequiredService<IRoutingErrorRecorder>()));
    }
}
