using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CJora.Transactions.Domain.Ledger;

namespace CJora.Transactions.Application.Finance;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AmountRequest(string Currency,string MinorUnits);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTransferIntentRequest(Guid SourceAccountUuid,Guid DestinationAccountUuid,AmountRequest Amount,string? Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmTransferIntentRequest(int ReviewVersion);

public enum FinancialOperation { PrepareTransfer=1,ConfirmTransfer=2 }
public sealed record FinancialSessionScope(Guid ProgramUuid,Guid CustomerUuid,Guid UserUuid,Guid CoreSessionUuid,Guid AuthSessionUuid)
{
    public bool HasIdentifiers=>ProgramUuid!=Guid.Empty&&CustomerUuid!=Guid.Empty&&UserUuid!=Guid.Empty&&CoreSessionUuid!=Guid.Empty&&AuthSessionUuid!=Guid.Empty;
}
public sealed record FinancialRequestScope(Guid ProgramUuid,Guid CustomerUuid,FinancialOperation Operation,Guid Key)
{
    public static FinancialRequestScope Create(FinancialSessionScope session,FinancialOperation operation,string? key)
    {
        if(session is null||!session.HasIdentifiers||!Enum.IsDefined(operation)||key is null||
           !Guid.TryParseExact(key,"D",out var uuid)||uuid==Guid.Empty||uuid.ToString("D")!=key)
            throw new LedgerRuleViolation("INVALID_IDEMPOTENCY_SCOPE");
        return new(session.ProgramUuid,session.CustomerUuid,operation,uuid);
    }
}
/// <summary>Digest of canonical business terms; not an authorization credential.</summary>
public sealed class RequestFingerprint
{
    public string Hex { get; }
    public Guid ProgramUuid { get; }
    public Guid CustomerUuid { get; }
    public FinancialOperation Operation { get; }
    private RequestFingerprint(FinancialRequestScope scope,byte[] hash)
    {ProgramUuid=scope.ProgramUuid;CustomerUuid=scope.CustomerUuid;Operation=scope.Operation;Hex=Convert.ToHexString(hash);}
    public bool Matches(RequestFingerprint other)=>other is not null&&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hex),Convert.FromHexString(other.Hex));
    public static RequestFingerprint Prepare(FinancialRequestScope scope,CreateTransferIntentRequest request)
    {
        ValidateScope(scope,FinancialOperation.PrepareTransfer);
        if(request is null||request.SourceAccountUuid==Guid.Empty||request.DestinationAccountUuid==Guid.Empty||request.SourceAccountUuid==request.DestinationAccountUuid||request.Amount is null)
            throw new LedgerRuleViolation("INVALID_TRANSFER_REQUEST");
        var amount=Money.FromMinorUnits(request.Amount.Currency,request.Amount.MinorUnits);
        if(amount.MinorUnits==0)throw new LedgerRuleViolation("TRANSFER_AMOUNT_MUST_BE_POSITIVE");
        var reference=request.Reference??"";
        if(reference.Length>140||reference.Any(c=>c<' '||c>'~'))throw new LedgerRuleViolation("INVALID_TRANSFER_REFERENCE");
        return Write(scope,writer=>
        {
            Header(writer,scope);
            writer.WriteString("sourceAccountUuid",request.SourceAccountUuid);
            writer.WriteString("destinationAccountUuid",request.DestinationAccountUuid);
            writer.WriteString("currency",amount.Currency.Code);
            writer.WriteString("amountMinor",amount.MinorUnitsText);
            writer.WriteString("reference",reference);
        });
    }
    public static RequestFingerprint Confirm(FinancialRequestScope scope,Guid intent,ConfirmTransferIntentRequest request)
    {
        ValidateScope(scope,FinancialOperation.ConfirmTransfer);
        if(intent==Guid.Empty||request is null||request.ReviewVersion!=1)throw new LedgerRuleViolation("INVALID_REVIEW_VERSION");
        return Write(scope,writer=>{Header(writer,scope);writer.WriteString("intentUuid",intent);writer.WriteNumber("reviewVersion",request.ReviewVersion);});
    }
    private static void ValidateScope(FinancialRequestScope scope,FinancialOperation expected)
    {
        if(scope is null||scope.ProgramUuid==Guid.Empty||scope.CustomerUuid==Guid.Empty||scope.Key==Guid.Empty||scope.Operation!=expected)
            throw new LedgerRuleViolation("INVALID_IDEMPOTENCY_SCOPE");
    }
    private static void Header(Utf8JsonWriter writer,FinancialRequestScope scope)
    {
        writer.WriteNumber("contractVersion",1);
        writer.WriteString("programUuid",scope.ProgramUuid);
        writer.WriteString("customerUuid",scope.CustomerUuid);
        writer.WriteString("operation",scope.Operation==FinancialOperation.PrepareTransfer?"TRANSFER_INTENT.CREATE":"TRANSFER_INTENT.CONFIRM");
        // The key selects a scoped record; it is deliberately not part of its business payload fingerprint.
    }
    private static RequestFingerprint Write(FinancialRequestScope scope,Action<Utf8JsonWriter> fields)
    {
        var buffer=new ArrayBufferWriter<byte>();
        using(var writer=new Utf8JsonWriter(buffer)){writer.WriteStartObject();fields(writer);writer.WriteEndObject();}
        return new RequestFingerprint(scope,SHA256.HashData(buffer.WrittenSpan));
    }
}
public sealed record StoredFinancialRequest(FinancialRequestScope Scope,RequestFingerprint Fingerprint);
public enum IdempotencyDecision { Execute,Replay,Conflict }
public static class FinancialIdempotency
{
    public static IdempotencyDecision Decide(FinancialRequestScope request,RequestFingerprint fingerprint,StoredFinancialRequest? stored)
    {
        if(request is null||request.ProgramUuid==Guid.Empty||request.CustomerUuid==Guid.Empty||request.Key==Guid.Empty||!Enum.IsDefined(request.Operation)||fingerprint is null||fingerprint.ProgramUuid!=request.ProgramUuid||fingerprint.CustomerUuid!=request.CustomerUuid||fingerprint.Operation!=request.Operation)
            throw new LedgerRuleViolation("INVALID_IDEMPOTENCY_SCOPE");
        if(stored is null)return IdempotencyDecision.Execute;
        return stored.Scope==request&&stored.Fingerprint is not null&&stored.Fingerprint.Matches(fingerprint)?IdempotencyDecision.Replay:IdempotencyDecision.Conflict;
    }
}
