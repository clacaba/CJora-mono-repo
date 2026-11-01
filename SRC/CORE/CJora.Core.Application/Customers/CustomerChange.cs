namespace CJora.Core.Application.Customers;
public sealed record CustomerChange(Guid CustomerUuid,Guid ProgramUuid,Guid UserUuid,string DisplayName,string Email,string Status,long ExpectedVersion,Guid OperationUuid)
{
    public bool IsValid()=>CustomerUuid!=Guid.Empty&&ProgramUuid!=Guid.Empty&&UserUuid!=Guid.Empty&&OperationUuid!=Guid.Empty&&
        !string.IsNullOrWhiteSpace(DisplayName)&&DisplayName.Length<=160&&!string.IsNullOrWhiteSpace(Email)&&Email.Length<=254&&
        System.Net.Mail.MailAddress.TryCreate(Email,out var address)&&address.Address==Email&&Status is "ACTIVE" or "RESTRICTED"&&ExpectedVersion>=0;
}
public interface ICustomerWriter
{
    Task<long> SaveAsync(CustomerChange change,CancellationToken cancellationToken);
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}
public sealed class CustomerWriteRejected(string code):Exception("Customer change rejected."){public string Code{get;}=code;}
