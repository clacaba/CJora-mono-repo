using CJora.Routing;
using CJora.Routing.Persistence;
using CJora.Core.Application.Customers;
using CJora.Core.Infrastructure.Customers;
using System.Text.Json;

if(args.Length!=1 || args[0] is not ("--save" or "--reconcile"))
{Console.Error.WriteLine("Use --save with a CustomerChange JSON command on stdin, or --reconcile. Development operator tool; no public write endpoint.");return 2;}
var connection=Environment.GetEnvironmentVariable("CJORA_CORE_CUSTOMER_CONNECTION");
if(string.IsNullOrWhiteSpace(connection)){Console.Error.WriteLine("Core operator connection is required via environment; no credentials displayed.");return 2;}
try
{
    var writer=new CustomerWriter(connection);
    if(args[0]=="--reconcile")Console.WriteLine($"Queued snapshots: {await writer.ReconcileAsync(default)}");
    else
    {
        var command=await JsonSerializer.DeserializeAsync<CustomerChange>(Console.OpenStandardInput(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(command is null)throw new CustomerWriteRejected("INVALID_COMMAND");
        Console.WriteLine($"Customer source version: {await writer.SaveAsync(command,default)}");
    }
    return 0;
}
catch(CustomerWriteRejected e){await Record(e.Code);Console.Error.WriteLine(e.Code);return 1;}
catch{await Record("CUSTOMER_WRITE_UNAVAILABLE");Console.Error.WriteLine("Customer operation failed. Database details and command content suppressed.");return 1;}

async Task Record(string code)
{
    try
    {
        await new RoutingErrorRecorder(connection,Path.Combine(Environment.CurrentDirectory,".local","errors","customer-operator.jsonl"))
            .RecordAsync(new RoutingError(code,"CUSTOMER_COMMAND",Guid.NewGuid().ToString("D"),null,"Customer operator command failed; input excluded."));
    }
    catch {Console.Error.WriteLine("Customer error persistence failed; inspect local diagnostic storage.");}
}