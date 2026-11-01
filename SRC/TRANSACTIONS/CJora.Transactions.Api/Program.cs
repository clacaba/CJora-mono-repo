using CJora.Transactions.Api.Finance;
using CJora.Transactions.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


builder.AddCustomerSynchronization();
builder.AddTransactionsFinance();
var app = builder.Build();
app.UseTransactionsFinance();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapControllers();

app.Run();