using CJora.Core.Infrastructure.Customers;
using CJora.CustomerEvents;
using CJora.Kafka;
using CJora.Routing;

namespace CJora.Core.Api;
public static class CustomerSynchronization
{
    public static void AddCustomerSynchronization(this WebApplicationBuilder builder)
    {
        if(!builder.Configuration.GetValue<bool>("CustomerSync:Enabled"))return;
        if(!builder.Environment.IsDevelopment())throw new InvalidOperationException("Local customer synchronization requires Development; production transport is not configured.");
        var connection=builder.Configuration.GetConnectionString("CoreOutbox")??throw new InvalidOperationException("Core outbox connection required.");
        builder.Services.AddSingleton<IEventOutbox>(new CustomerOutbox(connection));
        builder.Services.AddHostedService(p=>new OutboxPublisher(p.GetRequiredService<IEventOutbox>(),"localhost:9092",CustomerSnapshot.Topic,p.GetRequiredService<IRoutingErrorRecorder>()));
    }
}
