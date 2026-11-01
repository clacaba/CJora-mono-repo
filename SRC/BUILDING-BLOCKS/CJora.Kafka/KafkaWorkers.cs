using Confluent.Kafka;
using CJora.Routing;
using Microsoft.Extensions.Hosting;
using System.Text;

namespace CJora.Kafka;

public sealed record OutboundEvent(Guid EventUuid,Guid LeaseOwner,string Key,byte[] Payload);
public interface IEventOutbox
{
    Task<OutboundEvent?> ClaimAsync(CancellationToken ct);
    Task CompleteAsync(OutboundEvent message,bool published,CancellationToken ct);
}
public sealed class OutboxPublisher(IEventOutbox store,string bootstrap,string topic,IRoutingErrorRecorder errors):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        using var producer=new ProducerBuilder<string,byte[]>(new ProducerConfig
        {
            BootstrapServers=bootstrap,EnableIdempotence=true,Acks=Acks.All,MessageTimeoutMs=10000,RequestTimeoutMs=5000,AllowAutoCreateTopics=false,ClientId="cjora-core-outbox"
        }).SetLogHandler((_,_)=>{}).Build();
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var message=await store.ClaimAsync(ct);
                if(message is null){await Task.Delay(1000,ct);continue;}
                bool published;
                try
                {
                    await producer.ProduceAsync(topic,new Message<string,byte[]>{Key=message.Key,Value=message.Payload},ct);
                    published=true;
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
                catch {published=false;await errors.RecordAsync(new("CUSTOMER_PUBLISH_FAILED","DEPENDENCY",message.EventUuid.ToString("D"),null,"Customer snapshot delivery failed; payload excluded."));}
                await store.CompleteAsync(message,published,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch
            {
                await errors.RecordAsync(new("CUSTOMER_OUTBOX_UNAVAILABLE","DEPENDENCY",Guid.NewGuid().ToString("D"),null,"Customer outbox unavailable."));
                await Task.Delay(2000,ct);
            }
        }
    }
}
public sealed class SnapshotConsumer(string bootstrap,string topic,string group,
    Func<string,int,long,string?,byte[]?,CancellationToken,Task> apply,IRoutingErrorRecorder errors):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while(!ct.IsCancellationRequested)
        {
            using var consumer=new ConsumerBuilder<byte[],byte[]>(new ConsumerConfig
            {
                BootstrapServers=bootstrap,GroupId=group,EnableAutoCommit=false,EnableAutoOffsetStore=false,
                AutoOffsetReset=AutoOffsetReset.Earliest,AllowAutoCreateTopics=false,MaxPollIntervalMs=300000,
                SessionTimeoutMs=10000,ClientId="cjora-transactions-customers",FetchMaxBytes=1048576,MaxPartitionFetchBytes=1048576
            }).SetLogHandler((_,_)=>{}).Build();
            try
            {
                consumer.Subscribe(topic);
                while(!ct.IsCancellationRequested)
                {
                    var message=consumer.Consume(ct);
                    string? key=null;
                    try{if(message.Message.Key is {} bytes)key=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){}
                    // A failure exits/recreates the consumer. Never consume the next record after a failed database write.
                    await apply(message.Topic,message.Partition.Value,message.Offset.Value,key,message.Message.Value,ct);
                    consumer.Commit(message);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch
            {
                await errors.RecordAsync(new("CUSTOMER_CONSUME_RETRY","DEPENDENCY",Guid.NewGuid().ToString("D"),null,"Customer consumption failed; offset not acknowledged."));
                await Task.Delay(2000,ct);
            }
            finally {try{consumer.Close();}catch(KafkaException){}}
        }
    }
}
