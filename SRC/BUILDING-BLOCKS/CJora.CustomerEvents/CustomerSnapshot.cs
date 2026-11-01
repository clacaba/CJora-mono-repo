using System.Text.Json;
using System.Text.Json.Serialization;

namespace CJora.CustomerEvents;

/// <summary>Versioned, minimal full snapshot. No contact information or authentication credentials.</summary>
public sealed record CustomerSnapshot(Guid EventUuid, Guid OperationUuid, int SchemaVersion, string EventType,
    Guid CustomerUuid, Guid ProgramUuid, Guid UserUuid, long SourceVersion, string Status, DateTime OccurredAtUtc)
{
    public const string Topic = "cjora.core.customer-snapshots.v1";
    public const int MaximumBytes = 4096;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string Key => CustomerUuid.ToString("D");
    public bool IsValid() => EventUuid != Guid.Empty && OperationUuid != Guid.Empty && SchemaVersion == 1 &&
        EventType == "core.customer.snapshot.v1" && CustomerUuid != Guid.Empty && ProgramUuid != Guid.Empty &&
        UserUuid != Guid.Empty && SourceVersion > 0 && Status is "ACTIVE" or "RESTRICTED" &&
        OccurredAtUtc.Kind == DateTimeKind.Utc && OccurredAtUtc >= new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc) &&
        OccurredAtUtc <= DateTime.UtcNow.AddMinutes(5);
    public static CustomerSnapshot Parse(string? key, byte[]? payload)
    {
        if (payload is null || payload.Length > MaximumBytes) throw new InvalidCustomerEvent("INVALID_ENVELOPE");
        CustomerSnapshot snapshot;
        try { snapshot = JsonSerializer.Deserialize<CustomerSnapshot>(payload,Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidCustomerEvent("INVALID_ENVELOPE"); }
        if (!snapshot.IsValid() || key != snapshot.Key) throw new InvalidCustomerEvent("INVALID_CUSTOMER_EVENT");
        return snapshot;
    }
}
public sealed class InvalidCustomerEvent(string code) : Exception("Customer synchronization event rejected.")
{ public string Code { get; } = code; }
